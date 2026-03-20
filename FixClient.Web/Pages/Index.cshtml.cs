using FixClient.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FixClient.Web.Pages;

public class IndexModel : PageModel
{
    readonly FixSessionManager _manager;

    public IndexModel(FixSessionManager manager)
    {
        _manager = manager;
    }

    public List<FixVersionInfo> Versions { get; set; } = [];
    public int TotalFields { get; set; }
    public int ActiveSessions { get; set; }
    public int ConnectedSessions { get; set; }
    public int TotalMessages { get; set; }

    public void OnGet()
    {
        Versions = Fix.Dictionary.Versions
            .Select(v => new FixVersionInfo(v.BeginString, v.Messages.Count()))
            .ToList();

        TotalFields = Fix.Dictionary.Versions.FIX_5_0SP2.Fields
            .Count(f => f.IsValid);

        ActiveSessions = _manager.Sessions.Count;
        ConnectedSessions = _manager.Sessions.Values.Count(s => s.SessionState != Fix.State.Disconnected);
        TotalMessages = _manager.Sessions.Values.Sum(s => s.HistoryEntries.Count);
    }
}

public record FixVersionInfo(string BeginString, int MessageCount);
