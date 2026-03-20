using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class FieldsIndexModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string BeginString { get; set; } = "FIX.5.0SP2";

    public List<FieldSummary> Fields { get; set; } = [];
    public List<string> AllVersions { get; set; } = [];

    public IActionResult OnGet()
    {
        AllVersions = Fix.Dictionary.Versions.Select(v => v.BeginString).ToList();

        var version = Fix.Dictionary.Versions[BeginString];
        if (version == null)
            return NotFound();

        Fields = version.Fields
            .Where(f => f.IsValid)
            .OrderBy(f => f.Tag)
            .Select(f => new FieldSummary(f.Tag, f.Name, f.DataType, f.Description, f.Values.Count))
            .ToList();

        return Page();
    }
}

public record FieldSummary(int Tag, string Name, string DataType, string Description, int ValueCount);
