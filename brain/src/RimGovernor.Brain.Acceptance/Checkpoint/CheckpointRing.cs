using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>What a save leaves: its path, the game tick it was taken at and the identity it was taken under.</summary>
public sealed record SaveResult(string Path, ulong Tick, JsonObject? Identity);

/// <summary>
/// Replaces the bridge and service save paths: leaves the game saved as name and returns the save (tests only).
/// </summary>
public delegate Task<SaveResult> CheckpointSaver(string name, string label, bool force, CancellationToken cancellationToken);

/// <summary>
/// Copies the SQLite database at the first path to the second through the online backup, so a service holding it
/// open leaves a consistent copy (Go <c>store.Open</c> + <c>Snapshot</c>; the store lands with the service slice).
/// </summary>
public delegate Task StoreSnapshotter(string source, string destination, CancellationToken cancellationToken);

/// <summary>A periodic capture declined: the game runs (<see cref="NotPaused"/>) or no side holds it.</summary>
internal sealed class CaptureDeclinedException : AcceptanceException
{
    public CaptureDeclinedException(bool notPaused)
        : base(notPaused ? "game is not paused" : "neither the harness nor a service holds the game")
    {
    }
}

/// <summary>
/// Takes bundles for one run: every <see cref="Every"/> of run phase at the next natural pause (a bridge-only case's
/// next native call or wait probe once the game is paused; a serve-driven case's next wait probe, through the
/// service's pause/save/resume), plus named phase bundles and the final failed bundle, into Dir/&lt;label&gt;/. The
/// runner installs it (<see cref="Activate"/>) for the case's Run and reads its <see cref="Entries"/> afterwards.
/// </summary>
public sealed partial class CheckpointRing
{
    private readonly Lock _gate = new();
    private readonly List<Checkpoint> _entries = [];
    private readonly List<string> _errors = [];
    private bool _activated;
    private long _started;
    private long _last;
    private long _nextCheck;
    private bool _busy;
    private Checkpoint? _failed;

    // Why no further entry is taken (Cap): the case passed a point its body cannot resume after.
    private string _capped = "";

    // Why the breakpoint fired; _nextBreakTick throttles a tick breakpoint's reads under a service.
    private string _tripped = "";
    private long _nextBreakTick;
    private bool _breakReading;

    public CheckpointRing(string dir, string caseName, Config config)
    {
        Dir = dir;
        Case = caseName;
        Config = config;
    }

    public string Dir { get; }

    public string Case { get; }

    /// <summary>The ring's cadence in run phase; zero or less disables periodic captures.</summary>
    public TimeSpan Every { get; set; }

    /// <summary>How many periodic entries the ring retains (<see cref="Checkpoints.CheckpointKeep"/> when unset).</summary>
    public int Keep { get; set; }

    /// <summary>The run-phase offset the run resumed at (0 from scratch).</summary>
    public TimeSpan Base { get; set; }

    /// <summary>The run's configuration; the save lands in its game profile.</summary>
    public Config Config { get; }

    /// <summary>Returns the harness holding the game, null while a service holds it. Consulted at every capture.</summary>
    public Func<Harness?>? Bridge { get; set; }

    /// <summary>Returns the running service holding the game, null otherwise. Consulted at every capture.</summary>
    public Func<ICheckpointService?>? Service { get; set; }

    /// <summary>The case's service.sqlite, copied when it exists (through <see cref="SnapshotStore"/>).</summary>
    public string StorePath { get; set; } = "";

    /// <summary>Hook: the SQLite online-backup copy the service slice provides; a store that exists needs it.</summary>
    public StoreSnapshotter? SnapshotStore { get; set; }

    /// <summary>The run's output directory, recorded on a failing run's index (<see cref="Ring.FailedOutput"/>).</summary>
    public string Output { get; set; } = "";

    /// <summary>Fills the sidecar's fingerprint fields.</summary>
    public Fingerprint Fingerprint { get; set; } = new("", "", []);

    /// <summary>Fills the sidecar: the case's declared serve spec.</summary>
    public JsonObject? Serve { get; set; }

    /// <summary>Fills the sidecar: the fixture op's reply.</summary>
    public JsonObject? Prepared { get; set; }

    public string SourceRevision { get; set; } = "";

    /// <summary>
    /// The case's own progress record, carried into every capture (<see cref="Checkpoints.SetState"/> adds to it; a
    /// resumed run starts from the entry's).
    /// </summary>
    public JsonObject State { get; set; } = [];

    /// <summary>When set, marks every capture a stage bundle of the stage its label names, under this staging-code hash.</summary>
    public string StageKey { get; set; } = "";

    /// <summary>
    /// The entries of the timeline this run resumed into (the previous ring's up to the resume point); the
    /// provisional index a capture writes lists them before this run's own.
    /// </summary>
    public IReadOnlyList<Checkpoint> Prior { get; set; } = [];

    /// <summary>When set, replaces the bridge and service save paths (tests only).</summary>
    public CheckpointSaver? Saver { get; set; }

    /// <summary>The run's breakpoint, zero when none.</summary>
    public Breakpoint Break { get; set; }

    /// <summary>Runs once when the breakpoint trips (<see cref="Trip"/>), with the reason.</summary>
    public Action<string>? OnBreak { get; set; }

    /// <summary>
    /// Hook for a bridge-only case's tick breakpoint: the newest game tick a decoded reply carried since the last
    /// load (Go <c>lastObservedTick</c>; wire it to the harness's TickStats).
    /// </summary>
    public Func<ulong?>? ObservedTick { get; set; }

    /// <summary>Starts the ring's run phase and installs it for the hooks.</summary>
    public void Activate()
    {
        lock (_gate)
        {
            _started = Now();
            _activated = true;
            _last = _started;
            if (Keep <= 0)
            {
                Keep = Checkpoints.CheckpointKeep;
            }
        }

        Checkpoints.Install(this);
    }

    /// <summary>Uninstalls the ring; periodic captures stop.</summary>
    public void Deactivate() => Checkpoints.Uninstall(this);

    /// <summary>The bundles this run took, oldest first, with the failed bundle last when one was taken.</summary>
    public IReadOnlyList<Checkpoint> Entries()
    {
        lock (_gate)
        {
            var list = new List<Checkpoint>(_entries);
            if (_failed is not null)
            {
                list.Add(_failed);
            }

            return list;
        }
    }

    /// <summary>Why the ring stopped taking entries, "" while it still does.</summary>
    public string Capped()
    {
        lock (_gate)
        {
            return _capped;
        }
    }

    /// <summary>The captures that failed, for the report.</summary>
    public IReadOnlyList<string> Errors()
    {
        lock (_gate)
        {
            return [.. _errors];
        }
    }

    /// <summary>Stops taking any further entry (see <see cref="Checkpoints.Cap"/>).</summary>
    internal void CapWith(string reason)
    {
        lock (_gate)
        {
            if (_capped.Length == 0)
            {
                _capped = reason;
            }
        }
    }

    /// <summary>The current run-phase offset: Base plus the time since Activate.</summary>
    public TimeSpan Offset()
    {
        lock (_gate)
        {
            return OffsetLocked();
        }
    }

    private TimeSpan OffsetLocked() => _activated ? Base + Stopwatch.GetElapsedTime(_started) : Base;

    /// <summary>
    /// A bundle's label for a run-phase offset: "t+7m" for whole minutes, "t+1m30s" or "t+45s" otherwise. An offset
    /// that is not a whole second (a sub-second cadence, which rounds its offsets to the cadence) keeps its
    /// milliseconds, so two captures a few milliseconds apart never share a label.
    /// </summary>
    public static string OffsetLabel(TimeSpan d)
    {
        if (d.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            return "t+" + GoDuration.Format(d);
        }

        var hours = (long)d.TotalHours;
        var minutes = d.Minutes;
        var seconds = d.Seconds;
        var b = new StringBuilder("t+");
        if (hours > 0)
        {
            b.Append(CultureInfo.InvariantCulture, $"{hours}h");
        }

        if (minutes > 0)
        {
            b.Append(CultureInfo.InvariantCulture, $"{minutes}m");
        }

        if (seconds > 0 || (hours == 0 && minutes == 0))
        {
            b.Append(CultureInfo.InvariantCulture, $"{seconds}s");
        }

        return b.ToString();
    }

    /// <summary>
    /// The natural-pause hook: captures a ring entry when one is due and returns how long that took, so a wait can
    /// leave it out of its stall accounting.
    /// </summary>
    internal async Task<TimeSpan> MaybeAsync(CancellationToken cancellationToken)
    {
        // A breakpoint is checked at every natural pause, on its own clock.
        await CheckBreakAsync(cancellationToken).ConfigureAwait(false);
        string label;
        lock (_gate)
        {
            var now = Now();
            if (_capped.Length > 0 || _busy || Stopwatch.GetElapsedTime(_last, now) < Every || now < _nextCheck)
            {
                return TimeSpan.Zero;
            }

            // The label rounds to the cadence so consecutive captures never share one: 61 seconds in at a
            // one-minute cadence is t+1m.
            label = OffsetLabel(RoundTo(OffsetLocked(), Every));
            if (_entries.Any(e => string.Equals(e.Label, label, StringComparison.Ordinal)))
            {
                // The bundle under this label is live; overwriting it would leave two entries on one directory and
                // the prune deleting the newer.
                return TimeSpan.Zero;
            }

            _busy = true;
        }

        var began = Stopwatch.GetTimestamp();
        Checkpoint? entry = null;
        var declined = false;
        try
        {
            entry = await CaptureCoreAsync(label, force: false, cancellationToken).ConfigureAwait(false);
        }
        catch (CaptureDeclinedException)
        {
            declined = true;
        }
        catch (Exception e) when (IsCaptureFailure(e))
        {
            RecordError(label, e.Message);
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }

        var took = Stopwatch.GetElapsedTime(began);
        lock (_gate)
        {
            if (declined)
            {
                // The next call may find the window over (or the game held again); ask again in a moment, not on
                // every call of a running window.
                _nextCheck = Now() + ToTimestamp(TimeSpan.FromSeconds(5));
                return took;
            }

            _last = Now();
            if (entry is null)
            {
                return took;
            }

            _entries.Add(entry);
            while (_entries.Count > Keep)
            {
                Checkpoints.TryDeleteTree(_entries[0].Path);
                _entries.RemoveAt(0);
            }

            // A provisional index after every capture: a run the harness never closes (killed with its tool shell)
            // still resumes from its last bundle, with no failure tick to compare against.
            var provisional = new Ring
            {
                Case = Case,
                SourceRevision = SourceRevision,
                Next = entry.Label,
                Note = "the last run of " + Case + " ended without closing its ring",
            };
            foreach (var e in Prior.Concat(_entries))
            {
                provisional.Entries.Add(e);
            }

            while (provisional.Entries.Count > Keep)
            {
                provisional.Entries.RemoveAt(0);
            }

            try
            {
                provisional.Write(Dir);
            }
            catch (IOException e)
            {
                _errors.Add($"{label}: write provisional index: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                _errors.Add($"{label}: write provisional index: {e.Message}");
            }

            return took;
        }
    }

    /// <summary>
    /// Takes a named bundle now (a phase boundary a case declared), pausing the game if it runs; the bundle is kept
    /// beside the ring and is not pruned.
    /// </summary>
    public async Task<Checkpoint> CaptureAsync(string label, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_capped.Length > 0)
            {
                throw new AcceptanceException($"ring capped: {_capped}");
            }

            if (_busy)
            {
                throw new AcceptanceException("a capture is already in progress");
            }

            _busy = true;
        }

        try
        {
            var entry = await CaptureCoreAsync(label, force: true, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _entries.Add(entry);
            }

            return entry;
        }
        catch (Exception e) when (IsCaptureFailure(e))
        {
            RecordError(label, e.Message);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }
    }

    /// <summary>Whether a side holds the game for a capture: the harness, a running service, or a Saver.</summary>
    public bool Held() => Saver is not null || Bridge?.Invoke() is not null || Service?.Invoke() is not null;

    /// <summary>
    /// Takes the final bundle of a failed run (<see cref="Checkpoints.FailedCheckpoint"/>), pausing the game if it
    /// still runs; it never fails the run further.
    /// </summary>
    public async Task<Checkpoint?> FailAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _busy = true;
        }

        try
        {
            var entry = await CaptureCoreAsync(Checkpoints.FailedCheckpoint, force: true, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _failed = entry;
            }

            return entry;
        }
        catch (Exception e) when (IsCaptureFailure(e))
        {
            RecordError(Checkpoints.FailedCheckpoint, e.Message);
            return null;
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }
    }

    /// <summary>
    /// Takes one bundle through whichever side holds the game. <paramref name="force"/> pauses a running game first;
    /// otherwise a running game declines with <see cref="CaptureDeclinedException"/>.
    /// </summary>
    private async Task<Checkpoint> CaptureCoreAsync(string label, bool force, CancellationToken cancellationToken)
    {
        var began = Stopwatch.GetTimestamp();
        var dir = Path.Combine(Dir, label);
        Checkpoints.DeleteTree(dir);
        Directory.CreateDirectory(dir);
        var entry = new Checkpoint
        {
            Case = Case,
            Label = label,
            OffsetMs = (long)Offset().TotalMilliseconds,
            Save = Checkpoints.CheckpointSaveName(Case),
            Prepared = Prepared,
            State = StateSnapshot(),
            Serve = Serve,
            ServiceProfile = Config.ServiceProfileDir(),
            SourceRevision = SourceRevision,
            Package = Fingerprint.Package,
            Start = Fingerprint.Start,
            Expansions = Fingerprint.Expansions,
            At = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Path = dir,
        };
        if (StageKey.Length > 0)
        {
            entry.Stage = label;
            entry.StageKey = StageKey;
        }

        try
        {
            var holder = Bridge?.Invoke();
            var service = holder is null ? Service?.Invoke() : null;
            SaveResult saved;
            if (Saver is { } saver)
            {
                entry.Through = "saver";
                saved = await saver(entry.Save, label, force, cancellationToken).ConfigureAwait(false);
            }
            else if (holder is not null)
            {
                entry.Through = "bridge";
                saved = await BridgeSaveAsync(holder, entry.Save, label, force, cancellationToken).ConfigureAwait(false);
            }
            else if (service is not null)
            {
                entry.Through = "service";
                saved = await ServiceSaveCoreAsync(service, entry.Save, label, force, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new CaptureDeclinedException(notPaused: false);
            }

            entry.Tick = saved.Tick;
            entry.Identity = saved.Identity;
            Checkpoints.CopyFile(saved.Path, Path.Combine(dir, entry.Save + ".rws"));
            if (StorePath.Length > 0 && File.Exists(StorePath))
            {
                var snapshot = SnapshotStore ?? throw new AcceptanceException("store: no store snapshot hook is wired (CheckpointRing.SnapshotStore)");
                await snapshot(StorePath, Path.Combine(dir, Checkpoints.CheckpointStoreFile), cancellationToken).ConfigureAwait(false);
                entry.Store = true;
            }

            entry.WallMs = (long)Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            File.WriteAllText(Path.Combine(dir, Checkpoints.CheckpointSidecar), entry.ToJson().ToJsonString(SidecarOptions));
        }
        catch
        {
            Checkpoints.TryDeleteTree(dir);
            throw;
        }

        return entry;
    }

    private static readonly JsonSerializerOptions SidecarOptions = new() { WriteIndented = true };

    /// <summary>
    /// Copies the case state (for a sidecar, or for a stage bundle taken beside the ring); the case may keep adding
    /// to it while the capture writes. Null when empty.
    /// </summary>
    public JsonObject? StateSnapshot()
    {
        lock (_gate)
        {
            return State.Count == 0 ? null : State.DeepCloneObject();
        }
    }

    internal void SetState(string key, JsonNode? value)
    {
        lock (_gate)
        {
            if (value is null)
            {
                State.Remove(key);
                return;
            }

            State[key] = value.DeepClone();
        }
    }

    /// <summary>
    /// Saves through lifecycle_save on the harness session: the game must be paused (force pauses it), and the
    /// save must complete at the tick the identity read observed.
    /// </summary>
    private async Task<SaveResult> BridgeSaveAsync(Harness h, string name, string label, bool force, CancellationToken cancellationToken)
    {
        var loaded = await ReadLoadedAsync(h, $"checkpoint-{label}-identity", cancellationToken).ConfigureAwait(false);
        if (loaded["paused"].AsBool() != true)
        {
            if (!force)
            {
                throw new CaptureDeclinedException(notPaused: true);
            }

            await h.CallAsync($"checkpoint-{label}-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
            loaded = await ReadLoadedAsync(h, $"checkpoint-{label}-identity-paused", cancellationToken).ConfigureAwait(false);
        }

        var loadedContext = loaded["context"].AsMap();
        var identity = loadedContext?["identity"].AsMap()?.DeepCloneObject();
        var tick = loadedContext?["tick"].AsNumber() ?? -1;
        var tickNumber = tick < 0 ? 0UL : (ulong)tick;
        var request = new JsonObject
        {
            ["player"] = new JsonObject
            {
                ["identity"] = identity?.DeepClone(),
                ["playerDirection"] = 1,
                ["requestId"] = $"checkpoint-{label}-{DateTimeOffset.UtcNow.Ticks}",
            },
            ["saveName"] = name,
            ["expectedTick"] = tickNumber,
        };
        try
        {
            var reply = await h.WireAsync($"checkpoint-{label}-save", "lifecycle_save", request, cancellationToken).ConfigureAwait(false);
            Harness.Outcome(reply, "completed");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"lifecycle_save: {e.Message}", e);
        }

        var path = Config.ProfileSave(name);
        if (!File.Exists(path))
        {
            throw new AcceptanceException($"lifecycle_save completed but {path} is not there");
        }

        return new SaveResult(path, tickNumber, identity);
    }

    /// <summary>The "loaded" outcome of lifecycle_read_identity.</summary>
    private static async Task<JsonObject> ReadLoadedAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return Harness.Outcome(reply, "loaded").Value;
    }

    private void RecordError(string label, string message)
    {
        lock (_gate)
        {
            _errors.Add($"{label}: {message}");
        }
    }

    // The capture errors the ring records instead of failing the run; anything else (a bug) propagates.
    private static bool IsCaptureFailure(Exception e) =>
        e is AcceptanceException or IOException or UnauthorizedAccessException or JsonException;

    // Rounds half away from zero to a multiple of step, like Go's Duration.Round.
    private static TimeSpan RoundTo(TimeSpan d, TimeSpan step)
    {
        if (step <= TimeSpan.Zero)
        {
            return d;
        }

        var steps = Math.Round((double)d.Ticks / step.Ticks, MidpointRounding.AwayFromZero);
        return TimeSpan.FromTicks((long)steps * step.Ticks);
    }

    private static long Now() => Stopwatch.GetTimestamp();

    private static long ToTimestamp(TimeSpan d) => (long)(d.TotalSeconds * Stopwatch.Frequency);
}
