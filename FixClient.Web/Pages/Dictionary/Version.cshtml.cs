using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class DictionaryVersionModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string BeginString { get; set; } = "";

    public List<MessageInfo> Messages { get; set; } = [];

    public IActionResult OnGet()
    {
        var version = Fix.Dictionary.Versions[BeginString];
        if (version == null)
            return NotFound();

        Messages = version.Messages
            .OrderBy(m => m.Name)
            .Select(m => new MessageInfo(m.MsgType, m.Name, m.Pedigree.Added ?? ""))
            .ToList();

        return Page();
    }
}

public record MessageInfo(string MsgType, string Name, string Added);
