using FixClient.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class FiltersIndexModel : PageModel
{
    readonly FixSessionManager _manager;

    public FiltersIndexModel(FixSessionManager manager)
    {
        _manager = manager;
    }

    public string SessionId { get; set; } = "";
    public string SessionLabel { get; set; } = "";
    public List<string> SessionIds { get; set; } = [];
    public List<MessageFilterItem> MessageFilters { get; set; } = [];
    public bool Found { get; set; }
    public string SelectedVersion { get; set; } = "";

    public void OnGet(string? sessionId, string? version)
    {
        SessionIds = _manager.Sessions.Values.Select(s => s.Id).ToList();
        SelectedVersion = version ?? "FIX.5.0SP2";

        if (!string.IsNullOrEmpty(sessionId))
        {
            SessionId = sessionId;
            var info = _manager.GetSession(sessionId);
            if (info != null)
            {
                Found = true;
                SessionLabel = $"{info.SenderCompId} ? {info.TargetCompId}";
                SelectedVersion = info.BeginString;
            }
        }

        LoadMessageFilters();
    }

    public IActionResult OnPost(string? sessionId, string version, string[] enabledMsgTypes)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            var info = _manager.GetSession(sessionId);
            if (info != null)
            {
                info.MessageFilters.Clear();
                var ver = Fix.Dictionary.Versions[version ?? info.BeginString];
                if (ver != null)
                {
                    foreach (var msg in ver.Messages)
                    {
                        info.MessageFilters[msg.MsgType] = enabledMsgTypes.Contains(msg.MsgType);
                    }
                }
            }
        }

        return RedirectToPage(new { sessionId, version });
    }

    void LoadMessageFilters()
    {
        var ver = Fix.Dictionary.Versions[SelectedVersion];
        if (ver == null) return;

        FixSessionInfo? sessionInfo = null;
        if (!string.IsNullOrEmpty(SessionId))
            sessionInfo = _manager.GetSession(SessionId);

        MessageFilters = ver.Messages
            .OrderBy(m => m.Name)
            .Select(m => new MessageFilterItem
            {
                MsgType = m.MsgType,
                Name = m.Name,
                Enabled = sessionInfo?.MessageFilters.TryGetValue(m.MsgType, out var v) == true ? v : true
            })
            .ToList();
    }
}

public class MessageFilterItem
{
    public string MsgType { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
