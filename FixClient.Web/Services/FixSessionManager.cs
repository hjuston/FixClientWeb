using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Fix;

namespace FixClient.Web.Services;

public class FixSessionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    // Required
    public string SenderCompId { get; set; } = "SENDER";
    public string TargetCompId { get; set; } = "TARGET";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9810;
    public string BeginString { get; set; } = "FIX.5.0SP2";
    public int HeartBtInt { get; set; } = 30;
    public Behaviour Behaviour { get; set; } = Behaviour.Initiator;
    // Optional - Session
    public Behaviour OrderBehaviour { get; set; } = Behaviour.Initiator;
    public Behaviour LogonBehaviour { get; set; } = Behaviour.Initiator;
    public string? DefaultApplVerId { get; set; }
    public int TestRequestDelay { get; set; } = 2;
    public bool BrokenNewSeqNo { get; set; }
    public bool NextExpectedMsgSeqNum { get; set; }
    public bool ValidateDataFields { get; set; } = true;
    // Optional - Message Generation
    public int IncomingSeqNum { get; set; } = 1;
    public int OutgoingSeqNum { get; set; } = 1;
    public int TestRequestId { get; set; }
    // Optional - Network
    public bool FragmentMessages { get; set; } = true;
    public State SessionState { get; set; } = State.Disconnected;
    public Fix.Session? Session { get; set; }
    public TcpClient? TcpClient { get; set; }
    public TcpListener? TcpListener { get; set; }
    public List<LogEntry> LogEntries { get; } = [];
    public List<HistoryEntry> HistoryEntries { get; } = [];
    public OrderBook OrderBook { get; } = new();
    public Dictionary<string, bool> MessageFilters { get; } = new();
    public Dictionary<string, HashSet<int>> FieldFilters { get; } = new();
    public int NextOrderId { get; set; } = 1;
    public int NextExecId { get; set; } = 1;
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

    public FixSessionInfo CreateSession(FixSessionInfo info)
    {
        var session = new Fix.Session
        {
            SenderCompId = info.SenderCompId,
            TargetCompId = info.TargetCompId,
            HeartBtInt = info.HeartBtInt,
            LogonBehaviour = info.LogonBehaviour,
            OrderBehaviour = info.OrderBehaviour,
            TestRequestDelay = info.TestRequestDelay,
            BrokenNewSeqNo = info.BrokenNewSeqNo,
            NextExpectedMsgSeqNum = info.NextExpectedMsgSeqNum,
            ValidateDataFields = info.ValidateDataFields,
            IncomingSeqNum = info.IncomingSeqNum,
            OutgoingSeqNum = info.OutgoingSeqNum,
            TestRequestId = info.TestRequestId,
            FragmentMessages = info.FragmentMessages
        };

        var version = Fix.Dictionary.Versions[info.BeginString];
        if (version != null)
        {
            session.BeginString = version;
            var applVer = !string.IsNullOrEmpty(info.DefaultApplVerId)
                ? Fix.Dictionary.Versions[info.DefaultApplVerId]
                : null;
            session.DefaultApplVerId = applVer ?? version;
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

    public Order? FindOrder(string sessionId, string clOrdId)
    {
        if (!_sessions.TryGetValue(sessionId, out var info))
            return null;

        foreach (var order in info.OrderBook.Orders)
        {
            if (order.ClOrdID == clOrdId)
                return order;
        }
        return null;
    }

    public IEnumerable<Order> GetOrders(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var info))
            return [];
        return info.OrderBook.Orders;
    }

    public bool AcknowledgeOrder(string sessionId, string clOrdId)
    {
        if (!_sessions.TryGetValue(sessionId, out var info) || info.Session == null)
            return false;

        var order = FindOrder(sessionId, clOrdId);
        if (order == null)
            return false;

        var message = new Fix.Message { MsgType = Fix.Dictionary.FIX_5_0SP2.Messages.ExecutionReport.MsgType };
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID, order.ClOrdID);
        if (order.Side is Fix.Dictionary.FieldValue sideValue)
            message.Fields.Set(sideValue);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol, order.Symbol);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty, order.OrderQty);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.OrdStatus.New);

        order.OrderID ??= info.NextOrderId++.ToString();
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderID, order.OrderID);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ExecID, info.NextExecId++.ToString());
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastQty, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastPx, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.CumQty, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.AvgPx, 0);

        if (info.BeginString != "FIX.4.0")
        {
            message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.ExecType.New);
            message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LeavesQty, order.OrderQty);
        }

        if (info.BeginString.StartsWith("FIX.4."))
            message.Fields.Set(Fix.Dictionary.FIX_4_2.ExecTransType.New);

        return SendMessage(sessionId, message);
    }

    public bool RejectOrder(string sessionId, string clOrdId, string? reason = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var info) || info.Session == null)
            return false;

        var order = FindOrder(sessionId, clOrdId);
        if (order == null)
            return false;

        var message = new Fix.Message { MsgType = Fix.Dictionary.FIX_5_0SP2.Messages.ExecutionReport.MsgType };
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID, order.ClOrdID);
        if (order.Side is Fix.Dictionary.FieldValue sideValue)
            message.Fields.Set(sideValue);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol, order.Symbol);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty, order.OrderQty);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.OrdStatus.Rejected);

        order.OrderID ??= info.NextOrderId++.ToString();
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderID, order.OrderID);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ExecID, info.NextExecId++.ToString());
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastQty, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastPx, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.CumQty, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.AvgPx, 0);

        if (info.BeginString != "FIX.4.0")
        {
            message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.ExecType.Rejected);
            message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LeavesQty, 0);
        }

        if (info.BeginString.StartsWith("FIX.4."))
            message.Fields.Set(Fix.Dictionary.FIX_4_2.ExecTransType.New);

        if (!string.IsNullOrEmpty(reason))
            message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.Text, reason);

        return SendMessage(sessionId, message);
    }

    public bool FillOrder(string sessionId, string clOrdId, long? fillQty = null, decimal? fillPrice = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var info) || info.Session == null)
            return false;

        var order = FindOrder(sessionId, clOrdId);
        if (order == null)
            return false;

        var qty = fillQty ?? (order.LeavesQty ?? order.OrderQty);
        var price = fillPrice ?? order.Price ?? 0;
        var cumQty = (order.CumQty ?? 0) + qty;
        var leavesQty = order.OrderQty - cumQty;
        var isFull = leavesQty <= 0;

        var message = new Fix.Message { MsgType = Fix.Dictionary.FIX_5_0SP2.Messages.ExecutionReport.MsgType };
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID, order.ClOrdID);
        if (order.Side is Fix.Dictionary.FieldValue sideValue)
            message.Fields.Set(sideValue);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol, order.Symbol);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty, order.OrderQty);
        message.Fields.Set(isFull ? Fix.Dictionary.FIX_5_0SP2.OrdStatus.Filled : Fix.Dictionary.FIX_5_0SP2.OrdStatus.PartiallyFilled);

        order.OrderID ??= info.NextOrderId++.ToString();
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderID, order.OrderID);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ExecID, info.NextExecId++.ToString());
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastQty, qty);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastPx, price);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.CumQty, cumQty);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.AvgPx, price);

        if (info.BeginString != "FIX.4.0")
        {
            if (info.BeginString == "FIX.4.1" || info.BeginString == "FIX.4.2")
                message.Fields.Set(Fix.Dictionary.FIX_4_2.ExecType.Fill);
            else
                message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.ExecType.Trade);
            message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LeavesQty, leavesQty > 0 ? leavesQty : 0);
        }

        if (info.BeginString.StartsWith("FIX.4."))
            message.Fields.Set(Fix.Dictionary.FIX_4_2.ExecTransType.New);

        return SendMessage(sessionId, message);
    }

    public bool CancelOrder(string sessionId, string clOrdId)
    {
        if (!_sessions.TryGetValue(sessionId, out var info) || info.Session == null)
            return false;

        var order = FindOrder(sessionId, clOrdId);
        if (order == null)
            return false;

        var message = new Fix.Message { MsgType = Fix.Dictionary.FIX_5_0SP2.Messages.ExecutionReport.MsgType };
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID, order.ClOrdID);
        if (order.Side is Fix.Dictionary.FieldValue sideValue)
            message.Fields.Set(sideValue);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol, order.Symbol);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty, order.OrderQty);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.OrdStatus.Canceled);

        order.OrderID ??= info.NextOrderId++.ToString();
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderID, order.OrderID);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ExecID, info.NextExecId++.ToString());
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastQty, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastPx, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.CumQty, order.CumQty ?? 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.AvgPx, order.AvgPx ?? 0);

        if (info.BeginString != "FIX.4.0")
        {
            message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.ExecType.Canceled);
            message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LeavesQty, 0);
        }

        if (info.BeginString.StartsWith("FIX.4."))
            message.Fields.Set(Fix.Dictionary.FIX_4_2.ExecTransType.New);

        return SendMessage(sessionId, message);
    }

    public bool RejectCancelRequest(string sessionId, string clOrdId, string? reason = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var info) || info.Session == null)
            return false;

        var order = FindOrder(sessionId, clOrdId);
        if (order == null)
            return false;

        var message = new Fix.Message { MsgType = Fix.Dictionary.FIX_5_0SP2.Messages.OrderCancelReject.MsgType };
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID, order.NewClOrdID ?? order.ClOrdID);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrigClOrdID, order.ClOrdID);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderID, order.OrderID ?? "NONE");
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrdStatus, order.PreviousOrdStatus?.Value ?? order.OrdStatus?.Value ?? "0");
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.CxlRejResponseTo.OrderCancelRequest);

        if (!string.IsNullOrEmpty(reason))
            message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.Text, reason);

        return SendMessage(sessionId, message);
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
