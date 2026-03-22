using System.Text;
using FixClient.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class OrdersIndexModel : PageModel
{
    readonly FixSessionManager _manager;

    public OrdersIndexModel(FixSessionManager manager)
    {
        _manager = manager;
    }

    [BindProperty]
    public string? RawInput { get; set; }

    public string? SessionId { get; set; }
    public List<OrderDisplayInfo> Orders { get; set; } = [];
    public List<SessionSummary> AvailableSessions { get; set; } = [];

    public void OnGet(string? sessionId)
    {
        SessionId = sessionId;

        if (sessionId != null)
        {
            LoadSessionOrders(sessionId);
        }
        else
        {
            LoadAvailableSessions();
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        LoadAvailableSessions();

        if (string.IsNullOrWhiteSpace(RawInput))
            return Page();

        var input = RawInput
            .Replace('|', '\x01')
            .Replace('^', '\x01');

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(input));
        var orderBook = new Fix.OrderBook();

        await foreach (var message in Fix.Parser.Parse(stream))
        {
            orderBook.Process(message);
        }

        foreach (var order in orderBook.Orders)
        {
            Orders.Add(MapOrder(order));
        }

        return Page();
    }

    void LoadSessionOrders(string sessionId)
    {
        var info = _manager.GetSession(sessionId);
        if (info == null) return;

        foreach (var order in info.OrderBook.Orders)
        {
            Orders.Add(MapOrder(order));
        }
    }

    void LoadAvailableSessions()
    {
        foreach (var s in _manager.Sessions.Values)
        {
            AvailableSessions.Add(new SessionSummary(
                s.Id, s.SenderCompId, s.TargetCompId,
                s.Behaviour.ToString(), s.OrderBook.Orders.Count));
        }
    }

    static OrderDisplayInfo MapOrder(Fix.Order order)
    {
        var ordStatusValue = order.OrdStatus?.Value ?? "";
        var isPending = order.OrdStatus == null;
        var isActive = ordStatusValue == "0" || ordStatusValue == "1"; // New or PartiallyFilled
        var isPendingCancel = ordStatusValue == "6"; // PendingCancel

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
            isPendingCancel
        );
    }

    static string GetStatusBadge(string? status) => status switch
    {
        "0" => "badge-blue",     // New
        "1" => "badge-amber",    // PartiallyFilled
        "2" => "badge-green",    // Filled
        "4" => "badge-red",      // Canceled
        "6" => "badge-amber",    // PendingCancel
        "8" => "badge-red",      // Rejected
        null => "badge-gray",    // Pending (no ack yet)
        _ => "badge-gray"
    };
}

public record OrderDisplayInfo(
    string SenderCompID, string TargetCompID, string ClOrdID, string Symbol,
    string Side, long OrderQty, string Price, string OrdStatus,
    string CumQty, string AvgPx, string LeavesQty, string SendingTime, bool Active,
    int MessageCount, string StatusBadge, bool IsPending, bool IsActive, bool IsPendingCancel);

public record SessionSummary(string Id, string SenderCompId, string TargetCompId, string Behaviour, int OrderCount);
