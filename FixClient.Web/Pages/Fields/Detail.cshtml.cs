using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class FieldDetailModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string BeginString { get; set; } = "FIX.5.0SP2";

    [BindProperty(SupportsGet = true)]
    public int Tag { get; set; }

    public string FieldName { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Description { get; set; } = "";
    public string PedigreeText { get; set; } = "";
    public List<FieldValueInfo> Values { get; set; } = [];

    public IActionResult OnGet()
    {
        var version = Fix.Dictionary.Versions[BeginString];
        if (version == null)
            return NotFound();

        if (!version.Fields.TryGetValue(Tag, out var field) || !field.IsValid)
            return NotFound();

        FieldName = field.Name;
        DataType = field.DataType;
        Description = field.Description;
        PedigreeText = field.Pedigree.ToString();

        Values = field.Values.Values
            .Select(v => new FieldValueInfo(v.Value, v.Name, v.Description))
            .ToList();

        return Page();
    }
}

public record FieldValueInfo(string Value, string Name, string Description);
