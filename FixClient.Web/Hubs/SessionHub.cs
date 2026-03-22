using FixClient.Web.Services;
using Microsoft.AspNetCore.SignalR;

namespace FixClient.Web.Hubs;

public class SessionHub : Hub
{
    readonly FixSessionManager _manager;

    public SessionHub(FixSessionManager manager)
    {
        _manager = manager;
    }

    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }

    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
    }

    public async Task Connect(string sessionId)
    {
        var result = await _manager.ConnectAsync(sessionId);
        await Clients.Caller.SendAsync("ConnectResult", sessionId, result);
    }

    public Task Disconnect(string sessionId)
    {
        _manager.Disconnect(sessionId);
        return Task.CompletedTask;
    }

    public Task SendFixMessage(string sessionId, string rawMessage)
    {
        var input = rawMessage.Replace('|', '\x01').Replace('^', '\x01');
        var msg = new Fix.Message(input);
        _manager.SendMessage(sessionId, msg);
        return Task.CompletedTask;
    }

    public Task AcknowledgeOrder(string sessionId, string clOrdId)
    {
        _manager.AcknowledgeOrder(sessionId, clOrdId);
        return Task.CompletedTask;
    }

    public Task RejectOrder(string sessionId, string clOrdId, string? reason)
    {
        _manager.RejectOrder(sessionId, clOrdId, reason);
        return Task.CompletedTask;
    }

    public Task FillOrder(string sessionId, string clOrdId, long? qty, decimal? price)
    {
        _manager.FillOrder(sessionId, clOrdId, qty, price);
        return Task.CompletedTask;
    }

    public Task CancelOrder(string sessionId, string clOrdId)
    {
        _manager.CancelOrder(sessionId, clOrdId);
        return Task.CompletedTask;
    }

    public Task RejectCancelRequest(string sessionId, string clOrdId, string? reason)
    {
        _manager.RejectCancelRequest(sessionId, clOrdId, reason);
        return Task.CompletedTask;
    }
}
