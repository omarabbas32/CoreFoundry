using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Schema;
using Shouldly;

namespace CoreFoundry.UnitTests.Domain;

public class SchemaMigrationTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    private static SchemaMigration ThreeStatements() =>
        new(projectId: 7, version: 1, statementsJson: """["a","b","c"]""", statementCount: 3, requestedBy: 1);

    [Fact]
    public void New_migration_is_pending_with_no_progress()
    {
        var migration = ThreeStatements();

        migration.Status.ShouldBe(MigrationStatus.Pending);
        migration.StatementsApplied.ShouldBe(0);
    }

    [Fact]
    public void Applied_after_all_statements_succeed()
    {
        var migration = ThreeStatements();
        migration.RecordProgress(1);
        migration.RecordProgress(2);
        migration.RecordProgress(3);

        migration.MarkApplied("""{"tables":[]}""", Now);

        migration.Status.ShouldBe(MigrationStatus.Applied);
        migration.CompletedAt.ShouldBe(Now);
    }

    [Fact]
    public void Cannot_be_marked_applied_before_every_statement_ran()
    {
        var migration = ThreeStatements();
        migration.RecordProgress(2);

        Should.Throw<DomainException>(() => migration.MarkApplied("{}", Now));
    }

    [Fact]
    public void Failure_keeps_the_progress_reached()
    {
        var migration = ThreeStatements();
        migration.RecordProgress(1);

        migration.MarkFailed("Duplicate entry for key uq_books_isbn", Now);

        migration.Status.ShouldBe(MigrationStatus.Failed);
        migration.StatementsApplied.ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Progress_must_stay_within_the_statement_count(int applied) =>
        Should.Throw<DomainException>(() => ThreeStatements().RecordProgress(applied));

    [Fact]
    public void A_finished_migration_cannot_change()
    {
        var migration = ThreeStatements();
        migration.MarkFailed("boom", Now);

        Should.Throw<DomainException>(() => migration.RecordProgress(1));
    }
}
