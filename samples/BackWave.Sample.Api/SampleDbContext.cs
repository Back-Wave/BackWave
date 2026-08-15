using Microsoft.EntityFrameworkCore;

namespace BackWave.Sample.Api;

/// <summary>A demo business row written in the same transaction as a <c>tx-finalize</c> job.</summary>
public sealed class SampleBusinessRow
{
    public Guid Id { get; set; }
    public required string Note { get; set; }
}

/// <summary>
/// The host application's own EF Core unit of work, over the same relational database the
/// BackWave store uses. <c>POST /tx</c> opens a transaction here and BackWave's Transactional
/// Enqueue rides along on it — the business write and the job commit or roll back together.
/// </summary>
public sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public DbSet<SampleBusinessRow> BusinessRows => Set<SampleBusinessRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<SampleBusinessRow>().ToTable("sample_business");
}
