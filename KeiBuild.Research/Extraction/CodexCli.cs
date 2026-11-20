using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Extraction;

namespace KeiBuild.Research.Extraction;

internal sealed class CodexCli(Func<ProcessStartInfo, string, CancellationToken, Task<CodexCli.CodexExit>> execute)
{
    internal CodexCli() : this(RunProcessAsync) { }

    internal async Task<(KnowledgeResponse Reply, string Events)> RunAsync(byte[] outputSchema, string prompt, bool discovery, CancellationToken ct)
    {
        var directory = Directory.CreateTempSubdirectory("keibuild-codex-");
        var schema = Path.Combine(directory.FullName, "schema.json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await CheckLoginAsync(directory.FullName, deadline.Token);

            await File.WriteAllBytesAsync(schema, outputSchema, deadline.Token);
            var info = StartInfo(directory.FullName, [
                "exec", "--ignore-user-config", "--strict-config", "--skip-git-repo-check", "--ephemeral",
                .. (OperatingSystem.IsWindows()
                    ? new[] { "-c", "windows.sandbox=\"mxc\"" }
                    : Array.Empty<string>()),
                "-c", "model_provider=\"openai\"", "-c", "approval_policy=\"never\"", "-c", "forced_login_method=\"chatgpt\"",
                "-c", "default_permissions=\"extract\"", "-c", FileProfile,
                "-c", NetworkProfile, "-c", discovery ? "web_search=\"live\"" : "web_search=\"disabled\"",
                "--disable", "shell_tool", "--disable", "unified_exec", "--disable", "plugins",
                "--disable", "apps", "--disable", "hooks", "--disable", "multi_agent", "--disable", "multi_agent_v2",
                "--disable", "browser_use", "--disable", "computer_use", .. (discovery ? new[] { "--enable", "code_mode_host", "--enable", "standalone_web_search", "--disable", "in_app_browser" } : new[] { "--disable", "code_mode_host" }),
                "--disable", "image_generation", "-c", "mcp_servers={}",
                "--output-schema", schema, "--json", "-"
            ]);
            var reply = await execute(info, prompt, deadline.Token);
            return (Parse(reply), reply.Output);
        }
        catch (Win32Exception ex)
        {
            throw new IngestionException("Cannot launch Codex CLI. Install the native codex executable on PATH.", ex) { HttpStatus = 403 };
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IngestionException("Codex request timed out.", ex) { IsTransient = true };
        }
        catch (IOException ex)
        {
            throw new IngestionException("Codex process I/O failed.", ex) { IsTransient = true };
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private const string FileProfile = "permissions.extract.filesystem={ \":workspace_roots\" = \"read\" }";
    private const string NetworkProfile = "permissions.extract.network.enabled=false";

    private async Task CheckLoginAsync(string directory, CancellationToken ct)
    {
        var login = await execute(StartInfo(directory, ["login", "status"]), "", ct);
        if (login.Code != 0 || !(login.Output + "\n" + login.Error).Split('\n', StringSplitOptions.TrimEntries)
                .Contains("Logged in using ChatGPT", StringComparer.OrdinalIgnoreCase))
            throw Pause("Codex requires a ChatGPT login. Run codex.cmd login.", 401);
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var directory = Directory.CreateTempSubdirectory("keibuild-check-");
        try
        {
            await CheckLoginAsync(directory.FullName, timeout.Token);
            if (OperatingSystem.IsWindows())
            {
                var check = await execute(StartInfo(directory.FullName, ["-c", "windows.sandbox=mxc", "-c", FileProfile,
                    "-c", NetworkProfile, "sandbox", "--include-managed-config", "--permission-profile", "extract",
                    "--", "cmd.exe", "/d", "/c", "echo", "MXC_OK"]), "", timeout.Token);
                if (check.Code != 0 || check.Output.Trim() != "MXC_OK")
                    throw Pause("Codex could not enforce the Windows read-only extraction profile. Check MXC support.", 403);
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    private static ProcessStartInfo StartInfo(string directory, IEnumerable<string> arguments)
    {
        var exe = Environment.GetEnvironmentVariable("KEIBUILD_CODEX_EXE");
        if (exe != null && (!Path.IsPathFullyQualified(exe) || OperatingSystem.IsWindows() &&
            !string.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase)))
            throw Pause("KEIBUILD_CODEX_EXE must be the full path to the native Codex executable.", 403);
        if (exe is null && OperatingSystem.IsWindows())
            exe = ResolveWindowsNative(Environment.GetEnvironmentVariable("PATH") ?? "", RuntimeInformation.OSArchitecture);
        var info = new ProcessStartInfo(exe ?? "codex") { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        // Inherit only OS, connectivity and CLI location settings. Provider and database credentials stay in the parent.
        info.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemDrive", "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE",
                     "LOCALAPPDATA", "APPDATA", "PROGRAMDATA", "CODEX_HOME", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY",
                     "SSL_CERT_FILE", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS" })
            if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return info;
    }

    internal static string? ResolveWindowsNative(string path, Architecture architecture)
    {
        var arch = architecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };
        foreach (var location in path.Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = location.Trim('"');
            if (!Path.IsPathFullyQualified(dir)) continue;
            var native = Path.Combine(dir, "codex.exe");
            if (File.Exists(native)) return native;
            if (arch == null || !File.Exists(Path.Combine(dir, "codex.cmd"))) continue;
            var package = Path.Combine(dir, "node_modules", "@openai", "codex");
            var platform = Path.Combine(package, "node_modules", "@openai", "codex-win32-" + arch);
            var triple = (arch == "arm64" ? "aarch64" : "x86_64") + "-pc-windows-msvc";
            foreach (var root in new[] { platform, package })
                foreach (var subdir in new[] { "bin", "codex" })
                {
                    var candidate = Path.Combine(root, "vendor", triple, subdir, "codex.exe");
                    if (File.Exists(candidate)) return candidate;
                }
        }
        return null;
    }

    internal sealed record CodexExit(int Code, string Output, string Error);

    internal static async Task<CodexExit> RunProcessAsync(ProcessStartInfo info, string input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new IngestionException("Codex process did not start.");
        using var cancellation = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // Already exited.
        });
        var stdout = CaptureAsync(process.StandardOutput, ct);
        var stderr = CaptureAsync(process.StandardError, ct);
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), ct);
        }
        catch (IOException) { /* An early exit can close stdin. Inspect exit code and stderr below. */ }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* Drain the terminating child. */ }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { /* Child closed its pipe first. */ }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
        ct.ThrowIfCancellationRequested();
        return new(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> CaptureAsync(StreamReader reader, CancellationToken ct)
    {
        const int cap = 2_000_000;
        var result = new StringBuilder();
        var buffer = new char[8192];
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            result.Append(buffer, 0, Math.Min(length, cap - result.Length));
        return result.ToString();
    }

    private static KnowledgeResponse Parse(CodexExit exit)
    {
        string? message = null;
        string? failure = null;
        var input = 0;
        var output = 0;
        var completed = false;
        var failed = false;
        try
        {
            foreach (var line in exit.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var json = JsonDocument.Parse(line);
                var e = json.RootElement;
                switch (e.GetProperty("type").GetString())
                {
                    case "item.completed" when e.GetProperty("item") is var item && item.GetProperty("type").GetString() == "agent_message":
                        message = item.GetProperty("text").GetString();
                        break;
                    case "turn.completed":
                        completed = true;
                        if (e.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                        {
                            input = Tokens(usage, "input_tokens");
                            output = Tokens(usage, "output_tokens");
                        }
                        break;
                    case "turn.failed":
                        failed = true;
                        failure = e.GetProperty("error").ToString();
                        break;
                    case "error":
                        failure = e.GetProperty("message").GetString();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            if (exit.Code == 0)
                throw new IngestionException("Codex returned malformed JSON events.", ex) { IsTransient = true };
        }
        if (exit.Code != 0 || failed || !completed || failure is not null && message is null)
        {
            var diagnostic = (failure + " " + exit.Error).ToLowerInvariant();
            var prefix = $"Codex exited with code {exit.Code}. ";
            if (new[] { "usage limit", "usage_limit", "rate limit", "rate_limit", "quota", "429", "out of credits" }.Any(diagnostic.Contains))
                throw Pause(prefix + "Provider usage limit reached. Check Codex usage before resuming.", 429);
            if (diagnostic.Contains("sandbox"))
                throw Pause(prefix + "Codex could not enforce the read-only extraction sandbox. Check Codex sandbox setup and managed permissions.", 403);
            if (new[] { "login", "logged in", "authenticat", "401", "403", "unexpected argument", "config", "unknown feature" }.Any(diagnostic.Contains))
                throw Pause(prefix + "Check Codex authentication, CLI version and managed permissions.", 403);
            throw new IngestionException(prefix + "Request did not complete.") { IsTransient = true };
        }
        if (message is null) throw new IngestionException("Codex returned no final message.") { IsTransient = true };
        return new(message, input, output);
    }

    private static int Tokens(JsonElement usage, string key) =>
        usage.TryGetProperty(key, out var count) && count.TryGetInt32(out var value) ? Math.Max(0, value) : 0;
    private static IngestionException Pause(string reason, int status) => new(reason) { HttpStatus = status };
}
