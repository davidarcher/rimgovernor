using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Launcher;

/// <summary>One registered case: its "area/case" name and the one-line scope the registry carries.</summary>
public sealed record AcceptCase(string Name, string Scope);

/// <summary>The case list. It is read from the registry linked into the launcher, so there is no loading phase.</summary>
public sealed record AcceptCasesView(IReadOnlyList<AcceptCase> Cases, bool Loading, string Error);

/// <summary>What the tab polls while a run is in flight or after one.</summary>
public sealed record AcceptRunView(string State, string Case, long ElapsedMs, IReadOnlyList<string> Output, string Dir, string Error);

/// <summary>What the runner needs of the launcher.</summary>
public interface IAcceptHost
{
    /// <summary>Null when a run may start: the game copy is not in use. Else why not.</summary>
    Task<string?> GuardAsync();

    /// <summary>A fresh output directory for a run of <paramref name="name"/>.</summary>
    string OutputDir(string name);

    /// <summary>Runs the case windowed to completion (<paramref name="ct"/> stops it); returns the acceptance command's exit code.</summary>
    Task<int> RunAsync(string name, string output, TextWriter console, CancellationToken ct);

    void Log(string line);
}

/// <summary>
/// The Acceptance tab: the registered native acceptance cases, each runnable on demand in a visible game window for
/// local playtesting. The cases run in this process through the acceptance project; Stop
/// cancels the run, and the game it opened stays up (Close game ends it).
/// </summary>
public sealed class AcceptRunner(IAcceptHost host, CaseRegistry registry)
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Stopped = "stopped";

    /// <summary>How much of a run's output the tab shows.</summary>
    private const int KeepLines = 400;

    private readonly Lock _mu = new();
    private string _state = Idle;
    private string _name = "";
    private string _dir = "";
    private string _error = "";
    private DateTimeOffset _started;
    private DateTimeOffset _ended;
    private List<string> _lines = [];
    private string _partial = "";
    private CancellationTokenSource? _cts;
    private bool _stopping;

    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Whether a run is in flight.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_mu)
            {
                return _state == Running;
            }
        }
    }

    /// <summary>The registry's cases.</summary>
    public AcceptCasesView Cases() =>
        new([.. registry.All().Select(c => new AcceptCase(c.Name, c.Scope))], false, "");

    /// <summary>The current run.</summary>
    public AcceptRunView View()
    {
        lock (_mu)
        {
            var end = _state == Running ? Now() : _ended;
            var elapsed = _started == default ? 0 : (long)(end - _started).TotalMilliseconds;
            var lines = new List<string>(_lines);
            if (_partial.Length > 0)
            {
                lines.Add(_partial);
            }

            return new AcceptRunView(_state, _name, elapsed, lines, _dir, _error);
        }
    }

    /// <summary>
    /// Starts one case. Refuses while a run is in flight, while the registry does not know the case, and while the
    /// guard refuses.
    /// </summary>
    public async Task RunAsync(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_mu)
        {
            if (_state == Running)
            {
                throw new LauncherException($"{_name} is still running");
            }
        }

        if (registry.Lookup(name) is null)
        {
            throw new LauncherException($"unknown case \"{name}\"");
        }

        if (await host.GuardAsync().ConfigureAwait(false) is { } refusal)
        {
            throw new LauncherException(refusal);
        }

        var dir = host.OutputDir(name);
        CancellationTokenSource cts;
        lock (_mu)
        {
            if (_state == Running)
            {
                throw new LauncherException($"{_name} is still running");
            }

            cts = new CancellationTokenSource();
            (_state, _name, _dir, _error) = (Running, name, dir, "");
            (_started, _ended, _lines, _partial) = (Now(), default, [], "");
            (_cts, _stopping) = (cts, false);
        }

        _ = Task.Run(() => ExecuteAsync(name, dir, cts), CancellationToken.None);
    }

    /// <summary>Ends the run in flight. Throws when none is.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_mu)
        {
            cts = _cts;
            _stopping = cts is not null;
        }

        if (cts is null)
        {
            throw new LauncherException("no acceptance case is running");
        }

        cts.Cancel();
    }

    private async Task ExecuteAsync(string name, string dir, CancellationTokenSource cts)
    {
        string failure = "";
        try
        {
            var code = await host.RunAsync(name, dir, new LineSink(Append), cts.Token).ConfigureAwait(false);
            if (code != 0)
            {
                failure = $"the acceptance command exited with code {code}";
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is AcceptanceException or IOException or InvalidOperationException or UnauthorizedAccessException or LauncherException)
        {
            failure = e.Message;
        }

        lock (_mu)
        {
            if (_cts != cts)
            {
                return;
            }

            (_cts, _ended) = (null, Now());
            if (_partial.Length > 0)
            {
                _lines.Add(_partial);
                _partial = "";
            }

            if (_stopping)
            {
                _state = Stopped;
            }
            else if (failure.Length > 0)
            {
                (_state, _error) = (Failed, failure);
            }
            else
            {
                _state = Passed;
            }
        }

        cts.Dispose();
    }

    /// <summary>Collects the run's output by line.</summary>
    private void Append(string text)
    {
        lock (_mu)
        {
            var parts = (_partial + text.Replace("\r", "", StringComparison.Ordinal)).Split('\n');
            _partial = parts[^1];
            _lines.AddRange(parts.Take(parts.Length - 1));
            if (_lines.Count > KeepLines)
            {
                _lines = _lines.GetRange(_lines.Count - KeepLines, KeepLines);
            }
        }
    }
}
