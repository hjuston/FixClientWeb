using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Fix;

namespace FixClient.Web.Services;

public class FixSessionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string SenderCompId { get; set; } = "SENDER";
    public string TargetCompId { get; set; } = "TARGET";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9810;
    public string BeginString { get; set; } = "FIX.5.0SP2";
    public int HeartBtInt { get; set; } = 30;
    public Behaviour Behaviour { get; set; } = Behaviour.Initiator;
    public State SessionState { get; set; } = State.Disconnected;
    public Fix.Session? Session { get; set; }
    public TcpClient? TcpClient { get; set; }
    public TcpListener? TcpListener { get; set; }
    public List<LogEntry> LogEntries { get; } = [];
    public List<HistoryEntry> HistoryEntries { get; } = [];
    public OrderBook OrderBook { get; } = new();
    public Dictionary<string, bool> MessageFilters { get; } = new();
    public Dictionary<string, HashSet<int>> FieldFilters { get; } = new();
}

public record LogEntry(DateTime Timestamp, string Level, string Message);
public record HistoryEntry(DateTime Timestamp, string Direction, string MsgType, string MsgTypeName, int SeqNum, string Summary, string Raw);

public class FixSessionManager
{
    readonly ConcurrentDictionary<string, FixSessionInfo> _sessions = new();

    public event Action<string, HistoryEntry>? MessageReceived;
    public event Action<string, HistoryEntry>? MessageSent;
    public event Action<string, LogEntry>? LogAdded;
    public event Action<string, State>? StateChanged;

    public IReadOnlyDictionary<string, FixSessionInfo> Sessions => _sessions;

    public FixSessionInfo CreateSession(string senderCompId, string targetCompId, string host, int port,
        string beginString, int heartBtInt, Behaviour behaviour)
    {
        var info = new FixSessionInfo
        {
            SenderCompId = senderCompId,
            TargetCompId = targetCompId,
            Host = host,
            Port = port,
            BeginString = beginString,
            HeartBtInt = heartBtInt,
            Behaviour = behaviour
        };

        var session = new Fix.Session
        {
            SenderCompId = senderCompId,
            TargetCompId = targetCompId,
            HeartBtInt = heartBtInt,
            LogonBehaviour = behaviour
        };

        var version = Fix.Dictionary.Versions[beginString];
        if (version != null)
        {
            session.BeginString = version;
            session.DefaultApplVerId = version;
        }

        session.MessageReceived += (sender, e) =>
        {
            var msg = e.Message;
            var desc = msg.Describe();
            var entry = new HistoryEntry(
                DateTime.Now, "Incoming", msg.MsgType, desc.MsgTypeDescription ?? msg.MsgType,
                int.TryParse(msg.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.MsgSeqNum)?.Value, out var sn) ? sn : 0,
                FormatSummary(msg), FormatRaw(msg));
            info.HistoryEntries.Add(entry);
            info.OrderBook.Process((Fix.Message)msg.Clone());
            MessageReceived?.Invoke(info.Id, entry);
        };

        session.MessageSent += (sender, e) =>
        {
            var msg = e.Message;
            var desc = msg.Describe();
            var entry = new HistoryEntry(
                DateTime.Now, "Outgoing", msg.MsgType, desc.MsgTypeDescription ?? msg.MsgType,
                int.TryParse(msg.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.MsgSeqNum)?.Value, out var sn) ? sn : 0,
                FormatSummary(msg), FormatRaw(msg));
            info.HistoryEntries.Add(entry);
            MessageSent?.Invoke(info.Id, entry);
        };

        session.Information += (sender, e) =>
        {
            var entry = new LogEntry(e.TimeStamp, "Info", e.Message);
            info.LogEntries.Add(entry);
            LogAdded?.Invoke(info.Id, entry);
        };

        session.Warning += (sender, e) =>
        {
            var entry = new LogEntry(e.TimeStamp, "Warn", e.Message);
            info.LogEntries.Add(entry);
            LogAdded?.Invoke(info.Id, entry);
        };

        session.Error += (sender, e) =>
        {
            var entry = new LogEntry(e.TimeStamp, "Error", e.Message);
            info.LogEntries.Add(entry);
            LogAdded?.Invoke(info.Id, entry);
        };

        session.StateChanged += (sender, e) =>
        {
            info.SessionState = e.State;
            StateChanged?.Invoke(info.Id, e.State);
            var entry = new LogEntry(DateTime.Now, "Info", $"State changed to {e.State}");
            info.LogEntries.Add(entry);
            LogAdded?.Invoke(info.Id, entry);
        };

        info.Session = session;
        _sessions[info.Id] = info;

        AddLog(info, "Info", "Session created");
        return info;
    }

    public async Task<bool> ConnectAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var info) || info.Session == null)
            return false;

        try
        {
            if (info.Behaviour == Behaviour.Initiator)
            {
                var address = Fix.Network.GetAddress(info.Host);
                info.TcpClient = new TcpClient();
                AddLog(info, "Info", $"Connecting to {info.Host}:{info.Port}...");
                await info.TcpClient.ConnectAsync(address, info.Port);
                info.Session.Stream = info.TcpClient.GetStream();
            }
            else
            {
                var endPoint = new IPEndPoint(IPAddress.Any, info.Port);
                info.TcpListener = new TcpListener(endPoint);
                info.TcpListener.Start();
                AddLog(info, "Info", $"Listening on port {info.Port}...");
                var socket = await info.TcpListener.AcceptSocketAsync();
                info.Session.Stream = new System.Net.Sockets.NetworkStream(socket, true);
            }

            info.Session.Open();
            return true;
        }
        catch (Exception ex)
        {
            AddLog(info, "Error", $"Connection failed: {ex.Message}");
            return false;
        }
    }

    public void Disconnect(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var info))
            return;

        try
        {
            info.Session?.Close();
            info.TcpClient?.Close();
            info.TcpClient = null;
            info.TcpListener?.Stop();
            info.TcpListener = null;
            info.SessionState = State.Disconnected;
            AddLog(info, "Info", "Disconnected");
            StateChanged?.Invoke(info.Id, State.Disconnected);
        }
        catch (Exception ex)
        {
            AddLog(info, "Error", $"Disconnect error: {ex.Message}");
        }
    }

    public bool SendMessage(string sessionId, Fix.Message message)
    {
        if (!_sessions.TryGetValue(sessionId, out var info) || info.Session == null)
            return false;

        try
        {
            info.Session.Send(message);
            return true;
        }
        catch (Exception ex)
        {
            AddLog(info, "Error", $"Send failed: {ex.Message}");
            return false;
        }
    }

    public bool RemoveSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var info))
        {
            Disconnect(sessionId);
            return true;
        }
        return false;
    }

    public FixSessionInfo? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var info);
        return info;
    }

    void AddLog(FixSessionInfo info, string level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        info.LogEntries.Add(entry);
        LogAdded?.Invoke(info.Id, entry);
    }

    static string FormatSummary(Fix.Message msg)
    {
        var parts = new List<string>();
        if (msg.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID) is Fix.Field clOrdId && !string.IsNullOrEmpty(clOrdId.Value))
            parts.Add($"ClOrdID={clOrdId.Value}");
        if (msg.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol) is Fix.Field symbol && !string.IsNullOrEmpty(symbol.Value))
            parts.Add($"Symbol={symbol.Value}");
        if (msg.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Side) is Fix.Field side && !string.IsNullOrEmpty(side.Value))
            parts.Add($"Side={side.Value}");
        if (msg.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty) is Fix.Field qty && !string.IsNullOrEmpty(qty.Value))
            parts.Add($"Qty={qty.Value}");
        return string.Join(" ", parts);
    }

    static string FormatRaw(Fix.Message msg)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var field in msg.Fields)
        {
            if (field.Value is not null && field.Value.Length > 0)
                sb.Append($"{field.Tag}={field.Value}|");
        }
        return sb.ToString();
    }
}
