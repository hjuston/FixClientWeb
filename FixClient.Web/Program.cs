using Fix;
using System.Text;
using Microsoft.AspNetCore.Http;
using FixClient.Web.Services;
using FixClient.Web.Hubs;
using FixClient.Web.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSignalR();

var connectionString = builder.Configuration.GetConnectionString("FixClientWeb")
    ?? "Host=localhost;Port=5432;Database=fixclientweb;Username=postgres;Password=postgres";

builder.Services.AddDbContextFactory<FixClientWebDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton<SessionPersistenceStore>();
builder.Services.AddSingleton<FixSessionManager>();

var app = builder.Build();

var persistenceStore = app.Services.GetRequiredService<SessionPersistenceStore>();
await persistenceStore.EnsureCreatedAsync();

// Wire up SignalR broadcast from FixSessionManager events
var sessionManager = app.Services.GetRequiredService<FixSessionManager>();
var hubContext = app.Services.GetRequiredService<IHubContext<SessionHub>>();

sessionManager.MessageReceived += (sessionId, entry) =>
    hubContext.Clients.Group(sessionId).SendAsync("MessageReceived", sessionId, entry);
sessionManager.MessageSent += (sessionId, entry) =>
    hubContext.Clients.Group(sessionId).SendAsync("MessageSent", sessionId, entry);
sessionManager.LogAdded += (sessionId, entry) =>
    hubContext.Clients.Group(sessionId).SendAsync("LogAdded", sessionId, entry);
sessionManager.StateChanged += (sessionId, state) =>
    hubContext.Clients.Group(sessionId).SendAsync("StateChanged", sessionId, state.ToString());

app.UseStaticFiles();
app.MapRazorPages();
app.MapHub<SessionHub>("/hubs/session");

// --- API: Versions ---
app.MapGet("/api/versions", () =>
{
    var versions = Dictionary.Versions
        .Select(v => new { v.BeginString, MessageCount = v.Messages.Count() })
        .ToList();
    return Results.Ok(versions);
});

// --- API: Messages for a version ---
app.MapGet("/api/versions/{beginString}/messages", (string beginString) =>
{
    var version = Dictionary.Versions[beginString];
    if (version == null) return Results.NotFound();

    var messages = version.Messages
        .OrderBy(m => m.Name)
        .Select(m => new { m.MsgType, m.Name, m.Description, Pedigree = m.Pedigree.ToString() })
        .ToList();
    return Results.Ok(messages);
});

// --- API: Message detail ---
app.MapGet("/api/versions/{beginString}/messages/{msgType}", (string beginString, string msgType) =>
{
    var version = Dictionary.Versions[beginString];
    if (version == null) return Results.NotFound();

    var message = version.Messages[msgType];
    if (message == null) return Results.NotFound();

    var fields = message.Fields
        .Select(f => new { f.Tag, f.Name, f.Required, f.Depth, f.Description })
        .ToList();

    return Results.Ok(new { message.MsgType, message.Name, message.Description, Pedigree = message.Pedigree.ToString(), Fields = fields });
});

// --- API: Fields for a version ---
app.MapGet("/api/versions/{beginString}/fields", (string beginString) =>
{
    var version = Dictionary.Versions[beginString];
    if (version == null) return Results.NotFound();

    var fields = version.Fields
        .Where(f => f.IsValid)
        .OrderBy(f => f.Tag)
        .Select(f => new
        {
            f.Tag,
            f.Name,
            f.DataType,
            f.Description,
            Values = f.Values.Values.Select(v => new { v.Value, v.Name, v.Description }).ToList()
        })
        .ToList();
    return Results.Ok(fields);
});

// --- API: Parse FIX messages ---
app.MapPost("/api/parser/parse", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var input = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(input))
        return Results.BadRequest("No input provided");

    input = input.Replace('|', '\x01').Replace('^', '\x01');

    using var stream = new MemoryStream(Encoding.ASCII.GetBytes(input));
    var messages = new List<object>();

    await foreach (var message in Parser.Parse(stream))
    {
        var description = message.Describe();
        var fields = description.Fields.Select(f => new
        {
            f.Tag,
            f.Name,
            f.Value,
            ValueDescription = f.ValueDefinition is Dictionary.FieldValue fv ? fv.Name : null
        }).ToList();

        messages.Add(new
        {
            description.MsgTypeDescription,
            message.Incoming,
            Fields = fields
        });
    }

    return Results.Ok(messages);
});

// --- API: Session management ---
app.MapPost("/api/sessions", (HttpRequest request) =>
{
    var form = request.Form;
    var sessionInfo = new FixSessionInfo
    {
        SenderCompId = form["senderCompId"].ToString(),
        TargetCompId = form["targetCompId"].ToString(),
        Host = form["host"].ToString(),
        Port = int.TryParse(form["port"], out var p) ? p : 9810,
        BeginString = form["beginString"].ToString(),
        HeartBtInt = int.TryParse(form["heartBtInt"], out var h) ? h : 30,
        Behaviour = form["behaviour"].ToString() == "Acceptor" ? Fix.Behaviour.Acceptor : Fix.Behaviour.Initiator
    };
    var info = sessionManager.CreateSession(sessionInfo);
    return Results.Ok(new { info.Id });
});

app.MapGet("/api/sessions", () =>
{
    var sessions = sessionManager.Sessions.Values.Select(s => new
    {
        s.Id,
        s.SenderCompId,
        s.TargetCompId,
        s.Host,
        s.Port,
        s.BeginString,
        State = s.SessionState.ToString(),
        s.Behaviour,
        HistoryCount = s.HistoryEntries.Count,
        LogCount = s.LogEntries.Count
    });
    return Results.Ok(sessions);
});

app.MapGet("/api/sessions/{id}/history", (string id) =>
{
    var info = sessionManager.GetSession(id);
    if (info == null) return Results.NotFound();
    return Results.Ok(info.HistoryEntries.TakeLast(500));
});

app.MapGet("/api/sessions/{id}/log", (string id) =>
{
    var info = sessionManager.GetSession(id);
    if (info == null) return Results.NotFound();
    return Results.Ok(info.LogEntries.TakeLast(500));
});

app.MapDelete("/api/sessions/{id}", (string id) =>
{
    return sessionManager.RemoveSession(id) ? Results.Ok() : Results.NotFound();
});

// --- API: Orders for a session ---
app.MapGet("/api/sessions/{id}/orders", (string id) =>
{
    var info = sessionManager.GetSession(id);
    if (info == null) return Results.NotFound();

    var orders = info.OrderBook.Orders.Select(o => new
    {
        o.ClOrdID,
        o.Symbol,
        Side = o.Side?.ToString() ?? "",
        o.OrderQty,
        Price = o.Price?.ToString("F4") ?? "",
        OrdStatus = o.OrdStatus?.ToString() ?? "Pending",
        OrdStatusValue = o.OrdStatus?.Value ?? "",
        o.OrderID,
        CumQty = o.CumQty ?? 0,
        AvgPx = o.AvgPx ?? 0m,
        LeavesQty = o.LeavesQty ?? o.OrderQty,
        o.Active,
        o.SenderCompID,
        o.TargetCompID,
        MessageCount = o.Messages.Count
    });
    return Results.Ok(orders);
});

// --- API: Acknowledge order ---
app.MapPost("/api/sessions/{id}/orders/{clOrdId}/acknowledge", (string id, string clOrdId) =>
{
    static string Error(FixSessionManager mgr, string sessionId, string fallback)
    {
        var session = mgr.GetSession(sessionId);
        return session?.LogEntries.LastOrDefault(l => l.Level == "Error")?.Message ?? fallback;
    }

    return sessionManager.AcknowledgeOrder(id, clOrdId)
        ? Results.Ok(new { status = "acknowledged" })
        : Results.BadRequest(new { error = Error(sessionManager, id, "Failed to acknowledge order") });
});

// --- API: Reject order ---
app.MapPost("/api/sessions/{id}/orders/{clOrdId}/reject", async (string id, string clOrdId, HttpRequest request) =>
{
    string? reason = null;
    if (request.ContentLength > 0)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        reason = await reader.ReadToEndAsync();
    }

    static string Error(FixSessionManager mgr, string sessionId, string fallback)
    {
        var session = mgr.GetSession(sessionId);
        return session?.LogEntries.LastOrDefault(l => l.Level == "Error")?.Message ?? fallback;
    }

    return sessionManager.RejectOrder(id, clOrdId, reason)
        ? Results.Ok(new { status = "rejected" })
        : Results.BadRequest(new { error = Error(sessionManager, id, "Failed to reject order") });
});

// --- API: Fill order ---
app.MapPost("/api/sessions/{id}/orders/{clOrdId}/fill", (string id, string clOrdId, long? qty, decimal? price) =>
{
    static string Error(FixSessionManager mgr, string sessionId, string fallback)
    {
        var session = mgr.GetSession(sessionId);
        return session?.LogEntries.LastOrDefault(l => l.Level == "Error")?.Message ?? fallback;
    }

    return sessionManager.FillOrder(id, clOrdId, qty, price)
        ? Results.Ok(new { status = "filled" })
        : Results.BadRequest(new { error = Error(sessionManager, id, "Failed to fill order") });
});

// --- API: Cancel order ---
app.MapPost("/api/sessions/{id}/orders/{clOrdId}/cancel", (string id, string clOrdId) =>
{
    static string Error(FixSessionManager mgr, string sessionId, string fallback)
    {
        var session = mgr.GetSession(sessionId);
        return session?.LogEntries.LastOrDefault(l => l.Level == "Error")?.Message ?? fallback;
    }

    return sessionManager.CancelOrder(id, clOrdId)
        ? Results.Ok(new { status = "cancelled" })
        : Results.BadRequest(new { error = Error(sessionManager, id, "Failed to cancel order") });
});

// --- API: Reject cancel request ---
app.MapPost("/api/sessions/{id}/orders/{clOrdId}/reject-cancel", async (string id, string clOrdId, HttpRequest request) =>
{
    string? reason = null;
    if (request.ContentLength > 0)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        reason = await reader.ReadToEndAsync();
    }

    static string Error(FixSessionManager mgr, string sessionId, string fallback)
    {
        var session = mgr.GetSession(sessionId);
        return session?.LogEntries.LastOrDefault(l => l.Level == "Error")?.Message ?? fallback;
    }

    return sessionManager.RejectCancelRequest(id, clOrdId, reason)
        ? Results.Ok(new { status = "cancel-rejected" })
        : Results.BadRequest(new { error = Error(sessionManager, id, "Failed to reject cancel request") });
});

app.Run();
