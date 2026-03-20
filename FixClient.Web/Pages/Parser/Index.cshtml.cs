using System.Text;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class ParserIndexModel : PageModel
{
    [BindProperty]
    public string? RawInput { get; set; }

    [BindProperty]
    public bool ShowAdminMessages { get; set; } = true;

    public List<string> ParsedMessages { get; set; } = [];
    public List<OrderInfo> Orders { get; set; } = [];

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
            if (!ShowAdminMessages && message.Administrative)
                continue;

            ParsedMessages.Add(FormatMessage(message));
            orderBook.Process(message);
        }

        foreach (var order in orderBook.Orders)
        {
            Orders.Add(new OrderInfo(
                order.ClOrdID,
                order.Symbol,
                order.Side?.ToString() ?? "",
                order.OrderQty,
                order.Price?.ToString("F4") ?? "",
                order.OrdStatus?.ToString() ?? "New",
                order.CumQty?.ToString() ?? "0",
                order.AvgPx?.ToString("F4") ?? "",
                GetStatusBadge(order.OrdStatus?.Value)
            ));
        }

        return Page();
    }

    static string FormatMessage(Fix.Message message)
    {
        var description = message.Describe();
        var sb = new StringBuilder();

        sb.Append($"<span class=\"msg-header\">{HttpUtility.HtmlEncode(description.MsgTypeDescription)}</span>");
        sb.Append($" <span class=\"msg-field-tag\">({(message.Incoming ? "incoming" : "outgoing")})</span>\n");
        sb.Append("<span class=\"msg-bracket\">{</span>\n");

        int widestName = 0;
        foreach (var field in description.Fields)
        {
            if (field.Name?.Length > widestName)
                widestName = field.Name.Length;
        }

        foreach (var field in description.Fields)
        {
            var name = HttpUtility.HtmlEncode((field.Name ?? "").PadLeft(widestName));
            var tag = HttpUtility.HtmlEncode($"({field.Tag})".PadLeft(6));
            var value = HttpUtility.HtmlEncode(field.Value ?? "");
            var valueDef = field.ValueDefinition is Fix.Dictionary.FieldValue fv
                ? $" <span class=\"msg-field-desc\">— {HttpUtility.HtmlEncode(fv.Name)}</span>"
                : "";

            sb.Append($"    <span class=\"msg-field-name\">{name}</span> <span class=\"msg-field-tag\">{tag}</span> — <span class=\"msg-field-value\">{value}</span>{valueDef}\n");
        }

        sb.Append("<span class=\"msg-bracket\">}</span>");
        return sb.ToString();
    }

    static string GetStatusBadge(string? status) => status switch
    {
        "0" => "badge-blue",   // New
        "1" => "badge-amber",  // PartiallyFilled
        "2" => "badge-green",  // Filled
        "4" => "badge-red",    // Canceled
        "8" => "badge-red",    // Rejected
        _ => "badge-gray"
    };
}

public record OrderInfo(string ClOrdID, string Symbol, string Side, long OrderQty, string Price, string OrdStatus, string CumQty, string AvgPx, string StatusBadge);
