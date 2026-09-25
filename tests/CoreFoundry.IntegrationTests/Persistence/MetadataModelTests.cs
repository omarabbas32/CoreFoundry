using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.Users;
using CoreFoundry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Persistence;

/// <summary>
/// Checks the EF model against the data model doc (docs/corefoundry-erd.html).
/// Builds the model only; no database connection is opened.
/// </summary>
public sealed class MetadataModelTests : IDisposable
{
    private readonly MetadataDbContext _context = new(
        new DbContextOptionsBuilder<MetadataDbContext>()
            .UseMySQL("Server=127.0.0.1;Database=model_only;User=none;Password=none")
            .Options);

    private IModel Model => _context.Model;

    public void Dispose() => _context.Dispose();

    [Fact]
    public void Has_the_seven_metadata_tables() =>
        Model.GetEntityTypes().Select(entity => entity.GetTableName()).Order().ShouldBe(
        [
            "ProjectColumns", "ProjectMembers", "ProjectTables", "Projects",
            "RefreshTokens", "SchemaMigrations", "Users",
        ]);

    [Fact]
    public void Project_database_name_is_not_a_column() =>
        Model.FindEntityType(typeof(Project))!.FindProperty(nameof(Project.DatabaseName)).ShouldBeNull();

    [Fact]
    public void Project_members_have_a_composite_key() =>
        Model.FindEntityType(typeof(ProjectMember))!.FindPrimaryKey()!.Properties
            .Select(property => property.Name)
            .ShouldBe([nameof(ProjectMember.ProjectId), nameof(ProjectMember.UserId)]);

    [Theory]
    [InlineData(typeof(User), new[] { nameof(User.Email) })]
    [InlineData(typeof(RefreshToken), new[] { nameof(RefreshToken.TokenHash) })]
    [InlineData(typeof(Project), new[] { nameof(Project.Slug) })]
    [InlineData(typeof(ProjectTable), new[] { nameof(ProjectTable.ProjectId), nameof(ProjectTable.Name) })]
    [InlineData(typeof(ProjectColumn), new[] { nameof(ProjectColumn.TableId), nameof(ProjectColumn.Name) })]
    public void Has_unique_index(Type entity, string[] columns) =>
        Model.FindEntityType(entity)!.GetIndexes()
            .Where(index => index.IsUnique)
            .Select(index => index.Properties.Select(property => property.Name).ToArray())
            .ShouldContain(unique => unique.SequenceEqual(columns));

    [Fact]
    public void Migration_versions_repeat_for_retries_of_a_failed_apply() =>
        Model.FindEntityType(typeof(SchemaMigration))!.GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name).SequenceEqual([nameof(SchemaMigration.ProjectId), nameof(SchemaMigration.Version)]))
            .IsUnique.ShouldBeFalse();

    [Theory]
    [InlineData(typeof(RefreshToken), nameof(RefreshToken.UserId), DeleteBehavior.Cascade)]
    [InlineData(typeof(RefreshToken), nameof(RefreshToken.ReplacedByTokenId), DeleteBehavior.SetNull)]
    [InlineData(typeof(Project), nameof(Project.OwnerId), DeleteBehavior.Restrict)]
    [InlineData(typeof(ProjectMember), nameof(ProjectMember.ProjectId), DeleteBehavior.Cascade)]
    [InlineData(typeof(ProjectMember), nameof(ProjectMember.UserId), DeleteBehavior.Cascade)]
    [InlineData(typeof(ProjectTable), nameof(ProjectTable.ProjectId), DeleteBehavior.Cascade)]
    [InlineData(typeof(ProjectColumn), nameof(ProjectColumn.TableId), DeleteBehavior.Cascade)]
    [InlineData(typeof(SchemaMigration), nameof(SchemaMigration.ProjectId), DeleteBehavior.Cascade)]
    [InlineData(typeof(SchemaMigration), nameof(SchemaMigration.RequestedBy), DeleteBehavior.SetNull)]
    public void Foreign_key_delete_rule(Type entity, string column, DeleteBehavior expected) =>
        Model.FindEntityType(entity)!.GetForeignKeys()
            .Single(key => key.Properties.Single().Name == column)
            .DeleteBehavior.ShouldBe(expected);

    [Fact]
    public void Every_timestamp_is_stored_with_microsecond_precision() =>
        Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
            .ShouldAllBe(property => property.GetPrecision() == 6);
}
