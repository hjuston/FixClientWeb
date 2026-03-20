using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class OrdersIndexModel : PageModel
{
    [BindProperty]
    public string? RawInput { get; set; }

    public List<OrderDisplayInfo> Orders { get; set; } = [];

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
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
            Orders.Add(new OrderDisplayInfo(
                order.SenderCompID,
                order.TargetCompID,
                order.ClOrdID,
                order.Symbol,
                order.Side?.ToString() ?? "",
                order.OrderQty,
                order.Price?.ToString("F4") ?? "",
                order.OrdStatus?.ToString() ?? "New",
                order.CumQty?.ToString() ?? "0",
                order.AvgPx?.ToString("F4") ?? "",
                order.SendingTime.ToString("yyyy-MM-dd HH:mm:ss"),
                order.Active,
                order.Messages.Count,
                GetStatusBadge(order.OrdStatus?.Value)
            ));
        }

        return Page();
    }

    static string GetStatusBadge(string? status) => status switch
    {
        "0" => "badge-blue",
        "1" => "badge-amber",
        "2" => "badge-green",
        "4" => "badge-red",
        "8" => "badge-red",
        _ => "badge-gray"
    };
}

public record OrderDisplayInfo(
    string SenderCompID, string TargetCompID, string ClOrdID, string Symbol,
    string Side, long OrderQty, string Price, string OrdStatus,
    string CumQty, string AvgPx, string SendingTime, bool Active,
    int MessageCount, string StatusBadge);
