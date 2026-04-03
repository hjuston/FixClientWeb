using System.Text;
using System.Linq;
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

        if (info.OrderBook.Orders.Count > 0)
        {
            foreach (var order in info.OrderBook.Orders)
            {
                Orders.Add(MapOrder(order));
            }
            return;
        }

        var replayBook = RebuildFromHistory(info.HistoryEntries);

        if (replayBook.Orders.Count == 0)
        {
            Orders.AddRange(ExtractOrdersFromHistory(info.HistoryEntries));
            return;
        }

        foreach (var order in replayBook.Orders)
        {
            Orders.Add(MapOrder(order));
        }
    }

    void LoadAvailableSessions()
    {
        foreach (var s in _manager.Sessions.Values)
        {
            var orderCount = s.OrderBook.Orders.Count;
            if (orderCount == 0)
            {
                var replayCount = RebuildFromHistory(s.HistoryEntries).Orders.Count;
                orderCount = replayCount > 0 ? replayCount : ExtractOrdersFromHistory(s.HistoryEntries).Count;
            }

            AvailableSessions.Add(new SessionSummary(
                s.Id, s.SenderCompId, s.TargetCompId,
                s.Behaviour.ToString(), orderCount));
        }
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

    static List<OrderDisplayInfo> ExtractOrdersFromHistory(IEnumerable<HistoryEntry> entries)
    {
        var result = new Dictionary<string, OrderDisplayInfo>();

        foreach (var entry in entries.Where(e => e.Direction == "Incoming").OrderBy(e => e.Timestamp))
        {
            if (string.IsNullOrWhiteSpace(entry.Raw))
                continue;

            try
            {
                var message = new Fix.Message(entry.Raw.Replace('|', '\x01'));
                var clOrdId = message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID)?.Value;
                if (string.IsNullOrWhiteSpace(clOrdId))
                    continue;

                if (message.MsgType == Fix.Dictionary.FIX_5_0SP2.Messages.NewOrderSingle.MsgType)
                {
                    var symbol = message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol)?.Value
                        ?? message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.SecurityID)?.Value
                        ?? "";
                    var side = message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Side)?.Value ?? "";
                    var qty = long.TryParse(message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty)?.Value, out var q) ? q : 0;
                    var price = message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Price)?.Value ?? "";

                    result[clOrdId] = new OrderDisplayInfo(
                        message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.SenderCompID)?.Value ?? "",
                        message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.TargetCompID)?.Value ?? "",
                        clOrdId,
                        symbol,
                        side,
                        qty,
                        price,
                        "Pending",
                        "0",
                        "",
                        qty.ToString(),
                        entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                        true,
                        1,
                        GetStatusBadge(null),
                        true,
                        true,
                        false);
                    continue;
                }

                if (message.MsgType == Fix.Dictionary.FIX_5_0SP2.Messages.ExecutionReport.MsgType && result.TryGetValue(clOrdId, out var existing))
                {
                    var ordStatus = message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.OrdStatus)?.Value;
                    var cumQty = message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.CumQty)?.Value ?? existing.CumQty;
                    var avgPx = message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.AvgPx)?.Value ?? existing.AvgPx;
                    var leavesQty = message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.LeavesQty)?.Value ?? existing.LeavesQty;

                    result[clOrdId] = existing with
                    {
                        OrdStatus = ordStatus ?? existing.OrdStatus,
                        CumQty = cumQty,
                        AvgPx = avgPx,
                        LeavesQty = leavesQty,
                        StatusBadge = GetStatusBadge(ordStatus),
                        IsPending = string.IsNullOrEmpty(ordStatus) || ordStatus == "A",
                        IsActive = ordStatus is "0" or "1" or "A",
                        IsPendingCancel = ordStatus == "6",
                        Active = ordStatus is "0" or "1" or "A",
                        MessageCount = existing.MessageCount + 1
                    };
                }
            }
            catch
            {
            }
        }

        return result.Values.OrderByDescending(o => o.SendingTime).ToList();
    }

    static OrderDisplayInfo MapOrder(Fix.Order order)
    {
        var ordStatusValue = order.OrdStatus?.Value ?? "";
        var isPending = order.OrdStatus == null || ordStatusValue == "A"; // PendingNew
        var isActive = ordStatusValue == "0" || ordStatusValue == "1" || ordStatusValue == "A"; // New/PartiallyFilled/PendingNew
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
