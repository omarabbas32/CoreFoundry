using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;

namespace CoreFoundry.Infrastructure.HealthChecks;

/// <summary>Opens a connection with the given credentials and runs <c>SELECT 1</c>.</summary>
internal sealed class MySqlConnectionHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (MySqlException ex)
        {
            // The exception is logged by the health check service; only a generic
            // description is exposed in the HTTP response.
            return HealthCheckResult.Unhealthy("Cannot connect to MySQL.", ex);
        }
    }
}
