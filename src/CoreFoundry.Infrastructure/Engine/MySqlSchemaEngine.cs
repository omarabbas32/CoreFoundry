using System.Globalization;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.SchemaEngine;
using MySqlConnector;

namespace CoreFoundry.Infrastructure.Engine;

/// <summary>
/// Opens one dedicated <c>cf_engine</c> connection per apply and takes <c>GET_LOCK('cf_apply_{id}', 0)</c>
/// on it. MySQL locks belong to the session, so the connection stays pinned for the whole apply and
/// the lock is released on the same one.
/// </summary>
internal sealed class MySqlSchemaEngine(string engineConnectionString) : ISchemaEngine
{
    public async Task<IApplySession?> TryLockAsync(long projectId, CancellationToken cancellationToken)
    {
        // Pooling off: a pooled connection could come back already holding (or later leak) the lock.
        var connection = new MySqlConnection(new MySqlConnectionStringBuilder(engineConnectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("SELECT GET_LOCK(@name, 0)", connection);
            command.Parameters.AddWithValue("@name", LockName(projectId));
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            {
                await connection.DisposeAsync();
                return null;
            }

            return new Session(connection, LockName(projectId));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static string LockName(long projectId) => string.Create(CultureInfo.InvariantCulture, $"cf_apply_{projectId}");

    private sealed class Session(MySqlConnection connection, string lockName) : IApplySession
    {
        /// <summary>DDL on a big table can take a while; one statement may run up to 10 minutes.</summary>
        private const int StatementTimeoutSeconds = 600;

        public Task<SchemaModel> ReadSchemaAsync(string databaseName, CancellationToken cancellationToken) =>
            MySqlSchemaIntrospector.ReadAsync(connection, databaseName, cancellationToken);

        public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            try
            {
                await using var command = new MySqlCommand(sql, connection) { CommandTimeout = StatementTimeoutSeconds };
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (MySqlException ex)
            {
                throw new SchemaStatementFailedException($"MySQL error {ex.Number}: {ex.Message}", ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new MySqlCommand("SELECT RELEASE_LOCK(@name)", connection);
                command.Parameters.AddWithValue("@name", lockName);
                await command.ExecuteScalarAsync();
            }
            catch (MySqlException)
            {
                // Closing the session releases the lock anyway.
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
