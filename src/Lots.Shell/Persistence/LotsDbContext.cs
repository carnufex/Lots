using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Persistence;

public sealed class LotsDbContext(DbContextOptions<LotsDbContext> options) : DbContext(options)
{
    public DbSet<RunRecord> Runs => Set<RunRecord>();
    public DbSet<RunMessageRecord> RunMessages => Set<RunMessageRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunRecord>(e =>
        {
            e.ToTable("runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Prompt).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Status);
            e.HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RunMessageRecord>(e =>
        {
            e.ToTable("run_messages");
            e.HasKey(x => new { x.RunId, x.Seq });
            e.Property(x => x.Role).HasMaxLength(16).IsRequired();
        });
    }
}

public enum RunStatus { Pending, Running, Completed, Failed }

/// <summary>A persisted agent run: a job that is created, stepped, persisted and resumable.</summary>
public sealed class RunRecord
{
    public Guid Id { get; set; }
    public required string Prompt { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? FinalAnswer { get; set; }
    public string? Error { get; set; }
    public List<RunMessageRecord> Messages { get; set; } = [];
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
