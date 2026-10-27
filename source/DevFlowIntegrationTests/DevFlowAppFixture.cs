using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace AvalonDock.DevFlowIntegrationTests;

/// <summary>Drives TestApp, the classic sample whose layout the docking tests exercise.</summary>
[CollectionDefinition("DevFlow")]
public sealed class DevFlowCollection : ICollectionFixture<TestAppFixture>
{
}

/// <summary>
/// Drives AvalonDockCodeApp (ToggleDockingManager). A separate application, a separate agent and a
/// separate port: the two samples carry different controls, so they cannot share one process.
/// </summary>
[CollectionDefinition("DevFlowCodeApp")]
public sealed class DevFlowCodeAppCollection : ICollectionFixture<CodeAppFixture>
{
}

public sealed class TestAppFixture : DevFlowAppFixture
{
    public TestAppFixture() : base("TestApp", 9223)
    {
    }
}

public sealed class CodeAppFixture : DevFlowAppFixture
{
    public CodeAppFixture() : base("AvalonDockCodeApp", 9224)
    {
    }
}

/// <summary>
/// Starts one sample application (Debug build, DevFlow agent on <see cref="AgentPort"/>) and keeps
/// it alive for the duration of a test collection, restarting it on demand.
/// </summary>
public class DevFlowAppFixture : IAsyncLifetime
{
    private Process _process;
    private static readonly string TestAppLogPathFormat = Path.Combine(
        OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp",
        "avalondock-devflow-{0}-{1}.log");
    private readonly object _logSync = new();
    private List<string> _hiddenAppNames = new();
    private readonly string _projectName;
    private readonly int _port;

    protected DevFlowAppFixture(string projectName, int port)
    {
        _projectName = projectName;
        _port = port;
        LogPath = string.Format(TestAppLogPathFormat, projectName.ToLowerInvariant(), port);
    }

    public int AgentPort => _port;
    public string LogPath { get; }

    private static string ResolveDotnetPath()
    {
        foreach (var candidate in new[]
        {
            "/usr/local/share/dotnet/dotnet",
            "/opt/homebrew/bin/dotnet",
            "/usr/local/share/dotnet/x64/dotnet",
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var envPath = Environment.GetEnvironmentVariable("PATH");
        if (envPath != null)
        {
            foreach (var dir in envPath.Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir, "dotnet");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }
        }

        throw new FileNotFoundException("dotnet not found on system PATH or at known locations.");
    }

    public async ValueTask InitializeAsync()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(DevFlowAppFixture).Assembly.Location);
        var testAppDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", _projectName));

        if (!Directory.Exists(testAppDir))
            throw new DirectoryNotFoundException(
                $"{_projectName} project not found at {testAppDir} (resolved from {assemblyDir})");

        var dotnetPath = ResolveDotnetPath();
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var noBuild = string.Equals(
            Environment.GetEnvironmentVariable("DEVFLOW_TESTAPP_NO_BUILD"),
            "1",
            StringComparison.Ordinal);
        File.WriteAllText(LogPath, $"{_projectName} log started {DateTimeOffset.Now:O}{Environment.NewLine}");

        void AppendLog(string stream, string line)
        {
            lock (_logSync)
            {
                File.AppendAllText(LogPath, $"[{stream}] {line}{Environment.NewLine}");
            }
        }

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = dotnetPath,
                Arguments = $"run {(noBuild ? "--no-build " : string.Empty)}--project \"{testAppDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment =
                {
                    ["DEVFLOW_AGENT_PORT"] = _port.ToString(),
                },
            },
        };

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
                AppendLog("stdout", e.Data);
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
                AppendLog("stderr", e.Data);
            }
        };

        try
        {
            _process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to start {_projectName}. Ensure .NET SDK is installed. Error: {ex.Message}", ex);
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                var error = errorBuilder.Length > 0
                    ? $"\nStderr:\n{errorBuilder}"
                    : "";
                throw new InvalidOperationException(
                    $"{_projectName} exited prematurely (exit code: {_process.ExitCode}). Log: {LogPath}.{error}");
            }

            try
            {
                var response = await httpClient.GetAsync($"http://localhost:{_port}/api/v1/agent/status");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch
            {
            }

            await Task.Delay(500);
        }

        var stderr = errorBuilder.Length > 0
            ? $"\nStderr output:\n{errorBuilder}"
            : "";

        throw new TimeoutException(
            $"{_projectName} did not start within 120 seconds on port {_port}. Log: {LogPath}.{stderr}");
    }

    public async ValueTask DisposeAsync()
    {
        await RestoreDesktopAsync();

        if (_process != null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { }
            await _process.WaitForExitAsync();
            _process.Dispose();
        }
    }

	internal async Task RestartAsync()
	{
		if (_process != null && !_process.HasExited)
		{
			try { _process.Kill(entireProcessTree: true); }
			catch { }
			await _process.WaitForExitAsync();
			_process.Dispose();
			_process = null;
		}

		// A new process has no mutation lease, and the id held for the old one would be rejected.
		DevFlowClient.ForgetLease(_port);

		await InitializeAsync();
	}

    // Native-input tests (see NativeInputEnvironment) drive REAL OS-level mouse events via cliclick,
    // at absolute screen coordinates. The per-test base has already positioned and guarded the main
    // window, so this must only activate it. Repositioning after the guard is armed can transiently
    // send the LibreWPF NSWindow off-screen. Hiding
    // other apps can mask bad drag-start coordinates by making an invalid screen point appear safe.
    internal async Task IsolateDesktopForNativeInputAsync()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            // Through DevFlowClient so the call carries the mutation lease (avd.activate moves the
            // app window, and the agent rejects any mutation that arrives without one).
            using var client = new DevFlowClient(_port);
            await client.InvokeAsync("avd.activate");
        }
        catch
        {
            // Best-effort: if System Events isn't scriptable here (no Accessibility permission, no
            // GUI session, etc.), fall through and let native-input tests fail/skip on their own
            // merits rather than blocking the whole fixture on desktop isolation.
        }
    }

    private async Task RestoreDesktopAsync()
    {
        if (_hiddenAppNames.Count == 0)
            return;

        foreach (var name in _hiddenAppNames)
        {
            try
            {
                await RunOsaScriptAsync(
                    $"tell application \"System Events\" to set visible of process \"{EscapeForAppleScript(name)}\" to true");
            }
            catch
            {
            }
        }

        _hiddenAppNames.Clear();
    }

    private static async Task<string> RunOsaScriptAsync(string script)
    {
        var psi = new ProcessStartInfo("osascript")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start osascript.");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return stdout.Trim();
    }

    private static string EscapeForAppleScript(string value) => value.Replace("\"", "\\\"");
}
