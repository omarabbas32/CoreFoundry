using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoreFoundry.Infrastructure.Persistence.Configurations;

internal sealed class ProjectTableConfiguration : IEntityTypeConfiguration<ProjectTable>
{
    public void Configure(EntityTypeBuilder<ProjectTable> builder)
    {
        builder.ToTable("ProjectTables");
        builder.HasKey(table => table.Id);

        builder.Property(table => table.Name).HasMaxLength(ProjectTable.NameMaxLength).IsRequired();
        builder.Property(table => table.AppliedName).HasMaxLength(ProjectTable.NameMaxLength);
        builder.HasIndex(table => new { table.ProjectId, table.Name }).IsUnique();

        // Bumped by every change to the table or its columns; a stale Version makes the save fail (409).
        builder.Property(table => table.Version).IsConcurrencyToken();

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(table => table.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(table => table.Columns)
            .WithOne()
            .HasForeignKey(column => column.TableId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(table => table.Columns).HasField("_columns");

        builder.Ignore(table => table.IsApplied);
    }
}

internal sealed class ProjectColumnConfiguration : IEntityTypeConfiguration<ProjectColumn>
{
    public void Configure(EntityTypeBuilder<ProjectColumn> builder)
    {
        builder.ToTable("ProjectColumns");
        builder.HasKey(column => column.Id);

        builder.Property(column => column.Name).HasMaxLength(ProjectColumn.NameMaxLength).IsRequired();
        builder.Property(column => column.AppliedName).HasMaxLength(ProjectColumn.NameMaxLength);
        builder.Property(column => column.DefaultValue).HasMaxLength(ProjectColumn.DefaultValueMaxLength);
        builder.HasIndex(column => new { column.TableId, column.Name }).IsUnique();

        // A reference to another draft table. The Domain refuses to delete a referenced table, so the
        // cascade only runs when a whole project (all its tables) is deleted.
        builder.HasOne<ProjectTable>()
            .WithMany()
            .HasForeignKey(column => column.ReferencesTableId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(column => column.IsApplied);
        builder.Ignore(column => column.Default);
        builder.Ignore(column => column.Definition);
    }
}

internal sealed class SchemaMigrationConfiguration : IEntityTypeConfiguration<SchemaMigration>
{
    public void Configure(EntityTypeBuilder<SchemaMigration> builder)
    {
        builder.ToTable("SchemaMigrations");
        builder.HasKey(migration => migration.Id);

        // Not unique: a failed attempt and its retry target the same version (applies are serialized by GET_LOCK).
        builder.HasIndex(migration => new { migration.ProjectId, migration.Version });

        builder.Property(migration => migration.StatementsJson).HasColumnType("json").IsRequired();
        builder.Property(migration => migration.SnapshotJson).HasColumnType("json");
        builder.Property(migration => migration.Error).HasColumnType("text");

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(migration => migration.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // History stays when the user who applied it is deleted.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(migration => migration.RequestedBy)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
