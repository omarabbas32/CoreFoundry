using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CoreFoundry.Infrastructure.Persistence;

/// <summary>
/// CoreFoundry's own metadata in the <c>corefoundry</c> database, connected as <c>cf_meta</c>.
/// Project databases (<c>cf_p_*</c>) are never accessed through EF Core.
/// </summary>
public sealed class MetadataDbContext(DbContextOptions<MetadataDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectTable> ProjectTables => Set<ProjectTable>();
    public DbSet<ProjectColumn> ProjectColumns => Set<ProjectColumn>();
    public DbSet<SchemaMigration> SchemaMigrations => Set<SchemaMigration>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MetadataDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // All timestamps are UTC with microsecond precision: DATETIME(6).
        // MySQL doesn't store DateTimeKind, so values are re-tagged as UTC when read.
        configurationBuilder.Properties<DateTime>().HavePrecision(6).HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HavePrecision(6).HaveConversion<UtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        toDatabase => toDatabase,
        fromDatabase => DateTime.SpecifyKind(fromDatabase, DateTimeKind.Utc));
}
