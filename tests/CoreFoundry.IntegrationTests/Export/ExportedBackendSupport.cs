using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MySqlConnector;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Export;

/// <summary>
/// Shared plumbing for tests that build and run an exported backend over HTTP: building and running it, process
/// management, waiting for it to come up, and small JSON/HTTP helpers. Shared by <see cref="ExportEndpointsTests"/>,
/// <see cref="AccessEndpointsTests"/> and <see cref="RealtimeEndpointsTests"/>.
/// </summary>
internal static class ExportedBackendSupport
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Unzips an export of <paramref name="solution"/>, builds it (0 warnings), checks its migration with <c>dotnet-ef</c>
    /// when that is on the PATH, runs it against a new MySQL database and calls <paramref name="use"/> with a client for it
    /// and the database's name. Then stops it and drops the database. A failed assertion carries the exported API's log.
    /// </summary>
    /// <param name="solution">The solution's name, e.g. <c>BookStore</c> (the zip holds <c>bookstore-backend/</c>).</param>
    public static async Task RunExportedBackendAsync(
        byte[] zip, string solution, string engineConnectionString, Func<HttpClient, string, Task> use, bool swagger = true)
    {
        var folder = Directory.CreateTempSubdirectory("cf-export-");
        var database = $"cf_p_{1_900_000_000L + Random.Shared.NextInt64(99_999_999)}";
        Process? api = null;
        try
        {
            await ZipFile.ExtractToDirectoryAsync(new MemoryStream(zip), folder.FullName, Ct);
            var root = Path.Combine(folder.FullName, $"{solution.ToLowerInvariant()}-backend");

            // It builds, without warnings.
            var build = await RunAsync("dotnet", $"build {solution}.slnx -c Release -nologo", root, TimeSpan.FromMinutes(6));
            build.ExitCode.ShouldBe(0, build.Output);
            build.Output.ShouldContain(" 0 Warning(s)", Case.Sensitive, build.Output);

            // The hand-written migration describes exactly the model the configurations build.
            if (FindOnPath("dotnet-ef") is { } ef)
            {
                var check = await RunAsync(ef,
                    $"migrations has-pending-model-changes --project src/{solution}.Infrastructure --startup-project src/{solution}.Api --no-build --configuration Release",
                    root, TimeSpan.FromMinutes(3));
                check.ExitCode.ShouldBe(0, check.Output);
                check.Output.ShouldContain("No changes have been made to the model since the last migration.");
            }

            // It runs: the migration creates its database on startup.
            var port = FreePort();
            var log = new StringBuilder();
            var environment = new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
                ["ConnectionStrings__Default"] = $"{engineConnectionString.TrimEnd(';')};Database={database}",
                ["Jwt__SigningKey"] = "an-export-test-signing-key-of-sufficient-length",
                ["Database__MigrateOnStartup"] = "true",
            };
            if (swagger)
            {
                environment["Swagger__Enabled"] = "true";
            }

            api = Start(log, Path.Combine(root, $"src/{solution}.Api/bin/Release/net10.0/{solution}.Api.dll"), environment);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            try
            {
                await WaitUntilHealthyAsync(http, api);
                await use(http, database);
            }
            catch (ShouldAssertException ex)
            {
                string output;
                lock (log)
                {
                    output = log.ToString();
                }

                throw new ShouldAssertException($"{ex.Message}\n--- exported API log ---\n{output}", ex);
            }
        }
        finally
        {
            if (api is { HasExited: false })
            {
                api.Kill(entireProcessTree: true);
            }

            api?.Dispose();
            await ExecuteAsync(engineConnectionString, $"DROP DATABASE IF EXISTS `{database}`");
            try
            {
                folder.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A build server may still hold a file; the temp folder is cleaned up by the OS.
            }
        }
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient http, string path, object body) =>
        http.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, Ct);

    public static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public static async Task WaitUntilHealthyAsync(HttpClient http, Process api)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            api.HasExited.ShouldBeFalse("The exported API stopped while starting.");
            try
            {
                if ((await http.GetAsync(new Uri("/health", UriKind.Relative), Ct)).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(500, Ct);
        }

        throw new ShouldAssertException("The exported API didn't become healthy within 90 seconds.");
    }

    public static Process Start(StringBuilder log, string dll, Dictionary<string, string> environment)
    {
        var info = new ProcessStartInfo("dotnet", $"\"{dll}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, line) => { lock (log) { log.AppendLine(line.Data); } };
        process.ErrorDataReceived += (_, line) => { lock (log) { log.AppendLine(line.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(string file, string arguments, string directory, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        var output = new StringBuilder();
        process.OutputDataReceived += (_, line) => { lock (output) { output.AppendLine(line.Data); } };
        process.ErrorDataReceived += (_, line) => { lock (output) { output.AppendLine(line.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    public static string? FindOnPath(string tool)
    {
        var names = OperatingSystem.IsWindows() ? [tool + ".exe"] : new[] { tool };
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools"));
        return folders.SelectMany(folder => names.Select(name => Path.Combine(folder, name))).FirstOrDefault(File.Exists);
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static async Task ExecuteAsync(string engineConnectionString, string sql)
    {
        await using var connection = new MySqlConnection(engineConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
