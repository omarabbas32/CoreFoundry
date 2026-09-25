using CoreFoundry.Application.Export;
using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Infrastructure.Export;

/// <summary>
/// The realtime hub of an exported backend (phase-9-realtime-export.md): the Application port, the SignalR hub
/// with its table → read-level map, and the publisher that sends each saved change to the table's subscribers.
/// </summary>
internal static class RealtimeFiles
{
    public static IEnumerable<GeneratedFile> For(ExportModel model)
    {
        var n = model.Solution;

        yield return new($"src/{n}.Application/Realtime/IChangePublisher.cs", $$"""
            namespace {{n}}.Application.Realtime;

            /// <summary>A saved change to one row: sent as <c>{ table, operation, id }</c>. Not the row itself; clients refetch it.</summary>
            public sealed record ChangeEvent(string Table, ChangeOperation Operation, long Id);

            public enum ChangeOperation
            {
                Insert,
                Update,
                Delete,
            }

            /// <summary>Tells realtime subscribers about saved changes. Implemented with SignalR in Infrastructure.</summary>
            public interface IChangePublisher
            {
                /// <summary>Doesn't throw when sending fails: the change is saved already, so the request must not fail.</summary>
                Task PublishAsync(ChangeEvent change, CancellationToken cancellationToken);
            }

            """);

        var levels = model.Entities.Select(entity => $"[{CSharp.String(entity.Table)}] = Level.{LevelName(entity.Read)},");

        yield return new($"src/{n}.Infrastructure/Realtime/RealtimeHub.cs", $$"""
            using Microsoft.AspNetCore.SignalR;

            namespace {{n}}.Infrastructure.Realtime;

            /// <summary>
            /// Realtime notifications at <c>/hubs/realtime</c>: a client calls <c>Subscribe("books")</c> and then gets a
            /// <c>change</c> event for each row added, replaced or deleted through the API. Anyone may connect; each
            /// subscription needs the table's read level, like its <c>GET</c> endpoints. Checked when subscribing only.
            /// </summary>
            public sealed class RealtimeHub : Hub
            {
                /// <summary>Who may read, and so subscribe to, each table. Any other name is refused.</summary>
                private static readonly IReadOnlyDictionary<string, Level> Tables = new Dictionary<string, Level>(StringComparer.Ordinal)
                {
            {{CSharp.Lines(levels, 8)}}
                };

                private enum Level
                {
                    Public,
                    SignedIn,
                    Admin,
                }

                /// <summary>The group of a table's subscribers.</summary>
                public static string GroupName(string table) => $"table:{table}";

                /// <summary>Starts sending the table's changes to this connection. Subscribing twice is harmless.</summary>
                /// <exception cref="HubException">The table doesn't exist, or the caller may not read it.</exception>
                public async Task Subscribe(string table)
                {
                    if (table is null || !Tables.TryGetValue(table, out var level))
                    {
                        throw new HubException($"There is no table {table} to subscribe to.");
                    }

                    var refusal = level switch
                    {
                        Level.SignedIn when Context.User?.Identity?.IsAuthenticated != true => $"Sign in to subscribe to {table}.",
                        Level.Admin when Context.User?.IsInRole("Admin") != true => $"Only admins can subscribe to {table}.",
                        _ => null,
                    };
                    if (refusal is not null)
                    {
                        throw new HubException(refusal);
                    }

                    await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(table));
                }

                /// <summary>Stops sending the table's changes to this connection.</summary>
                /// <exception cref="HubException">The table doesn't exist.</exception>
                public async Task Unsubscribe(string table)
                {
                    if (table is null || !Tables.ContainsKey(table))
                    {
                        throw new HubException($"There is no table {table} to unsubscribe from.");
                    }

                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(table));
                }
            }

            """.Replace("{\n\n    };", "{\n    };", StringComparison.Ordinal));

        yield return new($"src/{n}.Infrastructure/Realtime/SignalRChangePublisher.cs", $$"""
            using Microsoft.AspNetCore.SignalR;
            using Microsoft.Extensions.Logging;
            using {{n}}.Application.Realtime;

            namespace {{n}}.Infrastructure.Realtime;

            /// <summary>
            /// Sends <c>change</c> with <c>{ table, operation, id }</c> to the table's subscribers. A failed or timed-out send
            /// is logged, never thrown: the row is saved already.
            /// </summary>
            internal sealed class SignalRChangePublisher(IHubContext<RealtimeHub> hub, ILogger<SignalRChangePublisher> logger) : IChangePublisher
            {
                /// <summary>How long one change may wait for its subscribers' connections before the send is given up.</summary>
                private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

                public async Task PublishAsync(ChangeEvent change, CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(change);
                    // Bounded: the send waits for every subscriber's connection, so one that stops reading would otherwise hold up every write to the table.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(SendTimeout);
                    try
                    {
                        await hub.Clients.Group(RealtimeHub.GroupName(change.Table))
                            .SendAsync("change", new { table = change.Table, operation = Name(change.Operation), id = change.Id }, timeout.Token);
                    }
                    catch (OperationCanceledException ex)
                    {
                        logger.LogWarning(ex, "Gave up sending the {Operation} of {Table} row {Id} to its subscribers after {Timeout}.", change.Operation, change.Table, change.Id, SendTimeout);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Couldn't send the {Operation} of {Table} row {Id} to its subscribers.", change.Operation, change.Table, change.Id);
                    }
                }

                private static string Name(ChangeOperation operation) => operation switch
                {
                    ChangeOperation.Insert => "insert",
                    ChangeOperation.Update => "update",
                    ChangeOperation.Delete => "delete",
                    _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
                };
            }

            """);
    }

    /// <summary>The generated hub's <c>Level</c> member for an access level.</summary>
    private static string LevelName(AccessLevel level) => level switch
    {
        AccessLevel.Public => "Public",
        AccessLevel.SignedIn => "SignedIn",
        AccessLevel.Admin => "Admin",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };
}
