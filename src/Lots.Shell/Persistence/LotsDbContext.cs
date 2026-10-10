using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Persistence;

public class LotsDbContext(DbContextOptions<LotsDbContext> options) : DbContext(options)
{
    public DbSet<RunRecord> Runs => Set<RunRecord>();
    public DbSet<RunMessageRecord> RunMessages => Set<RunMessageRecord>();
    public DbSet<RunStepRecord> RunSteps => Set<RunStepRecord>();
    public DbSet<ApprovalRecord> Approvals => Set<ApprovalRecord>();
    public DbSet<AuditRecord> AuditLog => Set<AuditRecord>();
    public DbSet<VoiceUsageRecord> VoiceUsage => Set<VoiceUsageRecord>();
    public DbSet<UserSettingsRecord> UserSettings => Set<UserSettingsRecord>();
    public DbSet<UserVocabularyRecord> UserVocabulary => Set<UserVocabularyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunRecord>(e =>
        {
            e.ToTable("runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Prompt).IsRequired();
            e.Property(x => x.Profile).HasMaxLength(128);
            e.Property(x => x.UserId).HasMaxLength(256);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ConversationId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Status);
            e.Property(x => x.LeaseOwner).HasMaxLength(256);
            e.HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RunMessageRecord>(e =>
        {
            e.ToTable("run_messages");
            e.HasKey(x => new { x.RunId, x.Seq });
            e.Property(x => x.Role).HasMaxLength(16).IsRequired();
        });

        modelBuilder.Entity<ApprovalRecord>(e =>
        {
            e.ToTable("approvals");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RunId, x.ToolCallId }).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.ToolName).HasMaxLength(256).IsRequired();
            e.Property(x => x.ToolCallId).HasMaxLength(256).IsRequired();
            e.Property(x => x.RequestedBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.DecidedBy).HasMaxLength(256);
        });

        modelBuilder.Entity<UserVocabularyRecord>(e =>
        {
            e.ToTable("user_vocabulary");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(256);
        });

        modelBuilder.Entity<UserSettingsRecord>(e =>
        {
            e.ToTable("user_settings");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.VoiceId).HasMaxLength(64);
        });

        modelBuilder.Entity<VoiceUsageRecord>(e =>
        {
            e.ToTable("voice_usage");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.At });
            e.Property(x => x.UserId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Direction).HasMaxLength(8).IsRequired();
            e.Property(x => x.Language).HasMaxLength(8);
            e.Property(x => x.Provider).HasMaxLength(256);
            e.Property(x => x.Outcome).HasMaxLength(16).IsRequired();
        });

        modelBuilder.Entity<AuditRecord>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.At });
            e.HasIndex(x => x.RunId);
            e.Property(x => x.Decision).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Tool).HasMaxLength(256).IsRequired();
            e.Property(x => x.Profile).HasMaxLength(128);
            e.Property(x => x.ApproverId).HasMaxLength(256);
            e.Property(x => x.BackendAuth).HasMaxLength(64);
            e.Property(x => x.ResultStatus).HasMaxLength(16);
        });

        modelBuilder.Entity<RunStepRecord>(e =>
        {
            e.ToTable("run_steps");
            e.HasKey(x => new { x.RunId, x.Seq });
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
        });
    }
}

public enum RunStatus { Pending, Running, WaitingForApproval, Completed, Failed }

/// <summary>A persisted agent run: a job that is created, stepped, persisted and resumable.</summary>
public sealed class RunRecord
{
    public Guid Id { get; set; }
    public required string Prompt { get; set; }
    public string Profile { get; set; } = "";
    /// <summary>The run is part of a spoken conversation: short plain answers, no long reasoning phase.</summary>
    public bool Voice { get; set; }
    /// <summary>Runs with the same id are the turns of one conversation; earlier turns are context for later ones.</summary>
    public Guid? ConversationId { get; set; }
    public string UserId { get; set; } = "";
    /// <summary>Comma-separated roles the run acts with, fixed when the run starts.</summary>
    public string Roles { get; set; } = "";
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? FinalAnswer { get; set; }
    public string? Error { get; set; }
    /// <summary>Worker currently executing the run, and until when (unix ms). See RunLeases.</summary>
    public string? LeaseOwner { get; set; }
    public long? LeaseUntilMs { get; set; }
    /// <summary>The user's login token, encrypted, kept only for runs that use delegated servers; cleared when the run ends.</summary>
    public string? SubjectTokenProtected { get; set; }
    public DateTimeOffset? SubjectTokenExpiresAt { get; set; }
    public List<RunMessageRecord> Messages { get; set; } = [];
    public List<RunStepRecord> Steps { get; set; } = [];
}

/// <summary>One message of the run's conversation, in order. Written after every step.</summary>
public sealed class RunMessageRecord
{
    public Guid RunId { get; set; }
    public int Seq { get; set; }
    public required string Role { get; set; }
    public string? Content { get; set; }
    /// <summary>JSON array of tool calls for assistant messages.</summary>
    public string? ToolCallsJson { get; set; }
    public string? ToolCallId { get; set; }
}

public enum StepKind { ModelCall, ToolCall }

/// <summary>Trace entry: one model call or one tool call of a run.</summary>
public sealed class RunStepRecord
{
    public Guid RunId { get; set; }
    public int Seq { get; set; }
    public StepKind Kind { get; set; }
    /// <summary>Model name for model calls, tool name for tool calls.</summary>
    public required string Name { get; set; }
    public string? ToolCallId { get; set; }
    public string? ArgumentsJson { get; set; }
    /// <summary>Truncated result (tool output or model reply text).</summary>
    public string? Result { get; set; }
    public long LatencyMs { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum ApprovalStatus { Pending, Approved, Denied }

/// <summary>A tool call that policy let through only with approval. The run waits until it is decided.</summary>
public sealed class ApprovalRecord
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public required string ToolCallId { get; set; }
    public required string ToolName { get; set; }
    public string? ArgumentsJson { get; set; }
    public required string RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Comment { get; set; }
}

public enum AuditDecision { Allowed, Denied, ApprovalRequested, ApprovalGranted, ApprovalRefused, ApprovalDenied }

/// <summary>
/// Append-only accountability record: who did what under which profile version, what policy decided and
/// who approved. Rows are only ever inserted. Not the trace (that is for debugging).
/// </summary>
public sealed class AuditRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string UserId { get; set; }
    public string Roles { get; set; } = "";
    public string Profile { get; set; } = "";
    public int ProfileVersion { get; set; }
    public Guid RunId { get; set; }
    public required string Tool { get; set; }
    public string? ArgumentsJson { get; set; }
    public AuditDecision Decision { get; set; }
    public string Reason { get; set; } = "";
    public string? ApproverId { get; set; }
    /// <summary>Backend auth strategy (ADR 0004) used for the call, if it was executed.</summary>
    public string? BackendAuth { get; set; }
    /// <summary>ok / error for executed calls, null otherwise.</summary>
    public string? ResultStatus { get; set; }
}

/// <summary>
/// One speech call (ADR 0013). Metadata only: never the audio, never the text. Stt = dictation, Tts = a spoken answer.
/// </summary>
public sealed class VoiceUsageRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string UserId { get; set; }
    public required string Direction { get; set; }
    public string? Language { get; set; }
    public double? AudioSeconds { get; set; }
    public int? Characters { get; set; }
    public long LatencyMs { get; set; }
    public string? Provider { get; set; }
    public required string Outcome { get; set; }
    public Guid? RunId { get; set; }
}

/// <summary>A user's own dictation vocabulary (names, products, jargon), one row per user. Words only; no audio, no secrets.</summary>
public sealed class UserVocabularyRecord
{
    public required string UserId { get; set; }
    /// <summary>JSON array of words.</summary>
    public string WordsJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; }
}


/// <summary>
/// A user's personal settings (ADR 0015): how the agent behaves in text and voice (0..100 sliders; 50 = no instruction)
/// and, optionally, the user's own voice. The clip itself lives only in the voice service; here is its id and consent.
/// </summary>
public sealed class UserSettingsRecord
{
    public required string UserId { get; set; }
    public int Talkativeness { get; set; } = 50;
    public int Warmth { get; set; } = 50;
    public int Formality { get; set; } = 50;
    public int Expressiveness { get; set; } = 60;
    public int Pace { get; set; } = 40;
    /// <summary>Voice registered with the voice service for this user; null = the deployment's voice.</summary>
    public string? VoiceId { get; set; }
    public double? VoiceSeconds { get; set; }
    /// <summary>When the user confirmed that the recording is their own voice.</summary>
    public DateTimeOffset? VoiceConsentAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
