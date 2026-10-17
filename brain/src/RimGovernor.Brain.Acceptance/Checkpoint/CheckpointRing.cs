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
/// open leaves a consistent copy.
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

    /// <summary>The run's configuration; the save lands in its game profile.</summary>
    public Config Config { get; }

    /// <summary>Returns the harness holding the game, null while a service holds it. Consulted at every capture.</summary>
    public Func<Harness?>? Bridge { get; set; }

    /// <summary>Returns the running service holding the game, null otherwise. Consulted at every capture.</summary>
    public Func<ServiceProcess?>? Service { get; set; }

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

    /// <summary>When set, replaces the bridge and service save paths (tests only).</summary>
    public CheckpointSaver? Saver { get; set; }

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

    /// <summary>The captures that failed, for the report.</summary>
    public IReadOnlyList<string> Errors()
    {
        lock (_gate)
        {
            return [.. _errors];
        }
    }

    /// <summary>The current run-phase offset: the time since Activate.</summary>
    public TimeSpan Offset()
    {
        lock (_gate)
        {
            return OffsetLocked();
        }
    }

    private TimeSpan OffsetLocked() => _activated ? Stopwatch.GetElapsedTime(_started) : TimeSpan.Zero;

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
        string label;
        lock (_gate)
        {
            var now = Now();
            if (_busy || Stopwatch.GetElapsedTime(_last, now) < Every || now < _nextCheck)
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
            if (e is NativeTransportException or RunLostException)
            {
                // The game did not answer the checkpoint's reads: nothing later in the run can succeed, so the run ends
                // here instead of retrying every cadence until its budget.
                throw;
            }
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
                // The next call may find the run over (or the game held again); ask again in a moment, not on
                // every call of a running clock.
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

            // A provisional index after every capture: a window the harness never closes (killed with its tool shell)
            // still leaves its newest bundle to publish.
            var provisional = new Ring
            {
                Case = Case,
                SourceRevision = SourceRevision,
                Next = entry.Label,
            };
            foreach (var e in _entries)
            {
                provisional.Entries.Add(e);
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
            Serve = Serve,
            SourceRevision = SourceRevision,
            Package = Fingerprint.Package,
            Start = Fingerprint.Start,
            Expansions = Fingerprint.Expansions,
            At = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Path = dir,
        };
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
            File.WriteAllText(Path.Combine(dir, Checkpoints.CheckpointSidecar), entry.ToJsonText());
        }
        catch
        {
            Checkpoints.TryDeleteTree(dir);
            throw;
        }

        return entry;
    }

    /// <summary>
    /// Saves through lifecycle_save on the harness session: the game must be paused (force pauses it), and the
    /// save must complete at the tick the identity read observed.
    /// </summary>
    private async Task<SaveResult> BridgeSaveAsync(Harness h, string name, string label, bool force, CancellationToken cancellationToken)
    {
        var loaded = await h.LoadedAsync($"checkpoint-{label}-identity", cancellationToken).ConfigureAwait(false);
        if (loaded.Paused != true)
        {
            if (!force)
            {
                throw new CaptureDeclinedException(notPaused: true);
            }

            await h.PauseClockAsync($"checkpoint-{label}-pause", cancellationToken).ConfigureAwait(false);
            loaded = await h.LoadedAsync($"checkpoint-{label}-identity-paused", cancellationToken).ConfigureAwait(false);
        }

        var tick = loaded.Context?.Tick ?? -1;
        var tickNumber = tick < 0 ? 0UL : (ulong)tick;
        try
        {
            _ = await h.SaveGameAsync($"checkpoint-{label}-save", loaded, name, $"checkpoint-{label}-{DateTimeOffset.UtcNow.Ticks}", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e) when (e is not RunLostException)
        {
            throw new AcceptanceException($"lifecycle_save: {e.Message}", e);
        }

        var path = Config.ProfileSave(name);
        if (!File.Exists(path))
        {
            throw new AcceptanceException($"lifecycle_save completed but {path} is not there");
        }

        return new SaveResult(path, tickNumber, loaded.Context?.Identity is { } identity ? Harness.IdentityNode(identity) : null);
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

    // Rounds half away from zero to a multiple of step.
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
