using FixClient.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace FixClient.Web.Data;

public class FixClientWebDbContext : DbContext
{
    public FixClientWebDbContext(DbContextOptions<FixClientWebDbContext> options) : base(options)
    {
    }

    public DbSet<PersistedSession> Sessions => Set<PersistedSession>();
    public DbSet<PersistedLogEntry> Logs => Set<PersistedLogEntry>();
    public DbSet<PersistedHistoryEntry> History => Set<PersistedHistoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PersistedSession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(32);
            entity.Property(x => x.SenderCompId).HasMaxLength(128);
            entity.Property(x => x.TargetCompId).HasMaxLength(128);
            entity.Property(x => x.Host).HasMaxLength(256);
            entity.Property(x => x.BeginString).HasMaxLength(32);
            entity.Property(x => x.DefaultApplVerId).HasMaxLength(32);
            entity.Property(x => x.Behaviour).HasMaxLength(32);
            entity.Property(x => x.OrderBehaviour).HasMaxLength(32);
            entity.Property(x => x.LogonBehaviour).HasMaxLength(32);
            entity.Property(x => x.SessionState).HasMaxLength(32);
        });

        modelBuilder.Entity<PersistedLogEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Level).HasMaxLength(16);
            entity.Property(x => x.Message).HasMaxLength(4000);
            entity.HasIndex(x => new { x.SessionId, x.Timestamp });
        });

        modelBuilder.Entity<PersistedHistoryEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Direction).HasMaxLength(16);
            entity.Property(x => x.MsgType).HasMaxLength(16);
            entity.Property(x => x.MsgTypeName).HasMaxLength(128);
            entity.Property(x => x.Summary).HasMaxLength(1024);
            entity.Property(x => x.Raw).HasMaxLength(8192);
            entity.HasIndex(x => new { x.SessionId, x.Timestamp });
        });

        modelBuilder.Entity<PersistedSession>()
            .HasMany(x => x.LogEntries)
            .WithOne()
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PersistedSession>()
            .HasMany(x => x.HistoryEntries)
            .WithOne()
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class PersistedSession
{
    public string Id { get; set; } = string.Empty;
    public string SenderCompId { get; set; } = string.Empty;
    public string TargetCompId { get; set; } = string.Empty;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string BeginString { get; set; } = "FIX.5.0SP2";
    public int HeartBtInt { get; set; }
    public string Behaviour { get; set; } = "Initiator";
    public string OrderBehaviour { get; set; } = "Initiator";
    public string LogonBehaviour { get; set; } = "Initiator";
    public string? DefaultApplVerId { get; set; }
    public int TestRequestDelay { get; set; }
    public bool BrokenNewSeqNo { get; set; }
    public bool NextExpectedMsgSeqNum { get; set; }
    public bool ValidateDataFields { get; set; }
    public int IncomingSeqNum { get; set; }
    public int OutgoingSeqNum { get; set; }
    public int TestRequestId { get; set; }
    public bool FragmentMessages { get; set; }
    public bool Enabled { get; set; }
    public string SessionState { get; set; } = "Disconnected";
    public int NextOrderId { get; set; }
    public int NextExecId { get; set; }

    public List<PersistedLogEntry> LogEntries { get; set; } = [];
    public List<PersistedHistoryEntry> HistoryEntries { get; set; } = [];
}

public class PersistedLogEntry
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
}

public class PersistedHistoryEntry
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string MsgType { get; set; } = string.Empty;
    public string MsgTypeName { get; set; } = string.Empty;
    public int SeqNum { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Raw { get; set; } = string.Empty;
}

public class SessionPersistenceStore
{
    readonly IDbContextFactory<FixClientWebDbContext> _factory;

    public SessionPersistenceStore(IDbContextFactory<FixClientWebDbContext> factory)
    {
        _factory = factory;
    }

    public async Task EnsureCreatedAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task<List<PersistedSession>> LoadAllAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Sessions
            .Include(s => s.LogEntries)
            .Include(s => s.HistoryEntries)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task UpsertSessionAsync(FixSessionInfo info)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var entity = await db.Sessions.FindAsync(info.Id);

        if (entity == null)
        {
            entity = new PersistedSession { Id = info.Id };
            db.Sessions.Add(entity);
        }

        entity.SenderCompId = info.SenderCompId;
        entity.TargetCompId = info.TargetCompId;
        entity.Host = info.Host;
        entity.Port = info.Port;
        entity.BeginString = info.BeginString;
        entity.HeartBtInt = info.HeartBtInt;
        entity.Behaviour = info.Behaviour.ToString();
        entity.OrderBehaviour = info.OrderBehaviour.ToString();
        entity.LogonBehaviour = info.LogonBehaviour.ToString();
        entity.DefaultApplVerId = info.DefaultApplVerId;
        entity.TestRequestDelay = info.TestRequestDelay;
        entity.BrokenNewSeqNo = info.BrokenNewSeqNo;
        entity.NextExpectedMsgSeqNum = info.NextExpectedMsgSeqNum;
        entity.ValidateDataFields = info.ValidateDataFields;
        entity.IncomingSeqNum = info.IncomingSeqNum;
        entity.OutgoingSeqNum = info.OutgoingSeqNum;
        entity.TestRequestId = info.TestRequestId;
        entity.FragmentMessages = info.FragmentMessages;
        entity.Enabled = info.Enabled;
        entity.SessionState = info.SessionState.ToString();
        entity.NextOrderId = info.NextOrderId;
        entity.NextExecId = info.NextExecId;

        await db.SaveChangesAsync();
    }

    public async Task AddLogAsync(string sessionId, LogEntry entry)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Logs.Add(new PersistedLogEntry
        {
            SessionId = sessionId,
            Timestamp = ToUtc(entry.Timestamp),
            Level = entry.Level,
            Message = entry.Message
        });
        await db.SaveChangesAsync();
    }

    public async Task AddHistoryAsync(string sessionId, HistoryEntry entry)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.History.Add(new PersistedHistoryEntry
        {
            SessionId = sessionId,
            Timestamp = ToUtc(entry.Timestamp),
            Direction = entry.Direction,
            MsgType = entry.MsgType,
            MsgTypeName = entry.MsgTypeName,
            SeqNum = entry.SeqNum,
            Summary = entry.Summary,
            Raw = entry.Raw
        });
        await db.SaveChangesAsync();
    }

    static DateTime ToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
        };
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var session = await db.Sessions.FindAsync(sessionId);
        if (session == null)
            return;

        db.Sessions.Remove(session);
        await db.SaveChangesAsync();
    }
}
