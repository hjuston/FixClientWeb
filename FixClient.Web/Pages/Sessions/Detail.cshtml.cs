using FixClient.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages.Sessions;

public class DetailModel : PageModel
{
    readonly FixSessionManager _manager;

    public DetailModel(FixSessionManager manager)
    {
        _manager = manager;
    }

    public string SessionId { get; set; } = "";
    public string SenderCompId { get; set; } = "";
    public string TargetCompId { get; set; } = "";
    public string BeginString { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int HeartBtInt { get; set; }
    public string Behaviour { get; set; } = "";
    public string State { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Found { get; set; }

    public List<HistoryEntry> HistoryEntries { get; set; } = [];
    public List<LogEntry> LogEntries { get; set; } = [];
    public List<OrderDisplayInfo> Orders { get; set; } = [];

    public IActionResult OnGet(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return RedirectToPage("Index");

        var info = _manager.GetSession(sessionId);
        if (info == null)
            return RedirectToPage("Index");

        Found = true;
        SessionId = info.Id;
        SenderCompId = info.SenderCompId;
        TargetCompId = info.TargetCompId;
        BeginString = info.BeginString;
        Host = info.Host;
        Port = info.Port;
        HeartBtInt = info.HeartBtInt;
        Behaviour = info.Behaviour.ToString();
        State = info.SessionState.ToString();
        Enabled = info.Enabled;

        HistoryEntries = info.HistoryEntries.TakeLast(500).Reverse().ToList();
        LogEntries = info.LogEntries.TakeLast(500).Reverse().ToList();

        foreach (var order in info.OrderBook.Orders)
        {
            Orders.Add(MapOrder(order));
        }

        if (Orders.Count == 0)
        {
            var replayBook = RebuildFromHistory(info.HistoryEntries);
            foreach (var order in replayBook.Orders)
            {
                Orders.Add(MapOrder(order));
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync(string sessionId)
    {
        await _manager.ConnectAsync(sessionId);
        return RedirectToPage(new { sessionId });
    }

    public IActionResult OnPostDisable(string sessionId)
    {
        _manager.Disconnect(sessionId);
        return RedirectToPage(new { sessionId });
    }

    static Fix.OrderBook RebuildFromHistory(IEnumerable<HistoryEntry> entries)
    {
        var replayBook = new Fix.OrderBook();
        foreach (var entry in entries.OrderBy(h => h.Timestamp))
        {
            if (entry.Direction != "Incoming" || string.IsNullOrWhiteSpace(entry.Raw))
                continue;

            try
            {
                var message = new Fix.Message(entry.Raw.Replace('|', '\x01'));
                replayBook.Process(message);
            }
            catch
            {
            }
        }
        return replayBook;
    }

    static OrderDisplayInfo MapOrder(Fix.Order order)
    {
        var ordStatusValue = order.OrdStatus?.Value ?? "";
        var isPending = order.OrdStatus == null || ordStatusValue == "A";
        var isActive = ordStatusValue is "0" or "1" or "A";
        var isPendingCancel = ordStatusValue == "6";

        return new OrderDisplayInfo(
            order.SenderCompID,
            order.TargetCompID,
            order.ClOrdID,
            order.Symbol,
            order.Side?.ToString() ?? "",
            order.OrderQty,
            order.Price?.ToString("F4") ?? "",
            order.OrdStatus?.ToString() ?? "Pending",
            order.CumQty?.ToString() ?? "0",
            order.AvgPx?.ToString("F4") ?? "",
            (order.LeavesQty ?? order.OrderQty).ToString(),
            order.SendingTime.ToString("yyyy-MM-dd HH:mm:ss"),
            order.Active,
            order.Messages.Count,
            GetStatusBadge(order.OrdStatus?.Value),
            isPending,
            isActive,
            isPendingCancel);
    }

    static string GetStatusBadge(string? status) => status switch
    {
        "0" => "badge-blue",
        "1" => "badge-amber",
        "2" => "badge-green",
        "4" => "badge-red",
        "6" => "badge-amber",
        "8" => "badge-red",
        null => "badge-gray",
        _ => "badge-gray"
    };
}
