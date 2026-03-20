using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class DictionaryMessageModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string BeginString { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string MsgType { get; set; } = "";

    public string MessageName { get; set; } = "";
    public string Description { get; set; } = "";
    public string PedigreeText { get; set; } = "";
    public List<MessageFieldInfo> Fields { get; set; } = [];

    public IActionResult OnGet()
    {
        var version = Fix.Dictionary.Versions[BeginString];
        if (version == null)
            return NotFound();

        var message = version.Messages[MsgType];
        if (message == null)
            return NotFound();

        MessageName = message.Name;
        Description = message.Description;
        PedigreeText = message.Pedigree.ToString();

        Fields = message.Fields
            .Select(f => new MessageFieldInfo(f.Tag, f.Name, f.Required, f.Depth, f.Description))
            .ToList();

        return Page();
    }
}

public record MessageFieldInfo(int Tag, string Name, bool Required, int Depth, string Description);
