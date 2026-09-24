using System.Text.RegularExpressions;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Projects;
using MySqlConnector;

namespace CoreFoundry.Infrastructure.Engine;

/// <summary>
/// Creates and drops <c>cf_p_{Id}</c> databases as <c>cf_engine</c>, whose grants only cover <c>cf_p\_%</c>.
/// </summary>
internal sealed partial class MySqlProjectDatabaseProvisioner(string engineConnectionString) : IProjectDatabaseProvisioner
{
    public Task CreateDatabaseAsync(string databaseName, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"CREATE DATABASE IF NOT EXISTS `{Checked(databaseName)}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci",
            cancellationToken);

    public Task DropDatabaseAsync(string databaseName, CancellationToken cancellationToken) =>
        ExecuteAsync($"DROP DATABASE IF EXISTS `{Checked(databaseName)}`", cancellationToken);

    /// <summary>
    /// Identifiers can't be sent as parameters, so the name is checked against the only shape
    /// CoreFoundry ever generates. It comes from <c>Project.DatabaseName</c>, never from a request;
    /// this is the second line of defense.
    /// </summary>
    private static string Checked(string databaseName) =>
        DatabaseNamePattern().IsMatch(databaseName)
            ? databaseName
            : throw new ArgumentException($"'{databaseName}' is not a CoreFoundry project database name.", nameof(databaseName));

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new MySqlConnection(engineConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException ex)
        {
            throw new DatabaseProvisioningException($"MySQL error {ex.ErrorCode}: {ex.Message}", ex);
        }
    }

    [GeneratedRegex(@"^cf_p_[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNamePattern();
}
