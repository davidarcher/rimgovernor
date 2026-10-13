using System.Diagnostics;
using System.Net;

namespace RimGovernor.Launcher;

/// <summary>What the launcher page polls for the Observability section.</summary>
/// <param name="State">stopped, starting or healthy.</param>
/// <param name="Problem">A plain line when Docker is missing, its daemon is down or the last Start or Stop failed; empty otherwise.</param>
/// <param name="Dashboards">The deep links the page shows while the stack is healthy.</param>
public sealed record ObsView(string State, string Problem, IReadOnlyList<DashboardLink> Dashboards);

/// <summary>Runs a command to completion and returns its combined output.</summary>
public interface ICommandRunner
{
    /// <summary>Runs the command; returns its combined output, and throws <see cref="LauncherException"/> when it cannot run or exits nonzero.</summary>
    Task<string> RunAsync(string name, IReadOnlyList<string> args, CancellationToken ct);

    /// <summary>Whether <paramref name="name"/> resolves on PATH.</summary>
    bool OnPath(string name);
}

/// <summary>Runs real commands without a console window.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<string> RunAsync(string name, IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(args);
        var info = new ProcessStartInfo(name)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        var output = new System.Text.StringBuilder();
        void Append(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        }

        process.OutputDataReceived += Append;
        process.ErrorDataReceived += Append;
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new LauncherException($"{name}: {e.Message}", e);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        string text;
        lock (output)
        {
            text = output.ToString();
        }

        if (process.ExitCode != 0)
        {
            throw new CommandFailedException($"exit status {process.ExitCode}", text);
        }

        return text;
    }

    public bool OnPath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                if (File.Exists(Path.Combine(dir.Trim('"'), name + extension)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>A command ran and exited nonzero; <see cref="Output"/> is what it printed.</summary>
public sealed class CommandFailedException(string message, string output) : LauncherException(message)
{
    public string Output { get; } = output;
}

/// <summary>
/// The optional local telemetry stack (tools/observability/compose.yml, docs/developers/observability.md; Go
/// <c>obsController</c>). The game never depends on it: the launcher only starts and stops the containers.
/// </summary>
public sealed class ObsController(string repo, ICommandRunner runner, HttpClient http, Action<string> log)
{
    public const string Project = "rimgovernor-observability";

    public const string DockerMissing = "Docker is not installed or not on PATH. The observability stack is optional; Play works without it.";

    public const string DockerDown = "Docker is installed but its daemon is not running. Start Docker Desktop and try again.";

    /// <summary>How long after Start the stack may still be coming up (an image pull included) before the state falls back to stopped.</summary>
    public static readonly TimeSpan StartWindow = TimeSpan.FromMinutes(5);

    private readonly string _compose = Path.Combine(repo, "tools", "observability", "compose.yml");
    private readonly Lock _mu = new();
    private bool _busy;
    private DateTimeOffset? _startedAt;
    private string _problem = "";

    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The current state. It only probes Grafana over HTTP and looks for docker on PATH, so polling it is cheap and never needs the daemon.</summary>
    public async Task<ObsView> ViewAsync(CancellationToken ct)
    {
        var healthy = await ProbeAsync(ct).ConfigureAwait(false);
        lock (_mu)
        {
            var state = "stopped";
            if (healthy)
            {
                state = "healthy";
                _problem = "";
            }
            else if (_busy || (_startedAt is { } at && Now() - at < StartWindow))
            {
                state = "starting";
            }

            var problem = _problem;
            if (state == "stopped" && problem.Length == 0 && !runner.OnPath("docker"))
            {
                problem = DockerMissing;
            }

            return new ObsView(state, problem, Links.Dashboards);
        }
    }

    /// <summary>Brings the stack up detached. Returns once the command has finished; the page polls <see cref="ViewAsync"/> meanwhile.</summary>
    public Task StartAsync() => ExecAsync(start: true, "up", "-d");

    /// <summary>Takes the stack down; its data volume is kept.</summary>
    public Task StopAsync() => ExecAsync(start: false, "down");

    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            using var response = await http.GetAsync(new Uri(Links.GrafanaUrl + "/api/health"), timeout.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>Null when docker is on PATH and its daemon answers, else the message for the player.</summary>
    private async Task<string?> DockerReadyAsync()
    {
        if (!runner.OnPath("docker"))
        {
            return DockerMissing;
        }

        try
        {
            await runner.RunAsync("docker", ["info", "--format", "{{.ServerVersion}}"], CancellationToken.None).ConfigureAwait(false);
        }
        catch (LauncherException e)
        {
            LogOutput(e is CommandFailedException failed ? failed.Output : e.Message);
            return DockerDown;
        }

        return null;
    }

    private void LogOutput(string output)
    {
        foreach (var line in output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                log("observability: " + trimmed);
            }
        }
    }

    private async Task ExecAsync(bool start, params string[] verb)
    {
        lock (_mu)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            _problem = "";
        }

        string? error = null;
        if (await DockerReadyAsync().ConfigureAwait(false) is { } message)
        {
            error = message;
        }
        else
        {
            string[] args = ["compose", "-f", _compose, "-p", Project, .. verb];
            log("observability: docker " + string.Join(' ', args));
            try
            {
                LogOutput(await runner.RunAsync("docker", args, CancellationToken.None).ConfigureAwait(false));
            }
            catch (LauncherException e)
            {
                LogOutput(e is CommandFailedException failed ? failed.Output : "");
                error = $"docker compose {verb[0]} failed: {e.Message} (see Details)";
            }
        }

        lock (_mu)
        {
            _busy = false;
            if (error is not null)
            {
                _problem = error;
                _startedAt = null;
                log("observability: " + error);
            }
            else
            {
                _startedAt = start ? Now() : null;
            }
        }
    }
}
