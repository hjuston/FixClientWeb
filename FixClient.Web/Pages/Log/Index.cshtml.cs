using FixClient.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class LogIndexModel : PageModel
{
    readonly FixSessionManager _manager;

    public LogIndexModel(FixSessionManager manager)
    {
        _manager = manager;
    }

    public string SessionId { get; set; } = "";
    public string SessionLabel { get; set; } = "";
    public List<LogEntry> LogEntries { get; set; } = [];
    public bool Found { get; set; }
    public List<SessionViewModel> AllSessions { get; set; } = [];

    public void OnGet(string? sessionId)
    {
        AllSessions = _manager.Sessions.Values.Select(s => new SessionViewModel
        {
            Id = s.Id, SenderCompId = s.SenderCompId, TargetCompId = s.TargetCompId,
            State = s.SessionState.ToString(), LogCount = s.LogEntries.Count
        }).ToList();

        SessionId = sessionId ?? "";
        var info = _manager.GetSession(SessionId);
        if (info != null)
        {
            Found = true;
            SessionLabel = $"{info.SenderCompId} ? {info.TargetCompId}";
            LogEntries = info.LogEntries.TakeLast(500).Reverse().ToList();
        }
    }
}
