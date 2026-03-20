using FixClient.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class SessionsIndexModel : PageModel
{
    readonly FixSessionManager _manager;

    public SessionsIndexModel(FixSessionManager manager)
    {
        _manager = manager;
    }

    public List<string> Versions { get; set; } = [];
    public List<SessionViewModel> SessionList { get; set; } = [];

    public void OnGet()
    {
        Versions = Fix.Dictionary.Versions.Select(v => v.BeginString).ToList();
        LoadSessions();
    }

    public IActionResult OnPost(string senderCompId, string targetCompId, string host, int port,
        string beginString, int heartBtInt, string behaviour)
    {
        _manager.CreateSession(senderCompId, targetCompId, host, port, beginString, heartBtInt,
            behaviour == "Acceptor" ? Fix.Behaviour.Acceptor : Fix.Behaviour.Initiator);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConnectAsync(string sessionId)
    {
        await _manager.ConnectAsync(sessionId);
        return RedirectToPage();
    }

    public IActionResult OnPostDisconnect(string sessionId)
    {
        _manager.Disconnect(sessionId);
        return RedirectToPage();
    }

    public IActionResult OnPostRemove(string sessionId)
    {
        _manager.RemoveSession(sessionId);
        return RedirectToPage();
    }

    void LoadSessions()
    {
        SessionList = _manager.Sessions.Values.Select(s => new SessionViewModel
        {
            Id = s.Id,
            SenderCompId = s.SenderCompId,
            TargetCompId = s.TargetCompId,
            Host = s.Host,
            Port = s.Port,
            BeginString = s.BeginString,
            HeartBtInt = s.HeartBtInt,
            Behaviour = s.Behaviour.ToString(),
            State = s.SessionState.ToString(),
            HistoryCount = s.HistoryEntries.Count,
            LogCount = s.LogEntries.Count
        }).ToList();
    }
}

public class SessionViewModel
{
    public string Id { get; set; } = "";
    public string SenderCompId { get; set; } = "";
    public string TargetCompId { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string BeginString { get; set; } = "";
    public int HeartBtInt { get; set; }
    public string Behaviour { get; set; } = "";
    public string State { get; set; } = "";
    public int HistoryCount { get; set; }
    public int LogCount { get; set; }
}
