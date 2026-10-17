using System.Globalization;
using System.Net;
using System.Text.Json;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Acceptance.Cases;
using RimGovernor.Brain.HttpChannel;

namespace RimGovernor.Launcher;

/// <summary>
/// The launcher's state and actions behind the page (Go <c>app</c>). It keeps the game files and the production mod
/// current, starts RimWorld with the in-process brain (<c>-rimgovernor-inproc</c>) and talks to the brain's local HTTP
/// channel through the endpoint file the game writes. There is no controller process: Play starts the game, Stop and
/// Close game end it.
/// </summary>
public sealed class LauncherApp : INewColonyHost, IAcceptHost, IDisposable
{
    public const string LayoutName = "Game files";
    public const string ModName = "RimGovernor mod";

    /// <summary>How long Play waits for the game to boot and announce its channel.</summary>
    private static readonly TimeSpan StartBudget = TimeSpan.FromMinutes(5);

    private const int KeepLogLines = 3000;

    private readonly Lock _mu = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SaveLoader _loader;
    private readonly CaseRegistry _registry = new();
    private readonly List<Artifact> _artifacts = [];
    private readonly List<string> _log = [];
    private string _logPartial = "";
    private bool _setupBusy;
    private bool _setupFailed;
    private bool _modPending;
    private DateTimeOffset _lastFull = DateTimeOffset.MinValue;
    private string _state = GameState.Stopped;
    private string _message = "";
    private bool _gameUp;
    private Settings _settings = new();
    private CancellationTokenSource? _startCts;

    /// <summary>What this launcher last started the game with; a running game it did not start (or forgot, on a relaunch of the launcher) is restarted before reuse.</summary>
    private LaunchedGame? _launched;

    public LauncherApp(string repo)
    {
        Repo = repo;
        PrivateDir = Path.Combine(repo, ".rimgovernor");
        Layout = SetupLayout.For(repo);
        _loader = new SaveLoader(_http);
        _artifacts.Add(new Artifact(LayoutName, ArtifactState.Building, "Checking"));
        _artifacts.Add(new Artifact(ModName, ArtifactState.Building, "Checking"));
        try
        {
            _settings = Settings.Load(SettingsPath);
        }
        catch (LauncherException e)
        {
            Log($"settings: {e.Message} (using defaults)");
        }

        CaseCatalog.Register(_registry);
        Colony = new NewColonyRunner(this, _http, _settings.NewColony ?? NewColonySpec.Default);
        Accept = new AcceptRunner(this, _registry);
        Watch = new ObserveService(_http, BrainAddressAsync, LogsPath);
    }

    public string Repo { get; }

    public string PrivateDir { get; }

    public SetupLayout Layout { get; }

    public NewColonyRunner Colony { get; }

    public AcceptRunner Accept { get; }

    /// <summary>The Observe tab's reads of the running brain and its log ring.</summary>
    public ObserveService Watch { get; }

    private string SettingsPath => Path.Combine(PrivateDir, "launcher.json");

    private string ConfigDir => Path.Combine(Layout.Root, "config");

    private string ConfigPath => Path.Combine(ConfigDir, "config.json");

    // INewColonyHost and IAcceptHost.
    public bool Observe
    {
        get
        {
            lock (_mu)
            {
                return _settings.Observe;
            }
        }
    }

    public string SavesDir => Path.Combine(Layout.Root, "profile", "Saves");

    /// <summary>Appends lines to the log panel.</summary>
    public void Log(string line)
    {
        lock (_mu)
        {
            AppendLog(line + "\n");
        }
    }

    /// <summary>Appends build output (chunks, not necessarily whole lines) to the log panel.</summary>
    public void Write(string chunk)
    {
        lock (_mu)
        {
            AppendLog(chunk);
        }
    }

    private void AppendLog(string chunk)
    {
        var parts = (_logPartial + chunk.Replace("\r", "", StringComparison.Ordinal)).Split('\n');
        _logPartial = parts[^1];
        _log.AddRange(parts.Take(parts.Length - 1));
        if (_log.Count > KeepLogLines)
        {
            _log.RemoveRange(0, _log.Count - KeepLogLines);
        }
    }

    private void SetArtifact(string name, string state, string detail)
    {
        lock (_mu)
        {
            var i = _artifacts.FindIndex(a => a.Name == name);
            if (i < 0 || (_artifacts[i].State == state && _artifacts[i].Detail == detail))
            {
                return;
            }

            _artifacts[i] = new Artifact(name, state, detail);
            if (state == ArtifactState.Failed)
            {
                AppendLog(name + ": " + detail + "\n");
            }
        }
    }

    private void SetState(string state, string message)
    {
        lock (_mu)
        {
            (_state, _message) = (state, message);
        }
    }

    /// <summary>The page's snapshot.</summary>
    public LauncherView View()
    {
        lock (_mu)
        {
            return new LauncherView([.. _artifacts], _state, _message, _gameUp, [.. _log], _settings, Settings.ListSaves(SavesDir));
        }
    }

    /// <summary>Validates and keeps the page's settings. The last New colony form is not the page's to edit: it stays as generated.</summary>
    public void SaveSettings(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_mu)
        {
            var kept = settings with { NewColony = _settings.NewColony };
            kept.Save(SettingsPath);
            _settings = kept;
        }
    }

    // INewColonyHost
    public void SaveSpec(NewColonySpec spec)
    {
        lock (_mu)
        {
            var kept = _settings with { NewColony = spec };
            kept.Save(SettingsPath);
            _settings = kept;
        }
    }

    /// <summary>The full check: the window gained focus, or the launcher opened. It hashes the native sources, so it is not part of the poll; it is debounced.</summary>
    public async Task FocusAsync()
    {
        lock (_mu)
        {
            if (DateTimeOffset.UtcNow - _lastFull < TimeSpan.FromSeconds(5))
            {
                return;
            }

            _lastFull = DateTimeOffset.UtcNow;
            _setupFailed = false;
        }

        await PollAsync().ConfigureAwait(false);
        StartSetup(poll: false);
    }

    /// <summary>Runs <see cref="PollAsync"/> every few seconds until cancelled.</summary>
    public async Task MonitorAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await PollAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The cheap check: the game process and the brain's health. A pending mod update applies once the game has closed.</summary>
    public async Task PollAsync()
    {
        IReadOnlyList<int>? running = null;
        try
        {
            running = await SetupProcesses.RunningGamesAsync(Layout.GameCopy, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SetupException e)
        {
            Log("poll: " + e.Message);
        }

        bool gameUp, pending;
        string state;
        lock (_mu)
        {
            if (running is not null)
            {
                _gameUp = running.Count > 0;
            }

            (gameUp, pending, state) = (_gameUp, _modPending, _state);
        }

        if (!File.Exists(ConfigPath) || (pending && !gameUp))
        {
            StartSetup(poll: true);
        }

        if (state == GameState.Starting || Accept.IsRunning)
        {
            return;
        }

        if (gameUp && await BrainAddressAsync(CancellationToken.None).ConfigureAwait(false) is not null)
        {
            SetState(GameState.Running, "");
        }
        else if (state == GameState.Running)
        {
            SetState(GameState.Stopped, "The game stopped responding.");
        }
    }

    /// <summary>The log ring (<c>logs.jsonl</c>) of the configured game's brain; null when no game is configured. It outlives the process, so it exists between runs.</summary>
    private string? LogsPath()
    {
        try
        {
            var gameId = Settings.ConfiguredGame(File.ReadAllText(ConfigPath));
            return Path.Combine(GameSessions.EndpointDirectory(ConfigDir, gameId), "logs.jsonl");
        }
        catch (Exception e) when (e is IOException or LauncherException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The address of the in-process brain of the game this launcher recorded, when it is alive and healthy; else null.</summary>
    private async Task<string?> BrainAddressAsync(CancellationToken ct)
    {
        string gameId;
        try
        {
            gameId = Settings.ConfiguredGame(await File.ReadAllTextAsync(ConfigPath, ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is IOException or LauncherException or UnauthorizedAccessException)
        {
            return null;
        }

        var game = GameProcess.Attach(ConfigDir, gameId);
        return game is null ? null : await EndpointAsync(GameSessions.EndpointDirectory(ConfigDir, gameId), game.Pid, ct).ConfigureAwait(false);
    }

    /// <summary>The endpoint file's address when it names <paramref name="pid"/> and the channel answers its health check; else null.</summary>
    private async Task<string?> EndpointAsync(string directory, int pid, CancellationToken ct)
    {
        if (EndpointFile.Read(directory) is not { } endpoint || endpoint.Address.Length == 0 || endpoint.Pid != pid)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            using var response = await _http.GetAsync(new Uri(endpoint.Address.TrimEnd('/') + "/api/health"), timeout.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK ? endpoint.Address.TrimEnd('/') : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>Runs the setup in the background unless it runs (or failed since the last focus, for the poll).</summary>
    private void StartSetup(bool poll)
    {
        lock (_mu)
        {
            if (_setupBusy || (poll && _setupFailed))
            {
                return;
            }

            _setupBusy = true;
        }

        _ = Task.Run(
            async () =>
            {
                var ok = false;
                try
                {
                    ok = await PrepareSetupAsync().ConfigureAwait(false);
                }
                finally
                {
                    lock (_mu)
                    {
                        (_setupBusy, _setupFailed) = (false, !ok);
                    }
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// The game layout and production mod through the acceptance setup (in place, idempotent). Nothing is replaced
    /// while the game copy runs; the mod update is then left pending.
    /// </summary>
    private async Task<bool> PrepareSetupAsync()
    {
        if (Accept.IsRunning)
        {
            // The run's preflight owns the game copy and its mod; the next poll restores the production build once the game closes.
            return true;
        }

        string current;
        try
        {
            current = SetupPackage.SourceTreeHash(Repo);
        }
        catch (Exception e) when (e is AcceptanceException or IOException)
        {
            SetArtifact(ModName, ArtifactState.Failed, e.Message);
            return false;
        }

        SetupPackageManifest? manifest = null;
        try
        {
            manifest = SetupPackage.ReadPackageManifest(Path.Combine(Layout.GameCopy, "Mods", "RimGovernor"));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // No readable manifest: the mod reads as missing.
        }

        var (modState, modReason) = ArtifactStates.ModState(manifest, current);
        IReadOnlyList<int> running;
        try
        {
            running = await SetupProcesses.RunningGamesAsync(Layout.GameCopy, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SetupException e)
        {
            SetArtifact(LayoutName, ArtifactState.Failed, e.Message);
            return false;
        }

        if (running.Count > 0)
        {
            SetArtifact(LayoutName, ArtifactState.Ok, "In use by the running game");
            lock (_mu)
            {
                (_gameUp, _modPending) = (true, modState != ArtifactState.Ok);
            }

            if (modState == ArtifactState.Ok)
            {
                SetArtifact(ModName, ArtifactState.Ok, "Up to date");
            }
            else
            {
                SetArtifact(ModName, ArtifactState.Pending, "Update pending; applies after the game closes");
            }

            return true;
        }

        if (Directory.Exists(Layout.GameCopy) && modState == ArtifactState.Ok)
        {
            SetArtifact(LayoutName, ArtifactState.Ok, "Ready");
            SetArtifact(ModName, ArtifactState.Ok, "Up to date");
            return true;
        }

        SetArtifact(LayoutName, ArtifactState.Building, "Refreshing");
        if (modState != ArtifactState.Ok)
        {
            SetArtifact(ModName, ArtifactState.Building, $"Building ({modReason})");
        }

        SetupInputs inputs;
        try
        {
            inputs = SetupDiscovery.Discover(new SetupOverrides { Repo = Repo });
        }
        catch (SetupException e)
        {
            SetArtifact(LayoutName, ArtifactState.Failed, e.Message);
            SetArtifact(ModName, ArtifactState.Failed, "Needs the game files");
            return false;
        }

        try
        {
            using var sink = new LineSink(Write);
            _ = await SetupEnvironment.RunAsync(
                new SetupOptions { Layout = Layout, Inputs = inputs, Fixtures = [], SourceTree = current, Log = sink },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            SetArtifact(LayoutName, ArtifactState.Failed, e.Message);
            if (modState != ArtifactState.Ok)
            {
                SetArtifact(ModName, ArtifactState.Failed, "Build failed; see Details");
            }

            return false;
        }

        lock (_mu)
        {
            _modPending = false;
        }

        SetArtifact(LayoutName, ArtifactState.Ok, "Ready");
        SetArtifact(ModName, ArtifactState.Ok, "Up to date");
        return true;
    }

    /// <summary>Empty when Play can start, else what it waits on.</summary>
    private string Ready()
    {
        if (Accept.IsRunning)
        {
            return "An acceptance case is running (Acceptance tab)";
        }

        lock (_mu)
        {
            foreach (var artifact in _artifacts)
            {
                switch (artifact.State)
                {
                    case ArtifactState.Ok or ArtifactState.Pending:
                        break;
                    case ArtifactState.Failed:
                        return artifact.Name + ": " + artifact.Detail;
                    default:
                        return "Still preparing " + artifact.Name.ToLowerInvariant();
                }
            }

            return _setupBusy ? "Still preparing the game files" : "";
        }
    }

    /// <summary>Play: starts RimWorld with the brain, then loads the chosen save when the settings name one.</summary>
    public async Task PlayAsync()
    {
        lock (_mu)
        {
            if (_state != GameState.Stopped)
            {
                return;
            }
        }

        Settings settings;
        lock (_mu)
        {
            settings = _settings;
        }

        try
        {
            _ = await StartAsync(settings, load: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (LauncherException)
        {
            // StartAsync logged it and put it on the Launch tab.
        }
        catch (OperationCanceledException)
        {
            // Close game ended the start.
        }
    }

    // INewColonyHost: a fresh game, no load.
    public async Task<string> StartGameAsync(CancellationToken ct)
    {
        Settings settings;
        lock (_mu)
        {
            settings = _settings with { LoadSave = "" };
        }

        return await StartAsync(settings, load: false, ct).ConfigureAwait(false);
    }

    private async Task<string> StartAsync(Settings settings, bool load, CancellationToken ct)
    {
        if (Ready() is { Length: > 0 } why)
        {
            SetState(GameState.Stopped, why);
            throw new LauncherException(why);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_mu)
        {
            if (_state != GameState.Stopped)
            {
                throw new LauncherException("RimWorld is already starting or running");
            }

            (_state, _message, _startCts) = (GameState.Starting, "Starting RimWorld", cts);
        }

        try
        {
            var trace = new LaunchTrace("launcher");
            var address = await LaunchAsync(settings, trace, cts.Token).ConfigureAwait(false);
            SetState(GameState.Running, "");
            if (load && settings.LoadSave.Length > 0)
            {
                _ = LoadInBackgroundAsync(address, settings.LoadSave, trace);
            }
            else
            {
                _ = PostLaunchAsync(trace, address);
            }

            return address;
        }
        catch (OperationCanceledException)
        {
            SetState(GameState.Stopped, "");
            throw;
        }
        catch (LauncherException e)
        {
            Log("play: " + e.Message);
            SetState(GameState.Stopped, e.Message);
            throw;
        }
        finally
        {
            lock (_mu)
            {
                _startCts = null;
            }
        }
    }

    /// <summary>
    /// Starts the configured game detached with <c>-rimgovernor-inproc</c> and the settings' extra arguments, the
    /// endpoint directory and a new run id in its environment, and waits for it to announce a healthy channel. Returns
    /// the channel's base address. A fresh launch records <c>launch.process_start</c> and <c>launch.connect</c> in
    /// <paramref name="trace"/>; an adopted game records nothing.
    /// </summary>
    private async Task<string> LaunchAsync(Settings settings, LaunchTrace trace, CancellationToken ct)
    {
        string gameId;
        try
        {
            gameId = Settings.ConfiguredGame(await File.ReadAllTextAsync(ConfigPath, ct).ConfigureAwait(false));
        }
        catch (IOException e)
        {
            throw new LauncherException("the game launch configuration is missing: " + e.Message, e);
        }

        var endpointDir = GameSessions.EndpointDirectory(ConfigDir, gameId);
        var spec = GameSessions.LaunchSpecFromConfig(ConfigDir, gameId);
        var (args, hostEnv) = LaunchInputs(spec, settings, endpointDir);
        if (GameProcess.Attach(ConfigDir, gameId) is { } running)
        {
            if (await EndpointAsync(endpointDir, running.Pid, ct).ConfigureAwait(false) is not { } adopted)
            {
                throw new LauncherException("RimWorld is already running without the in-process brain; use Close game first");
            }

            // Loading into the running game is a reuse like New colony's: a process started with another mod list,
            // package, command line or host environment restarts.
            if (ReuseMismatch(running.Pid, args, hostEnv) is not { Length: > 0 } reason)
            {
                return adopted;
            }

            Log($"play: restarting the running game ({reason})");
            await StopGamesAsync(ct).ConfigureAwait(false);
        }

        if ((await SetupProcesses.RunningGamesAsync(Layout.GameCopy, ct).ConfigureAwait(false)).Count > 0)
        {
            throw new LauncherException("RimWorld is already running from the game copy; use Close game first");
        }

        var stale = EndpointFile.PathIn(endpointDir);
        try
        {
            // File.Delete throws when the directory is missing, as it is for a game id that has not run here yet.
            _ = Directory.CreateDirectory(endpointDir);
            File.Delete(stale);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"remove stale {stale}: {e.Message}", e);
        }

        var env = new Dictionary<string, string>(hostEnv, StringComparer.Ordinal) { ["RIMGOVERNOR_RUN_ID"] = Guid.NewGuid().ToString("N") };
        GameProcess game;
        try
        {
            using var phase = trace.Time("launch.process_start");
            GameSessions.PrepareFreshLaunch(ConfigDir);
            game = GameProcess.StartDetached(new GameLaunchSpec
            {
                GameId = spec.GameId, Executable = spec.Executable, WorkingDir = spec.WorkingDir, Args = args, Env = env, StateDir = spec.StateDir,
            });
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(e.Message, e);
        }

        lock (_mu)
        {
            _launched = new LaunchedGame(game.Pid, args, hostEnv);
        }

        Log($"RimWorld {string.Join(' ', args)}");
        var log = GameProcess.LogPath(ConfigDir, gameId);
        using var connect = trace.Time("launch.connect");
        for (var deadline = DateTimeOffset.UtcNow + StartBudget; DateTimeOffset.UtcNow < deadline;)
        {
            ct.ThrowIfCancellationRequested();
            if (!game.Alive)
            {
                var exit = GameProcess.LastExit(ConfigDir, gameId);
                throw new LauncherException(string.Create(CultureInfo.InvariantCulture, $"RimWorld exited while starting (exit code {exit?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}); see {log}"));
            }

            if (await EndpointAsync(endpointDir, game.Pid, ct).ConfigureAwait(false) is { } address)
            {
                return address;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new LauncherException($"RimWorld did not announce its channel within {StartBudget.TotalMinutes:0} minutes; see {log}");
    }

    /// <summary>
    /// The command line and environment a start with <paramref name="settings"/> uses, less the per-run id: the shared
    /// config's, without the acceptance probes' pause-on-load flag (a player's game must run after a load), then the
    /// settings' own.
    /// </summary>
    private static (string[] Args, Dictionary<string, string> Env) LaunchInputs(GameLaunchSpec spec, Settings settings, string endpointDir)
    {
        var env = new Dictionary<string, string>(spec.Env, StringComparer.Ordinal) { ["RIMGOVERNOR_OTLP_DIR"] = endpointDir };
        foreach (var (name, value) in settings.HostEnvironment())
        {
            env[name] = value;
        }

        string[] args = [.. spec.Args.Where(a => !string.Equals(a, "-rimgovernor-pause-on-load", StringComparison.Ordinal)), .. settings.GameArguments()];
        return (args, env);
    }

    /// <summary>
    /// Why the running game <paramref name="pid"/> cannot be reused for a start with <paramref name="args"/> and
    /// <paramref name="env"/>, or "" when it can. The mod list and package come from the launch snapshots
    /// (<see cref="HeadlessProfile.LaunchedMismatch"/>, as acceptance's kept game); the command line and host environment
    /// from this launcher's own record, since the game reads both once at start.
    /// </summary>
    private string ReuseMismatch(int pid, string[] args, Dictionary<string, string> env)
    {
        string snapshot;
        try
        {
            snapshot = HeadlessProfile.LaunchedMismatch(ConfigDir);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            return "launch snapshot: " + e.Message;
        }

        LaunchedGame? launched;
        bool pending;
        lock (_mu)
        {
            (launched, pending) = (_launched, _modPending);
        }

        if (snapshot.Length > 0)
        {
            return snapshot;
        }

        if (pending)
        {
            return "mod update pending";
        }

        if (launched is null || launched.Pid != pid)
        {
            return "unrecorded";
        }

        if (!launched.Args.SequenceEqual(args, StringComparer.Ordinal))
        {
            return "args";
        }

        return launched.Env.Count == env.Count && env.All(e => launched.Env.TryGetValue(e.Key, out var v) && v == e.Value) ? "" : "env";
    }

    // INewColonyHost
    public async Task<string?> ReusableGameAsync(CancellationToken ct)
    {
        lock (_mu)
        {
            if (_state == GameState.Starting)
            {
                return null; // a Play start is not a game to reuse yet; the restart ends it as before
            }
        }

        string gameId;
        try
        {
            gameId = Settings.ConfiguredGame(await File.ReadAllTextAsync(ConfigPath, ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is IOException or LauncherException or UnauthorizedAccessException)
        {
            return null;
        }

        if (GameProcess.Attach(ConfigDir, gameId) is not { } running)
        {
            return null;
        }

        var endpointDir = GameSessions.EndpointDirectory(ConfigDir, gameId);
        if (await EndpointAsync(endpointDir, running.Pid, ct).ConfigureAwait(false) is not { } address)
        {
            Log("new colony: restarting the game (its channel does not answer)");
            return null;
        }

        Settings settings;
        lock (_mu)
        {
            settings = _settings;
        }

        string reason;
        try
        {
            var (args, env) = LaunchInputs(GameSessions.LaunchSpecFromConfig(ConfigDir, gameId), settings, endpointDir);
            reason = ReuseMismatch(running.Pid, args, env);
        }
        catch (Exception e) when (e is AcceptanceException or LauncherException)
        {
            reason = e.Message;
        }

        if (reason.Length > 0)
        {
            Log($"new colony: restarting the game ({reason})");
            return null;
        }

        return address;
    }

    private async Task LoadInBackgroundAsync(string address, string save, LaunchTrace trace)
    {
        Log($"load: {save}");
        try
        {
            using (trace.Time("start.load"))
            {
                await _loader.LoadAsync(address, save, TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
            }

            Log($"load: {save} loaded");
            await PostLaunchAsync(trace, address).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log($"load: {save} cancelled");
        }
        catch (LauncherException e)
        {
            Log("load: " + e.Message);
        }
        catch (Exception e)
        {
            // Nothing awaits this task: an unlogged fault here is a save that silently never loads.
            Log($"load: {save} failed: {e}");
        }
    }

    /// <summary>
    /// Ships the launch phases to the game's brain (<see cref="LaunchTrace"/>), waiting for its routes; a drop is
    /// logged and never fails the launch. Nothing awaits it on a plain Play, so every fault is logged here.
    /// </summary>
    private async Task PostLaunchAsync(LaunchTrace trace, string address)
    {
        try
        {
            if (await trace.PostAsync(_http, address, LaunchTrace.PostWait, CancellationToken.None).ConfigureAwait(false) is { } dropped)
            {
                Log("play: " + dropped);
            }
        }
        catch (Exception e)
        {
            Log("play: launch spans dropped: " + e.Message);
        }
    }

    /// <summary>Stop and Close game: ends RimWorld, and the brain with it. The in-process brain has no separate process to keep.</summary>
    public async Task CloseGameAsync()
    {
        _loader.Stop();
        CancellationTokenSource? starting;
        lock (_mu)
        {
            starting = _startCts;
        }

        if (starting is not null)
        {
            await starting.CancelAsync().ConfigureAwait(false);
        }

        await StopGamesAsync(CancellationToken.None).ConfigureAwait(false);
        SetState(GameState.Stopped, "");
        await PollAsync().ConfigureAwait(false);
    }

    /// <summary>Ends every RimWorld process of the game copy.</summary>
    private async Task StopGamesAsync(CancellationToken ct)
    {
        try
        {
            var pids = await SetupProcesses.StopGamesAsync(Layout.GameCopy, ct).ConfigureAwait(false);
            if (pids.Count > 0)
            {
                Log($"closed RimWorld (pid {string.Join(' ', pids)})");
            }
        }
        catch (SetupException e)
        {
            Log("close game: " + e.Message);
        }
    }

    public async Task RestartAsync()
    {
        await CloseGameAsync().ConfigureAwait(false);
        await PlayAsync().ConfigureAwait(false);
    }

    // IAcceptHost
    public async Task<string?> GuardAsync()
    {
        await PollAsync().ConfigureAwait(false);
        lock (_mu)
        {
            if (_state != GameState.Stopped || _gameUp)
            {
                return "close the game first (Launch tab)";
            }

            return _setupBusy ? "still preparing the game files; try again in a moment" : null;
        }
    }

    public string OutputDir(string name) =>
        Path.Combine(Layout.Root, "acceptance", "launcher", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    public Task<int> RunAsync(string name, string output, TextWriter console, CancellationToken ct)
    {
        string[] args = ["run", name, "-root", Layout.Root, "-output", output, "-headless=false", "-no-series"];
        Log("acceptance: " + string.Join(' ', args));
        return AcceptanceCommand.RunAsync(args, _registry, console, console, ct);
    }

    public void Dispose()
    {
        _loader.Dispose();
        _http.Dispose();
    }

    /// <summary>A game this launcher started: its process and the command line and environment (less the run id) it got.</summary>
    private sealed record LaunchedGame(int Pid, string[] Args, Dictionary<string, string> Env);
}
