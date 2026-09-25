using CoreFoundry.Application.Export;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Infrastructure.Export;
using Shouldly;

namespace CoreFoundry.UnitTests.Export;

/// <summary>The realtime hub of the exported backend (phase-9-realtime-export.md §1–§3).</summary>
public class RealtimeGeneratorTests
{
    /// <summary>Bookshop with M8's levels: books Public/Admin, authors Admin/Admin, categories left at SignedIn/SignedIn.</summary>
    private static readonly IReadOnlyList<GeneratedFile> Files = new DotNetBackendGenerator().Generate(BackendGeneratorTests.Bookshop() with
    {
        Entities = [.. BackendGeneratorTests.Bookshop().Entities.Select(entity => entity.Table switch
        {
            "books" => entity with { Read = AccessLevel.Public, Write = AccessLevel.Admin },
            "authors" => entity with { Read = AccessLevel.Admin, Write = AccessLevel.Admin },
            _ => entity,
        })],
    });

    private static string File(string path) =>
        Files.SingleOrDefault(file => file.Path == path)?.Content ?? throw new ShouldAssertException($"{path} wasn't generated.");

    [Fact]
    public void The_port_has_the_event_and_operation_and_no_SignalR_types()
    {
        var port = File("src/Bookshop.Application/Realtime/IChangePublisher.cs");
        port.ShouldContain("namespace Bookshop.Application.Realtime;");
        port.ShouldContain("public sealed record ChangeEvent(string Table, ChangeOperation Operation, long Id);");
        port.ShouldContain("public enum ChangeOperation\n{\n    Insert,\n    Update,\n    Delete,\n}");
        port.ShouldContain("public interface IChangePublisher");
        port.ShouldContain("Task PublishAsync(ChangeEvent change, CancellationToken cancellationToken);");
        port.ShouldNotContain("using Microsoft.AspNetCore");
    }

    [Fact]
    public void CrudService_publishes_once_after_each_successful_save()
    {
        var crud = File("src/Bookshop.Application/Common/CrudService.cs");
        crud.ShouldContain("using Bookshop.Application.Realtime;");
        crud.ShouldContain("public abstract class CrudService<TEntity, TDto, TInput>(IRepository<TEntity> repository, IChangePublisher changes)");
        crud.ShouldContain("protected abstract string TableName { get; }");

        // Publish follows the save: a save that throws never reaches it.
        crud.ShouldContain("repository.Add(entity);\n        await repository.SaveChangesAsync(cancellationToken);\n        await PublishAsync(ChangeOperation.Insert, entity.Id);\n");
        crud.ShouldContain("Apply(input, entity, replace: true);\n        await repository.SaveChangesAsync(cancellationToken);\n        await PublishAsync(ChangeOperation.Update, entity.Id);\n");
        crud.ShouldContain("repository.Remove(await FindAsync(id, cancellationToken));\n        await repository.SaveChangesAsync(cancellationToken);\n        await PublishAsync(ChangeOperation.Delete, id);\n");
        crud.Split("await PublishAsync(").Length.ShouldBe(4); // once in each of the three writes
        crud.ShouldContain("changes.PublishAsync(new ChangeEvent(TableName, operation, id)");
    }

    [Theory]
    [InlineData("Book", "books")]
    [InlineData("Author", "authors")]
    [InlineData("Category", "categories")]
    public void Each_service_names_its_table_and_passes_the_publisher_on(string className, string table)
    {
        var service = File($"src/Bookshop.Application/Tables/{className}/{className}Service.cs");
        service.ShouldContain("using Bookshop.Application.Realtime;");
        service.ShouldContain($"public sealed class {className}Service(IRepository<{className}> repository, IChangePublisher changes)");
        service.ShouldContain($": CrudService<{className}, {className}Dto, {className}Input>(repository, changes)");
        service.ShouldContain($"protected override string TableName => \"{table}\";");
    }

    [Fact]
    public void The_hub_maps_each_table_to_its_read_level()
    {
        var hub = File("src/Bookshop.Infrastructure/Realtime/RealtimeHub.cs");
        hub.ShouldContain("namespace Bookshop.Infrastructure.Realtime;");
        hub.ShouldContain("public sealed class RealtimeHub : Hub");
        hub.ShouldContain("[\"authors\"] = Level.Admin,");
        hub.ShouldContain("[\"books\"] = Level.Public,");
        hub.ShouldContain("[\"categories\"] = Level.SignedIn,");
        hub.ShouldNotContain("[\"cf_users\"]"); // accounts aren't a table of the API
        hub.ShouldNotContain("[Authorize"); // the level is checked per Subscribe, not per connection
    }

    [Fact]
    public void Subscribe_refuses_unknown_tables_and_checks_the_level_against_the_caller()
    {
        var hub = File("src/Bookshop.Infrastructure/Realtime/RealtimeHub.cs");
        hub.ShouldContain("public async Task Subscribe(string table)");
        hub.ShouldContain("public async Task Unsubscribe(string table)");
        hub.ShouldContain("if (table is null || !Tables.TryGetValue(table, out var level))");
        hub.ShouldContain("throw new HubException(");
        hub.ShouldContain("Level.SignedIn when Context.User?.Identity?.IsAuthenticated != true =>");
        hub.ShouldContain("Level.Admin when Context.User?.IsInRole(\"Admin\") != true =>");
        hub.ShouldContain("$\"Sign in to subscribe to {table}.\"");
        hub.ShouldContain("$\"Only admins can subscribe to {table}.\"");
        hub.ShouldContain("await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(table));");
        hub.ShouldContain("await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(table));");
        hub.ShouldContain("public static string GroupName(string table) => $\"table:{table}\";");
    }

    [Fact]
    public void The_publisher_sends_change_to_the_table_group_and_logs_failures()
    {
        var publisher = File("src/Bookshop.Infrastructure/Realtime/SignalRChangePublisher.cs");
        publisher.ShouldContain("IHubContext<RealtimeHub> hub");
        publisher.ShouldContain(": IChangePublisher");
        publisher.ShouldContain("hub.Clients.Group(RealtimeHub.GroupName(change.Table))");
        publisher.ShouldContain(".SendAsync(\"change\", new { table = change.Table, operation = Name(change.Operation), id = change.Id }, cancellationToken);");
        publisher.ShouldContain("ChangeOperation.Insert => \"insert\",");
        publisher.ShouldContain("ChangeOperation.Update => \"update\",");
        publisher.ShouldContain("ChangeOperation.Delete => \"delete\",");
        publisher.ShouldContain("catch (Exception ex)");
        publisher.ShouldContain("logger.LogError(ex,");
    }

    [Fact]
    public void Infrastructure_registers_SignalR_and_the_publisher()
    {
        var di = File("src/Bookshop.Infrastructure/DependencyInjection.cs");
        di.ShouldContain("services.AddSignalR();");
        di.ShouldContain("services.AddSingleton<IChangePublisher, SignalRChangePublisher>();");
    }

    [Fact]
    public void Program_maps_the_hub_anonymously_and_closes_connections_when_the_token_expires()
    {
        var program = File("src/Bookshop.Api/Program.cs");
        program.ShouldContain("using Bookshop.Infrastructure.Realtime;");
        program.ShouldContain(
            "app.MapHub<RealtimeHub>(\"/hubs/realtime\", options => options.CloseOnAuthenticationExpiration = true).AllowAnonymous();");
    }

    [Fact]
    public void Program_reads_access_token_from_the_query_string_only_under_hubs()
    {
        var program = File("src/Bookshop.Api/Program.cs");
        program.ShouldContain("OnMessageReceived = context =>");
        program.ShouldContain("context.HttpContext.Request.Path.StartsWithSegments(\"/hubs\")");
        program.ShouldContain("context.Request.Query[\"access_token\"]");
        program.Split("Query[").Length.ShouldBe(2); // the one query-string read
    }

    [Fact]
    public void Program_adds_CORS_only_with_allowed_origins_with_credentials_before_authentication()
    {
        var program = File("src/Bookshop.Api/Program.cs");
        program.ShouldContain("builder.Configuration.GetSection(\"Cors:AllowedOrigins\").Get<string[]>() ?? []");
        program.ShouldContain("policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()");
        program.ShouldNotContain("AllowAnyOrigin()");
        program.ShouldContain("if (corsOrigins.Length > 0)\n{\n    app.UseCors();\n}\n\napp.UseAuthentication();");
        program.IndexOf("app.UseCors();", StringComparison.Ordinal)
            .ShouldBeLessThan(program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal));
    }

    [Fact]
    public void Appsettings_has_an_empty_CORS_origin_list()
    {
        var settings = File("src/Bookshop.Api/appsettings.json");
        settings.ShouldContain("\"Cors\": {\n    \"AllowedOrigins\": []\n  }");
        System.Text.Json.JsonDocument.Parse(settings).RootElement.GetProperty("Cors").GetProperty("AllowedOrigins").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void The_README_has_a_Realtime_section_with_the_snippet_and_the_subscribable_tables()
    {
        var readme = File("README.md");
        readme.ShouldContain("\n## Realtime\n");
        var realtime = readme[readme.IndexOf("## Realtime", StringComparison.Ordinal)..readme.IndexOf("## Tables", StringComparison.Ordinal)];

        realtime.ShouldContain("import { HubConnectionBuilder } from \"@microsoft/signalr\";");
        realtime.ShouldContain("  .withUrl(\"http://localhost:8080/hubs/realtime\", { accessTokenFactory: () => token })\n  .withAutomaticReconnect()\n");
        realtime.ShouldContain("  for (const table of tables) await connection.invoke(\"Subscribe\", table);\n  refreshEverything(); // events sent while disconnected are lost\n");
        realtime.ShouldContain("| Table | Who may subscribe |");
        realtime.ShouldContain("| `books` | Public |");
        realtime.ShouldContain("| `authors` | Admin |");
        realtime.ShouldContain("| `categories` | Signed-in |");
        realtime.ShouldContain("`Cors:AllowedOrigins`");
        realtime.ShouldContain("proxy access logs");
        realtime.ShouldContain("WebSocket upgrades");
        realtime.ShouldContain("backplane");
        realtime.ShouldContain("Only this API's writes send events.");
        realtime.ShouldContain("Cascades send no event");
        realtime.ShouldContain("checked when subscribing");
    }
}
