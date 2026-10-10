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
    public DbSet<KnowledgeConflictRecord> KnowledgeConflicts => Set<KnowledgeConflictRecord>();
    public DbSet<ConfigResourceRecord> ConfigResources => Set<ConfigResourceRecord>();
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();
    public DbSet<UserCredentialRecord> UserCredentials => Set<UserCredentialRecord>();
    public DbSet<UserProfileRecord> UserProfiles => Set<UserProfileRecord>();
    public DbSet<QuotaOverrideRecord> QuotaOverrides => Set<QuotaOverrideRecord>();
    public DbSet<ConversationAudioRecord> ConversationAudio => Set<ConversationAudioRecord>();
    public DbSet<ConversationRecord> Conversations => Set<ConversationRecord>();
    public DbSet<AuditForwardStateRecord> AuditForwardState => Set<AuditForwardStateRecord>();
    public DbSet<ConfigVersionRecord> ConfigVersions => Set<ConfigVersionRecord>();
    public DbSet<ConflictVoteRecord> ConflictVotes => Set<ConflictVoteRecord>();
    public DbSet<UserSettingsRecord> UserSettings => Set<UserSettingsRecord>();
    public DbSet<UserVocabularyRecord> UserVocabulary => Set<UserVocabularyRecord>();
    public DbSet<ApiTokenRecord> ApiTokens => Set<ApiTokenRecord>();
    public DbSet<VoiceConsentRecord> VoiceConsents => Set<VoiceConsentRecord>();
    public DbSet<ScheduleFireRecord> ScheduleFires => Set<ScheduleFireRecord>();
    public DbSet<AttachmentRecord> Attachments => Set<AttachmentRecord>();
    public DbSet<ChannelEventRecord> ChannelEvents => Set<ChannelEventRecord>();
    public DbSet<MemoryRecord> Memories => Set<MemoryRecord>();
    public DbSet<FeedbackRecord> Feedback => Set<FeedbackRecord>();
    public DbSet<Lots.Shell.Core.Outcomes.RunOutcomeRecord> RunOutcomes => Set<Lots.Shell.Core.Outcomes.RunOutcomeRecord>();
    public DbSet<Lots.Shell.Core.Mining.EvalCandidateRecord> EvalCandidates => Set<Lots.Shell.Core.Mining.EvalCandidateRecord>();
    public DbSet<Lots.Shell.Core.Proposals.ProposalRecord> Proposals => Set<Lots.Shell.Core.Proposals.ProposalRecord>();

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
            e.Property(x => x.Sensitivity).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.Status);
            e.Property(x => x.LeaseOwner).HasMaxLength(256);
            e.Property(x => x.CancelRequestedBy).HasMaxLength(256);
            e.Property(x => x.TraceId).HasMaxLength(32);
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
            e.Property(x => x.ApprovedBy).HasMaxLength(1024);
            e.Property(x => x.Risk).HasMaxLength(16);
            e.HasIndex(x => new { x.Status, x.ExpiresAt });
        });

        modelBuilder.Entity<ConversationAudioRecord>(e =>
        {
            e.ToTable("conversation_audio");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ConversationId);
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.UserId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(8).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<ConversationRecord>(e =>
        {
            e.ToTable("conversations");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200);
        });

        modelBuilder.Entity<QuotaOverrideRecord>(e =>
        {
            e.ToTable("quota_overrides");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.SetBy).HasMaxLength(256);
        });

        modelBuilder.Entity<UserProfileRecord>(e =>
        {
            e.ToTable("user_profiles");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.Email).HasMaxLength(320);
        });

        modelBuilder.Entity<UserCredentialRecord>(e =>
        {
            e.ToTable("user_credentials");
            e.HasKey(x => new { x.UserId, x.Server });
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.Server).HasMaxLength(128);
        });

        modelBuilder.Entity<NotificationRecord>(e =>
        {
            e.ToTable("notification_outbox");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SentAt, x.NextAttemptAt });
            e.Property(x => x.Event).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<ConfigResourceRecord>(e =>
        {
            e.ToTable("config_resources");
            e.HasKey(x => new { x.Kind, x.Name });
            e.Property(x => x.Kind).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.ManagedBy).HasMaxLength(16).IsRequired();
            e.Property(x => x.AppliedBy).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<ConfigVersionRecord>(e =>
        {
            e.ToTable("config_versions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Kind, x.Name, x.AppliedAt });
            e.HasIndex(x => x.AppliedAt);
            e.Property(x => x.Kind).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.ManagedBy).HasMaxLength(16).IsRequired();
            e.Property(x => x.AppliedBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.Action).HasMaxLength(16).IsRequired();
        });

        modelBuilder.Entity<KnowledgeConflictRecord>(e =>
        {
            e.ToTable("knowledge_conflicts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Fingerprint).IsUnique();
            e.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.ResolvedBy).HasMaxLength(256);
        });

        modelBuilder.Entity<ConflictVoteRecord>(e =>
        {
            e.ToTable("conflict_votes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ConflictId, x.UserId }).IsUnique(); // one vote per user per conflict
            e.HasIndex(x => new { x.UserId, x.At });
            e.Property(x => x.UserId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Option).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<UserVocabularyRecord>(e =>
        {
            e.ToTable("user_vocabulary");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(256);
        });

        modelBuilder.Entity<MemoryRecord>(e =>
        {
            e.ToTable("memories");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.Text).HasMaxLength(1000);
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.Profile).HasMaxLength(128);
        });

        modelBuilder.Entity<Lots.Shell.Core.Outcomes.RunOutcomeRecord>(e =>
        {
            e.ToTable("run_outcomes");
            e.HasKey(x => x.RunId);
            e.HasIndex(x => new { x.Profile, x.ProfileVersion, x.EndedAt });
            e.HasIndex(x => x.EndedAt);
            e.HasIndex(x => x.Dirty);
            e.Property(x => x.UserHash).HasMaxLength(32);
            e.Property(x => x.Profile).HasMaxLength(128);
            e.Property(x => x.Model).HasMaxLength(256);
            e.Property(x => x.Channel).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.JudgeRubric).HasMaxLength(64);
            e.Property(x => x.TraceId).HasMaxLength(32);
            e.HasOne<RunRecord>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade); // goes with its run
        });

        modelBuilder.Entity<Lots.Shell.Core.Proposals.ProposalRecord>(e =>
        {
            e.ToTable("proposals");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Profile, x.CreatedAt });
            e.Property(x => x.Profile).HasMaxLength(128);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Rationale).HasMaxLength(8000);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.Property(x => x.DecidedBy).HasMaxLength(256);
            e.Property(x => x.PrUrl).HasMaxLength(1000);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<Lots.Shell.Core.Mining.EvalCandidateRecord>(e =>
        {
            e.ToTable("eval_candidates");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ClusterKey).IsUnique();
            e.HasIndex(x => new { x.State, x.Impact });
            e.Property(x => x.ClusterKey).HasMaxLength(512);
            e.Property(x => x.Signature).HasMaxLength(400);
            e.Property(x => x.Profile).HasMaxLength(128);
            e.Property(x => x.Problem).HasMaxLength(64);
            e.Property(x => x.ReviewedBy).HasMaxLength(256);
            e.Property(x => x.ReviewNote).HasMaxLength(2000);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<FeedbackRecord>(e =>
        {
            e.ToTable("feedback");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RunId, x.UserId }).IsUnique(); // one rating per user per answer
            e.HasIndex(x => new { x.State, x.CreatedAt });
            e.Property(x => x.UserId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.Property(x => x.ReviewedBy).HasMaxLength(256);
            e.Property(x => x.ReviewNote).HasMaxLength(2000);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            // Feedback goes with its run (retention, erasure).
            e.HasOne<RunRecord>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChannelEventRecord>(e =>
        {
            e.ToTable("channel_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(256);
        });

        modelBuilder.Entity<AttachmentRecord>(e =>
        {
            e.ToTable("attachments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.FileName).HasMaxLength(256);
            e.Property(x => x.ContentType).HasMaxLength(128);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Sha256).HasMaxLength(64);
        });

        modelBuilder.Entity<ScheduleFireRecord>(e =>
        {
            e.ToTable("schedule_fires");
            e.HasKey(x => new { x.Name, x.DueAt }); // one row per occurrence: the claim that makes exactly one replica fire it
            e.Property(x => x.Name).HasMaxLength(128);
        });

        modelBuilder.Entity<VoiceConsentRecord>(e =>
        {
            e.ToTable("voice_consents");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.At });
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.VoiceId).HasMaxLength(64);
            e.Property(x => x.Event).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Statement).HasMaxLength(512);
        });

        modelBuilder.Entity<ApiTokenRecord>(e =>
        {
            e.ToTable("api_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Hash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.Hint).HasMaxLength(32);
            e.Property(x => x.Scopes).HasMaxLength(128);
            e.Property(x => x.Roles).HasMaxLength(1024);
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
            e.HasIndex(x => x.ConversationId);
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
            e.Property(x => x.ToolCallId).HasMaxLength(256);
            e.Property(x => x.Decision).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Tool).HasMaxLength(256).IsRequired();
            e.Property(x => x.Profile).HasMaxLength(128);
            e.Property(x => x.ApproverId).HasMaxLength(256);
            e.Property(x => x.BackendAuth).HasMaxLength(64);
            e.Property(x => x.ResultStatus).HasMaxLength(16);
            e.HasIndex(x => x.Seq).IsUnique();
            e.Property(x => x.PrevHash).HasMaxLength(64);
            e.Property(x => x.Hash).HasMaxLength(64);
        });

        modelBuilder.Entity<AuditForwardStateRecord>(e =>
        {
            e.ToTable("audit_forward_state");
            e.HasKey(x => x.Target);
            e.Property(x => x.Target).HasMaxLength(32);
        });

        modelBuilder.Entity<RunStepRecord>(e =>
        {
            e.ToTable("run_steps");
            e.HasKey(x => new { x.RunId, x.Seq });
            e.Property(x => x.Endpoint).HasMaxLength(128);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
        });
    }
}

public enum RunStatus { Pending, Running, WaitingForApproval, Completed, Failed, Cancelled }

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
    /// <summary>Someone asked to stop the run. The worker holding it stops within about a second and marks it Cancelled.</summary>
    public DateTimeOffset? CancelRequestedAt { get; set; }
    public string? CancelRequestedBy { get; set; }
    /// <summary>The failed or cancelled run this one was started again from.</summary>
    public Guid? RetryOf { get; set; }
    /// <summary>
    /// The run has seen tool output that looked like an injected instruction (#85): from then on its write and destructive tool calls
    /// need an approval even where the role would allow them directly.
    /// </summary>
    public bool Tainted { get; set; }
    /// <summary>What started the run without a person (#101): <c>schedule:&lt;name&gt;</c>, <c>webhook:&lt;name&gt;</c>, <c>manual:&lt;name&gt;</c>.</summary>
    public string? Trigger { get; set; }
    /// <summary>Who besides the owner and admins may read the run: <c>,role:operator,user:bob,</c> (#101).</summary>
    public string? Viewers { get; set; }
    /// <summary>The run that delegated this one to another profile (#103); null for top-level runs.</summary>
    public Guid? ParentRunId { get; set; }
    /// <summary>0 for a top-level run, 1 for a sub-run, ...</summary>
    public int Depth { get; set; }
    /// <summary>A lower model-call limit than Agent:MaxSteps, for sub-runs (#103).</summary>
    public int? StepLimit { get; set; }
    /// <summary>Where the answer goes for a run asked in a channel (#107): Slack channel and thread, or a mail address.</summary>
    public string? ReplyJson { get; set; }
    /// <summary>Files given with the prompt (#105): <c>[{"id","name","kind"}]</c>.</summary>
    public string? AttachmentsJson { get; set; }
    /// <summary>Where the result is delivered when the run ends (e-mail, webhooks), as JSON (#101).</summary>
    public string? DeliverJson { get; set; }
    /// <summary>The answer the model is writing right now (#95), for readers on other replicas; null between model calls.</summary>
    public string? Partial { get; set; }
    /// <summary>A model alias chosen for this run instead of the profile's (#119, model comparison); only roles in Models:ChooseRoles.</summary>
    public string? ModelAlias { get; set; }
    /// <summary>A reasoning effort chosen for this run (#119); null leaves it to the model or the voice settings.</summary>
    public string? ReasoningEffort { get; set; }
    /// <summary>The highest data class the run has read (#89). Model calls go only to endpoints cleared for it.</summary>
    public Lots.Shell.Core.Policy.DataClass Sensitivity { get; set; }
    /// <summary>OpenTelemetry trace of the run's first execution (#76): resumed executions link to it.</summary>
    public string? TraceId { get; set; }
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
    /// <summary>Ids of image attachments that belong to this (user) message (#105); the bytes stay in <c>attachments</c>.</summary>
    public string? ImagesJson { get; set; }
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
    /// <summary>For tool calls: the output looked like an injected instruction and was flagged for the model (#85).</summary>
    public bool Flagged { get; set; }
    /// <summary>For model calls: the configured endpoint that answered (after any fallback).</summary>
    public string? Endpoint { get; set; }
    /// <summary>For model calls: why the call left the run's model alias (data classification, #89).</summary>
    public string? Routing { get; set; }
    public long LatencyMs { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum ApprovalStatus { Pending, Approved, Denied, Expired }

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
    /// <summary>Distinct approvers needed (2 for two-person risk classes, #74); the requester never counts.</summary>
    public int RequiredApprovals { get; set; } = 1;
    /// <summary>Approvers so far (comma separated) while more are needed.</summary>
    public string ApprovedBy { get; set; } = "";
    /// <summary>Undecided after this, the request expires and counts as refused.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Risk { get; set; }
}

/// <summary>One stored audio clip of a conversation (ADR 0014): kind user (what was said) or agent (the spoken answer).</summary>
public sealed class ConversationAudioRecord
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? RunId { get; set; }
    public required string UserId { get; set; }
    public required string Kind { get; set; }
    public required string ContentType { get; set; }
    public int Bytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A conversation's generated title and summary (#80). The turns themselves are its runs.</summary>
public sealed class ConversationRecord
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset? SummarizedAt { get; set; }
}

/// <summary>An admin's per-user quota override (#78); fields left empty keep the role/profile limits.</summary>
public sealed class QuotaOverrideRecord
{
    public required string UserId { get; set; }
    public int? RunsPerMinute { get; set; }
    public int? ConcurrentRuns { get; set; }
    public long? TokensPerDay { get; set; }
    public int? ToolCallsPerRun { get; set; }
    public double? SpeechSecondsPerDay { get; set; }
    public string? SetBy { get; set; }
    public DateTimeOffset SetAt { get; set; }

    public Lots.Shell.Core.Quotas.QuotaLimits Limits() => new()
    {
        RunsPerMinute = RunsPerMinute, ConcurrentRuns = ConcurrentRuns, TokensPerDay = TokensPerDay,
        ToolCallsPerRun = ToolCallsPerRun, SpeechSecondsPerDay = SpeechSecondsPerDay,
    };
}

/// <summary>
/// What the shell knows about a person from their login (#136): the e-mail claim (for approval notifications) and the roles they
/// last had. Refreshed on use; deleted with the user's data (#79).
/// </summary>
public sealed class UserProfileRecord
{
    public required string UserId { get; set; }
    public string? Email { get; set; }
    public string Roles { get; set; } = "";
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>A user's own connected account at a backend (#62): encrypted tokens, never shown or shared.</summary>
public sealed class UserCredentialRecord
{
    public required string UserId { get; set; }
    public required string Server { get; set; }
    public string? AccessTokenProtected { get; set; }
    public string? RefreshTokenProtected { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Scope { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Outbox of notifications (#74): written with the change that causes them, delivered with retries by NotificationWorker.</summary>
public sealed class NotificationRecord
{
    public Guid Id { get; set; }
    public required string Event { get; set; }
    /// <summary>JSON payload: no tool arguments or results, only what an approver needs to find the request.</summary>
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }
}

public enum AuditDecision
{
    Allowed, Denied, ApprovalRequested, ApprovalGranted, ApprovalRefused, ApprovalDenied,
    /// <summary>A model call went to another alias because of the run's data class (#89); Tool is "model:&lt;alias&gt;".</summary>
    ModelRerouted,
    /// <summary>No model endpoint was cleared for the run's data; the run stopped (#89).</summary>
    ModelBlocked,
}

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
    /// <summary>The model's id for the call; links the decision to its trace step. Null on rows written before it existed.</summary>
    public string? ToolCallId { get; set; }
    public string? ArgumentsJson { get; set; }
    public AuditDecision Decision { get; set; }
    public string Reason { get; set; } = "";
    public string? ApproverId { get; set; }
    /// <summary>Backend auth strategy (ADR 0004) used for the call, if it was executed.</summary>
    public string? BackendAuth { get; set; }
    /// <summary>ok / error for executed calls, null otherwise.</summary>
    public string? ResultStatus { get; set; }
    /// <summary>Position in the hash chain (#81), assigned by the sealer; null until sealed.</summary>
    public long? Seq { get; set; }
    public string? PrevHash { get; set; }
    public string? Hash { get; set; }
}

/// <summary>How far audit forwarding to one target got (#81): it resumes after LastSeq.</summary>
public sealed class AuditForwardStateRecord
{
    public required string Target { get; set; }
    public long LastSeq { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }
    public string? LastError { get; set; }
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
    /// <summary>The conversation a dictated turn or spoken answer belongs to (history and timing).</summary>
    public Guid? ConversationId { get; set; }
    /// <summary>Total time of the call. For speech output <see cref="LatencyMs"/> is only time to first audio.</summary>
    public long? DurationMs { get; set; }
}

/// <summary>A declarative resource applied through the admin API (#66): its current spec (YAML) and who manages it (api or gitops).</summary>
public sealed class ConfigResourceRecord
{
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public required string ManagedBy { get; set; }
    public int Version { get; set; }
    public string Spec { get; set; } = "";
    public required string AppliedBy { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
}

/// <summary>Append-only history of every admin change: who, what, the spec after it (null when deleted). Before = the previous row.</summary>
public sealed class ConfigVersionRecord
{
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public int Version { get; set; }
    public string? Spec { get; set; }
    public required string ManagedBy { get; set; }
    public required string AppliedBy { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    /// <summary>create | update | delete</summary>
    public required string Action { get; set; }
}

/// <summary>
/// Retrieved passages that contradict each other (#48). Options are chunk ids with the content hash of their document at detection
/// time: when a document changes, votes for it no longer count. A signal for owners, never an authority (no effect on policy).
/// </summary>
public sealed class KnowledgeConflictRecord
{
    public Guid Id { get; set; }
    /// <summary>Hash of the sorted option chunk ids: the same disagreement is recorded once.</summary>
    public required string Fingerprint { get; set; }
    public string Question { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>JSON array of {chunkId, sourceId, documentId, title, contentHash}.</summary>
    public string OptionsJson { get; set; } = "[]";
    public DateTimeOffset DetectedAt { get; set; }
    public Guid? RunId { get; set; }
    /// <summary>open | resolved</summary>
    public string Status { get; set; } = "open";
    public string? ResolvedOption { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>One user's vote on a conflict (append-only; a changed vote replaces the row's option and time).</summary>
public sealed class ConflictVoteRecord
{
    public Guid Id { get; set; }
    public Guid ConflictId { get; set; }
    public required string UserId { get; set; }
    public string Roles { get; set; } = "";
    /// <summary>A chunk id of one option, or "neither".</summary>
    public required string Option { get; set; }
    /// <summary>Content hash of the chosen option's document when the vote was cast.</summary>
    public string? ContentHash { get; set; }
    public DateTimeOffset At { get; set; }
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
public enum VoiceConsentEvent { Given, Withdrawn, Revoked, Erased }

/// <summary>Something a user wants Lots to remember about them (#99). Used only once <see cref="ConfirmedAt"/> is set.</summary>
public sealed class MemoryRecord
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Text { get; set; }
    /// <summary>user: written by the user; agent: suggested by the agent with the remember tool.</summary>
    public required string Source { get; set; }
    /// <summary>The profile of the run that suggested it (provenance).</summary>
    public string? Profile { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
}

public enum FeedbackState { Open, Resolved, Converted }

/// <summary>
/// A user's rating of an answer (#121): thumbs up (+1) or down (-1) and an optional comment. Reviewers resolve it or turn it into an
/// eval case (<see cref="CaseJson"/>, the expected behaviour they wrote), so a bad answer becomes a regression test.
/// </summary>
public sealed class FeedbackRecord
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public required string UserId { get; set; }
    public int Rating { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public FeedbackState State { get; set; } = FeedbackState.Open;
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    /// <summary>The eval case made from this feedback, as JSON in the dataset format (docs/evals.md).</summary>
    public string? CaseJson { get; set; }
}

/// <summary>A channel event (Slack event id, mail message id) that has been handled (#107): retries and duplicates are dropped.</summary>
public sealed class ChannelEventRecord
{
    public required string Id { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A file a user gave a run (#105): the bytes, and for documents the extracted text.</summary>
public sealed class AttachmentRecord
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required string Kind { get; set; }
    public long Size { get; set; }
    public required string Sha256 { get; set; }
    public required byte[] Data { get; set; }
    public string? Text { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A schedule occurrence that has been fired (#101).</summary>
public sealed class ScheduleFireRecord
{
    public required string Name { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset FiredAt { get; set; }
    public Guid? RunId { get; set; }
}

/// <summary>
/// The own-voice consent trail (#93), append-only: when a user gave consent for a recording (and to which statement), withdrew it,
/// had it revoked by an admin, or had it erased with their data. No audio, only who, when and what. Kept like the audit log
/// (<c>Retention:AuditDays</c>) as the record that the voice was used with consent.
/// </summary>
public sealed class VoiceConsentRecord
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public string? VoiceId { get; set; }
    public VoiceConsentEvent Event { get; set; }
    /// <summary>Who caused the event: the user, or the admin who revoked it.</summary>
    public required string Actor { get; set; }
    public DateTimeOffset At { get; set; }
    /// <summary>For <see cref="VoiceConsentEvent.Given"/>: the statement the user confirmed.</summary>
    public string? Statement { get; set; }
    public double? Seconds { get; set; }
}

/// <summary>A personal API token (#88). Only the SHA-256 hash of the token is stored.</summary>
public sealed class ApiTokenRecord
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Name { get; set; }
    public required string Hash { get; set; }
    /// <summary>The first characters, so a user can tell their tokens apart.</summary>
    public required string Hint { get; set; }
    public required string Scopes { get; set; }
    /// <summary>The creator's roles when the token was made: the most it can ever do.</summary>
    public required string Roles { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

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
    /// <summary>Out of office until then (#136): approval notifications for this user go to <see cref="DelegateTo"/>.</summary>
    public DateTimeOffset? AwayUntil { get; set; }
    public string? DelegateTo { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
