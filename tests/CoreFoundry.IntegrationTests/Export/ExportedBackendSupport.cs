using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MySqlConnector;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Export;

/// <summary>
/// Shared plumbing for tests that build and run an exported backend over HTTP: process management, waiting
/// for it to come up, and small JSON/HTTP helpers. Shared by <see cref="ExportEndpointsTests"/> and
/// <see cref="AccessEndpointsTests"/>.
/// </summary>
internal static class ExportedBackendSupport
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
