using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class DictionaryIndexModel : PageModel
{
    public List<DictionaryVersionInfo> Versions { get; set; } = [];

    public void OnGet()
    {
        Versions = Fix.Dictionary.Versions
            .Select(v => new DictionaryVersionInfo(
                v.BeginString,
                v.Messages.Count(),
                v.Fields.Count(f => f.IsValid)))
            .ToList();
    }
}

public record DictionaryVersionInfo(string BeginString, int MessageCount, int FieldCount);
