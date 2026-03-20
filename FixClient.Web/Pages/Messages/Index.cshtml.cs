using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class MessagesIndexModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string BeginString { get; set; } = "FIX.5.0SP2";

    [BindProperty(SupportsGet = true)]
    public string? SelectedMsgType { get; set; }

    public List<string> AllVersions { get; set; } = [];
    public List<MessageTypeItem> MessageTypes { get; set; } = [];
    public SelectedMessageInfo? SelectedMessage { get; set; }
    public List<FieldEditorInfo> FieldEditors { get; set; } = [];
    public string? BuiltMessage { get; set; }

    [BindProperty]
    public List<FieldInput> FieldValues { get; set; } = [];

    public IActionResult OnGet()
    {
        AllVersions = Fix.Dictionary.Versions.Select(v => v.BeginString).ToList();

        var version = Fix.Dictionary.Versions[BeginString];
        if (version == null)
            return NotFound();

        MessageTypes = version.Messages
            .OrderBy(m => m.Name)
            .Select(m => new MessageTypeItem(m.MsgType, m.Name))
            .ToList();

        if (!string.IsNullOrEmpty(SelectedMsgType))
        {
            LoadMessage(version);
        }

        return Page();
    }

    public IActionResult OnPost()
    {
        AllVersions = Fix.Dictionary.Versions.Select(v => v.BeginString).ToList();

        var version = Fix.Dictionary.Versions[BeginString];
        if (version == null)
            return NotFound();

        MessageTypes = version.Messages
            .OrderBy(m => m.Name)
            .Select(m => new MessageTypeItem(m.MsgType, m.Name))
            .ToList();

        if (!string.IsNullOrEmpty(SelectedMsgType))
        {
            LoadMessage(version);
            BuildMessage(version);
        }

        return Page();
    }

    void LoadMessage(Fix.Dictionary.Version version)
    {
        var msg = version.Messages[SelectedMsgType!];
        if (msg == null) return;

        SelectedMessage = new SelectedMessageInfo(msg.MsgType, msg.Name, msg.Description);

        FieldEditors = msg.Fields.Select(f =>
        {
            var versionField = version.Fields.TryGetValue(f.Tag, out var vf) ? vf : null;
            var enumValues = versionField?.Values.Values
                .Select(v => new EnumValueItem(v.Value, v.Name))
                .ToList() ?? [];

            var existingValue = FieldValues.FirstOrDefault(fv => fv.Tag == f.Tag)?.Value ?? "";

            return new FieldEditorInfo(f.Tag, f.Name, f.Required, f.Depth, existingValue, enumValues);
        }).ToList();
    }

    void BuildMessage(Fix.Dictionary.Version version)
    {
        var msg = new Fix.Message();
        msg.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.BeginString, BeginString);
        msg.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.MsgType, SelectedMsgType!);

        foreach (var fv in FieldValues)
        {
            if (!string.IsNullOrEmpty(fv.Value) && fv.Tag > 0)
            {
                if (version.Fields.TryGetValue(fv.Tag, out var fieldDef))
                {
                    msg.Fields.Set(fieldDef, fv.Value);
                }
            }
        }

        var sb = new StringBuilder();
        foreach (var field in msg.Fields)
        {
            if (field.Value is not null && field.Value.Length > 0)
            {
                sb.Append($"{field.Tag}={field.Value}|");
            }
        }

        BuiltMessage = sb.ToString();
    }
}

public record MessageTypeItem(string MsgType, string Name);
public record SelectedMessageInfo(string MsgType, string Name, string Description);
public record FieldEditorInfo(int Tag, string Name, bool Required, int Depth, string Value, List<EnumValueItem> EnumValues);
public record EnumValueItem(string Value, string Name);
public record FieldInput
{
    public int Tag { get; set; }
    public string? Value { get; set; }
}
