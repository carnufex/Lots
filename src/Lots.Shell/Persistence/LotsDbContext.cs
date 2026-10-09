using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Persistence;

public sealed class LotsDbContext(DbContextOptions<LotsDbContext> options) : DbContext(options)
{
    public DbSet<RunRecord> Runs => Set<RunRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunRecord>(e =>
        {
            e.ToTable("runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Prompt).IsRequired();
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
        });
    }
}

/// <summary>Placeholder for the persisted agent run; fleshed out in the agent loop slice.</summary>
public sealed class RunRecord
{
    public Guid Id { get; set; }
    public required string Prompt { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
