using CoreFoundry.Application.Projects;

namespace CoreFoundry.Api.Projects;

/// <summary>
/// Runs once at startup: finishes projects a crash left in Provisioning or Deleting.
/// Never blocks startup and never takes the app down; failures are logged and retried next start.
/// </summary>
internal sealed partial class ProjectRecoveryService(
    IServiceScopeFactory scopeFactory,
    ILogger<ProjectRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ProjectService>().RecoverAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
#pragma warning disable CA1031 // A recovery failure must not crash the host; it is logged and retried next start.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRecoveryFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Project recovery at startup failed; it will run again on the next start")]
    private partial void LogRecoveryFailed(Exception exception);
}
