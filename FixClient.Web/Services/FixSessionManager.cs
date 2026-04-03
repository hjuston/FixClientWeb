using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Fix;
using FixClient.Web.Data;

namespace FixClient.Web.Services;

public class FixSessionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    // Required
    public string SenderCompId { get; set; } = "";
    public string TargetCompId { get; set; } = "";
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
    public CancellationTokenSource? ListenerCancellation { get; set; }
    public bool Enabled { get; set; }
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
    readonly SessionPersistenceStore? _store;

    public FixSessionManager(SessionPersistenceStore? store = null)
    {
        _store = store;
        LoadPersistedSessions();
    }

    public event Action<string, HistoryEntry>? MessageReceived;
    public event Action<string, HistoryEntry>? MessageSent;
    public event Action<string, LogEntry>? LogAdded;
    public event Action<string, State>? StateChanged;

    public IReadOnlyDictionary<string, FixSessionInfo> Sessions => _sessions;

    public FixSessionInfo CreateSession(FixSessionInfo info)
    {
        return CreateSession(info, addCreatedLog: true, persist: true);
    }

    FixSessionInfo CreateSession(FixSessionInfo info, bool addCreatedLog, bool persist)
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
            PersistSafe(() => _store!.AddHistoryAsync(info.Id, entry));
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
            PersistSafe(() => _store!.AddHistoryAsync(info.Id, entry));
        };

        session.Information += (sender, e) =>
        {
            var entry = new LogEntry(e.TimeStamp, "Info", e.Message);
            info.LogEntries.Add(entry);
            LogAdded?.Invoke(info.Id, entry);
            PersistSafe(() => _store!.AddLogAsync(info.Id, entry));
        };

        session.Warning += (sender, e) =>
        {
            var entry = new LogEntry(e.TimeStamp, "Warn", e.Message);
            info.LogEntries.Add(entry);
            LogAdded?.Invoke(info.Id, entry);
            PersistSafe(() => _store!.AddLogAsync(info.Id, entry));
        };

        session.Error += (sender, e) =>
        {
            var entry = new LogEntry(e.TimeStamp, "Error", e.Message);
            info.LogEntries.Add(entry);
            LogAdded?.Invoke(info.Id, entry);
            PersistSafe(() => _store!.AddLogAsync(info.Id, entry));
        };

        session.StateChanged += (sender, e) =>
        {
            info.SessionState = e.State;
            info.Enabled = e.State != State.Disconnected;

            if (e.State == State.Disconnected)
            {
                ReleaseNetworkResources(info);
            }

            StateChanged?.Invoke(info.Id, e.State);
            var entry = new LogEntry(DateTime.Now, "Info", $"State changed to {e.State}");
            info.LogEntries.Add(entry);
            LogAdded?.Invoke(info.Id, entry);
            PersistSafe(() => _store!.AddLogAsync(info.Id, entry));
            PersistSafe(() => _store!.UpsertSessionAsync(info));
        };

        info.Session = session;
        _sessions[info.Id] = info;

        if (addCreatedLog)
        {
            AddLog(info, "Info", "Session created");
        }

        if (persist)
        {
            PersistSafe(() => _store!.UpsertSessionAsync(info));
        }

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
                if (info.Enabled && info.SessionState != State.Disconnected)
                    return true;

                var address = Fix.Network.GetAddress(info.Host);
                info.TcpClient = new TcpClient();
                AddLog(info, "Info", $"Connecting to {info.Host}:{info.Port}...");
                await info.TcpClient.ConnectAsync(address, info.Port);
                info.Session.Stream = info.TcpClient.GetStream();
                info.Enabled = true;
                info.SessionState = State.Connected;
                StateChanged?.Invoke(info.Id, State.Connected);
                AddLog(info, "Info", "Enabled");
                PersistSafe(() => _store!.UpsertSessionAsync(info));
                info.Session.Open();
                return true;
            }

            if (info.Enabled && info.TcpListener != null)
                return true;

            var endPoint = new IPEndPoint(IPAddress.Any, info.Port);
            info.TcpListener = new TcpListener(endPoint);
            info.TcpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            info.TcpListener.Start();
            info.Enabled = true;
            info.ListenerCancellation = new CancellationTokenSource();

            info.SessionState = State.Connected;
            StateChanged?.Invoke(info.Id, State.Connected);
            AddLog(info, "Info", $"Enabled. Listening on port {info.Port}...");
            PersistSafe(() => _store!.UpsertSessionAsync(info));

            _ = Task.Run(() => AcceptConnectionAsync(info, info.ListenerCancellation.Token));
            return true;
        }
        catch (Exception ex)
        {
            info.Enabled = false;
            ReleaseNetworkResources(info);
            info.SessionState = State.Disconnected;
            StateChanged?.Invoke(info.Id, State.Disconnected);
            AddLog(info, "Error", $"Connection failed: {ex.Message}");
            PersistSafe(() => _store!.UpsertSessionAsync(info));
            return false;
        }
    }

    async Task AcceptConnectionAsync(FixSessionInfo info, CancellationToken cancellationToken)
    {
        try
        {
            if (info.TcpListener == null || info.Session == null)
                return;

            var socket = await info.TcpListener.AcceptSocketAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                return;
            }

            AddLog(info, "Info", "Incoming connection accepted");
            info.Session.Stream = new System.Net.Sockets.NetworkStream(socket, true);
            info.Session.Open();

            info.SessionState = State.Connected;
            StateChanged?.Invoke(info.Id, State.Connected);
            PersistSafe(() => _store!.UpsertSessionAsync(info));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            info.Enabled = false;
            ReleaseNetworkResources(info);
            AddLog(info, "Error", $"Accept failed: {ex.Message}");
            info.SessionState = State.Disconnected;
            StateChanged?.Invoke(info.Id, State.Disconnected);
            PersistSafe(() => _store!.UpsertSessionAsync(info));
        }
    }

    public void Disconnect(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var info))
            return;

        try
        {
            info.Enabled = false;
            info.Session?.Close();
            ReleaseNetworkResources(info);
            info.SessionState = State.Disconnected;
            AddLog(info, "Info", "Disconnected");
            StateChanged?.Invoke(info.Id, State.Disconnected);
            PersistSafe(() => _store!.UpsertSessionAsync(info));
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

        if (info.SessionState == State.Disconnected || info.Session.Stream == null)
        {
            AddLog(info, "Error", "Send failed: session is not connected");
            return false;
        }

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
        if (_sessions.TryGetValue(sessionId, out _))
        {
            Disconnect(sessionId);
            var removed = _sessions.TryRemove(sessionId, out _);
            if (removed)
            {
                PersistSafe(() => _store!.DeleteSessionAsync(sessionId));
            }
            return removed;
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
        PersistSafe(() => _store!.AddLogAsync(info.Id, entry));
    }

    void PersistSafe(Func<Task> operation)
    {
        if (_store == null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Persistence error: {ex.Message}");
            }
        });
    }

    void LoadPersistedSessions()
    {
        if (_store == null)
            return;

        try
        {
            var persisted = _store.LoadAllAsync().GetAwaiter().GetResult();

            foreach (var row in persisted)
            {
                var info = new FixSessionInfo
                {
                    Id = row.Id,
                    SenderCompId = row.SenderCompId,
                    TargetCompId = row.TargetCompId,
                    Host = row.Host,
                    Port = row.Port,
                    BeginString = row.BeginString,
                    HeartBtInt = row.HeartBtInt,
                    Behaviour = Enum.TryParse<Behaviour>(row.Behaviour, out var behaviour) ? behaviour : Behaviour.Initiator,
                    OrderBehaviour = Enum.TryParse<Behaviour>(row.OrderBehaviour, out var orderBehaviour) ? orderBehaviour : Behaviour.Initiator,
                    LogonBehaviour = Enum.TryParse<Behaviour>(row.LogonBehaviour, out var logonBehaviour) ? logonBehaviour : Behaviour.Initiator,
                    DefaultApplVerId = row.DefaultApplVerId,
                    TestRequestDelay = row.TestRequestDelay,
                    BrokenNewSeqNo = row.BrokenNewSeqNo,
                    NextExpectedMsgSeqNum = row.NextExpectedMsgSeqNum,
                    ValidateDataFields = row.ValidateDataFields,
                    IncomingSeqNum = row.IncomingSeqNum,
                    OutgoingSeqNum = row.OutgoingSeqNum,
                    TestRequestId = row.TestRequestId,
                    FragmentMessages = row.FragmentMessages,
                    NextOrderId = row.NextOrderId <= 0 ? 1 : row.NextOrderId,
                    NextExecId = row.NextExecId <= 0 ? 1 : row.NextExecId,
                    Enabled = false,
                    SessionState = State.Disconnected
                };

                CreateSession(info, addCreatedLog: false, persist: false);

                foreach (var log in row.LogEntries.OrderBy(x => x.Timestamp))
                {
                    info.LogEntries.Add(new LogEntry(log.Timestamp, log.Level, log.Message));
                }

                foreach (var history in row.HistoryEntries.OrderBy(x => x.Timestamp))
                {
                    var entry = new HistoryEntry(
                        history.Timestamp,
                        history.Direction,
                        history.MsgType,
                        history.MsgTypeName,
                        history.SeqNum,
                        history.Summary,
                        history.Raw);

                    info.HistoryEntries.Add(entry);

                    if (history.Direction != "Incoming" || string.IsNullOrWhiteSpace(history.Raw))
                        continue;

                    try
                    {
                        var message = new Fix.Message(history.Raw.Replace('|', '\x01'));
                        info.OrderBook.Process(message);
                    }
                    catch
                    {
                        // ignore malformed persisted raw history lines
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load persisted sessions: {ex.Message}");
        }
    }

    static void ReleaseNetworkResources(FixSessionInfo info)
    {
        info.ListenerCancellation?.Cancel();
        info.ListenerCancellation?.Dispose();
        info.ListenerCancellation = null;

        info.TcpClient?.Close();
        info.TcpClient = null;

        info.TcpListener?.Stop();
        info.TcpListener = null;
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

        var order = FindOrderForAction(info, clOrdId);
        var sourceMessage = order == null ? FindIncomingNewOrder(info, clOrdId) : null;
        if (order == null && sourceMessage == null)
        {
            AddLog(info, "Error", $"Acknowledge failed: order {clOrdId} not found");
            return false;
        }

        var symbol = order?.Symbol
            ?? sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol)?.Value
            ?? sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.SecurityID)?.Value;
        var orderQty = order?.OrderQty ?? ParseLong(sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty)?.Value);
        var side = order?.Side ?? (sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Side) is Fix.Field s ? (Fix.Dictionary.FieldValue?)s : null);

        if (string.IsNullOrEmpty(symbol))
            symbol = "UNKNOWN";
        if (orderQty <= 0)
            orderQty = 1;

        var message = new Fix.Message { MsgType = Fix.Dictionary.FIX_5_0SP2.Messages.ExecutionReport.MsgType };
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID, clOrdId);
        if (side is Fix.Dictionary.FieldValue sideValue)
            message.Fields.Set(sideValue);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol, symbol);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty, orderQty);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.OrdStatus.New);

        var orderId = order?.OrderID ?? info.NextOrderId++.ToString();
        if (order != null)
            order.OrderID ??= orderId;
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderID, orderId);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ExecID, info.NextExecId++.ToString());
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastQty, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastPx, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.CumQty, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.AvgPx, 0);

        if (info.BeginString != "FIX.4.0")
        {
            message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.ExecType.New);
            message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LeavesQty, orderQty);
        }

        if (info.BeginString.StartsWith("FIX.4."))
            message.Fields.Set(Fix.Dictionary.FIX_4_2.ExecTransType.New);

        return SendMessage(sessionId, message);
    }

    public bool RejectOrder(string sessionId, string clOrdId, string? reason = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var info) || info.Session == null)
            return false;

        var order = FindOrderForAction(info, clOrdId);
        var sourceMessage = order == null ? FindIncomingNewOrder(info, clOrdId) : null;
        if (order == null && sourceMessage == null)
        {
            AddLog(info, "Error", $"Reject failed: order {clOrdId} not found");
            return false;
        }

        var symbol = order?.Symbol
            ?? sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol)?.Value
            ?? sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.SecurityID)?.Value;
        var orderQty = order?.OrderQty ?? ParseLong(sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty)?.Value);
        var side = order?.Side ?? (sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Side) is Fix.Field s ? (Fix.Dictionary.FieldValue?)s : null);

        if (string.IsNullOrEmpty(symbol))
            symbol = "UNKNOWN";
        if (orderQty <= 0)
            orderQty = 1;

        var message = new Fix.Message { MsgType = Fix.Dictionary.FIX_5_0SP2.Messages.ExecutionReport.MsgType };
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID, clOrdId);
        if (side is Fix.Dictionary.FieldValue sideValue)
            message.Fields.Set(sideValue);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol, symbol);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty, orderQty);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.OrdStatus.Rejected);

        var orderId = order?.OrderID ?? info.NextOrderId++.ToString();
        if (order != null)
            order.OrderID ??= orderId;
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderID, orderId);
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

        var order = FindOrderForAction(info, clOrdId);
        var sourceMessage = order == null ? FindIncomingNewOrder(info, clOrdId) : null;
        if (order == null && sourceMessage == null)
        {
            AddLog(info, "Error", $"Fill failed: order {clOrdId} not found");
            return false;
        }

        var symbol = order?.Symbol
            ?? sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol)?.Value
            ?? sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.SecurityID)?.Value;
        var orderQty = order?.OrderQty ?? ParseLong(sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty)?.Value);
        var side = order?.Side ?? (sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Side) is Fix.Field s ? (Fix.Dictionary.FieldValue?)s : null);
        var currentCumQty = order?.CumQty ?? 0;
        var currentLeavesQty = order?.LeavesQty ?? orderQty;
        var basePrice = order?.Price ?? ParseDecimal(sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Price)?.Value) ?? 0;

        if (string.IsNullOrEmpty(symbol))
            symbol = "UNKNOWN";
        if (orderQty <= 0)
            orderQty = 1;

        var qty = fillQty ?? currentLeavesQty;
        var price = fillPrice ?? basePrice;
        var cumQty = currentCumQty + qty;
        var leavesQty = orderQty - cumQty;
        var isFull = leavesQty <= 0;

        var message = new Fix.Message { MsgType = Fix.Dictionary.FIX_5_0SP2.Messages.ExecutionReport.MsgType };
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID, clOrdId);
        if (side is Fix.Dictionary.FieldValue sideValue)
            message.Fields.Set(sideValue);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol, symbol);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty, orderQty);
        message.Fields.Set(isFull ? Fix.Dictionary.FIX_5_0SP2.OrdStatus.Filled : Fix.Dictionary.FIX_5_0SP2.OrdStatus.PartiallyFilled);

        var orderId = order?.OrderID ?? info.NextOrderId++.ToString();
        if (order != null)
            order.OrderID ??= orderId;
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderID, orderId);
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

        var order = FindOrderForAction(info, clOrdId);
        var sourceMessage = order == null ? FindIncomingNewOrder(info, clOrdId) : null;
        if (order == null && sourceMessage == null)
        {
            AddLog(info, "Error", $"Cancel failed: order {clOrdId} not found");
            return false;
        }

        var symbol = order?.Symbol
            ?? sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol)?.Value
            ?? sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.SecurityID)?.Value;
        var orderQty = order?.OrderQty ?? ParseLong(sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty)?.Value);
        var side = order?.Side ?? (sourceMessage?.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.Side) is Fix.Field s ? (Fix.Dictionary.FieldValue?)s : null);
        var cumQty = order?.CumQty ?? 0;
        var avgPx = order?.AvgPx ?? 0;

        if (string.IsNullOrEmpty(symbol))
            symbol = "UNKNOWN";
        if (orderQty <= 0)
            orderQty = 1;

        var message = new Fix.Message { MsgType = Fix.Dictionary.FIX_5_0SP2.Messages.ExecutionReport.MsgType };
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID, clOrdId);
        if (side is Fix.Dictionary.FieldValue sideValue)
            message.Fields.Set(sideValue);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.Symbol, symbol);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderQty, orderQty);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.OrdStatus.Canceled);

        var orderId = order?.OrderID ?? info.NextOrderId++.ToString();
        if (order != null)
            order.OrderID ??= orderId;
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderID, orderId);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ExecID, info.NextExecId++.ToString());
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastQty, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.LastPx, 0);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.CumQty, cumQty);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.AvgPx, avgPx);

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

        var order = FindOrderForAction(info, clOrdId);
        var sourceMessage = order == null ? FindIncomingNewOrder(info, clOrdId) : null;
        if (order == null && sourceMessage == null)
        {
            AddLog(info, "Error", $"RejectCancel failed: order {clOrdId} not found");
            return false;
        }

        var ordStatusValue = order?.PreviousOrdStatus?.Value ?? order?.OrdStatus?.Value ?? "0";
        var orderId = order?.OrderID ?? "NONE";
        var responseClOrdId = order?.NewClOrdID ?? order?.ClOrdID ?? clOrdId;

        var message = new Fix.Message { MsgType = Fix.Dictionary.FIX_5_0SP2.Messages.OrderCancelReject.MsgType };
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID, responseClOrdId);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrigClOrdID, clOrdId);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrderID, orderId);
        message.Fields.Set(Fix.Dictionary.FIX_5_0SP2.Fields.OrdStatus, ordStatusValue);
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

    static Order? FindOrderForAction(FixSessionInfo info, string clOrdId)
    {
        foreach (var order in info.OrderBook.Orders)
        {
            if (order.ClOrdID == clOrdId)
                return order;
        }

        foreach (var history in info.HistoryEntries.Where(x => x.Direction == "Incoming").Reverse())
        {
            if (string.IsNullOrWhiteSpace(history.Raw))
                continue;

            try
            {
                var message = new Fix.Message(history.Raw.Replace('|', '\x01'));
                if (message.MsgType != Fix.Dictionary.FIX_5_0SP2.Messages.NewOrderSingle.MsgType)
                    continue;

                var value = message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID)?.Value;
                if (!string.Equals(value, clOrdId, StringComparison.Ordinal))
                    continue;

                info.OrderBook.Process(message);

                foreach (var hydrated in info.OrderBook.Orders)
                {
                    if (hydrated.ClOrdID == clOrdId)
                        return hydrated;
                }
            }
            catch
            {
                // ignore malformed history records
            }
        }

        return null;
    }

    static Fix.Message? FindIncomingNewOrder(FixSessionInfo info, string clOrdId)
    {
        foreach (var history in info.HistoryEntries.Where(x => x.Direction == "Incoming").Reverse())
        {
            if (string.IsNullOrWhiteSpace(history.Raw))
                continue;

            try
            {
                var message = new Fix.Message(history.Raw.Replace('|', '\x01'));
                if (message.MsgType != Fix.Dictionary.FIX_5_0SP2.Messages.NewOrderSingle.MsgType)
                    continue;

                var value = message.Fields.Find(Fix.Dictionary.FIX_5_0SP2.Fields.ClOrdID)?.Value;
                if (string.Equals(value, clOrdId, StringComparison.Ordinal))
                    return message;
            }
            catch
            {
            }
        }

        return null;
    }

    static long ParseLong(string? value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            return result;

        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var decimalValue))
            return decimal.ToInt64(decimal.Truncate(decimalValue));

        return 0;
    }

    static decimal? ParseDecimal(string? value)
    {
        return decimal.TryParse(value, out var result) ? result : null;
    }
}
