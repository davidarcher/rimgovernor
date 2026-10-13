using System.Globalization;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Runs the bot for every fresh observed world once, without a launcher click: the world present at startup, and
/// each new load token a native load afterwards issues (Go <c>autoResumer</c>, serve_resume.go). It goes through
/// exactly the control intent <c>POST /api/player/control/resume</c> submits, so authority, the root plan and the
/// fresh review are established the same way. A world is offered a resume once per run (bounded retries while
/// observations settle), so a player's later Pause for that world stands. Authority the world then loses for any
/// other reason is re-offered once per loss while the journal's current control intent is still a running Resume
/// for that world: Manual is the only thing that pauses the controller. A watchdog suspension is not such a loss: a
/// suspended brain waits for the player.
/// </summary>
internal sealed class AutoResumer
{
    /// <summary>Bounds how often one observed world is offered a startup resume before the controller leaves it to the launcher.</summary>
    internal const int Attempts = 20;

    /// <summary>
    /// Bounds how often one world that lost authority under a resume intent is re-acquired in a process; past it
    /// the loss is left to the launcher. From the third cycle each re-acquire first waits a doubling number of
    /// steps (capped at <see cref="BackoffMax"/>), so a loop that keeps revoking the grant cannot churn authority unbounded.
    /// </summary>
    internal const int Cycles = 8;

    internal const int BackoffMax = 32;

    /// <summary>
    /// How many consecutive steps a world must hold live authority before its loss count is forgiven. Without it
    /// the cycles accumulated over a whole process: eight losses spread over hours of healthy play left the colony
    /// unowned for good, and a force-pausing dialog that opened next was never answered.
    /// </summary>
    internal const int HealthySteps = 100;

    private readonly FacadeWorldSource _worlds;
    private readonly Player _player;
    private readonly StoreJournal _controls;
    private readonly StepSupervisor _supervisor;
    private readonly HostLog _log;

    // Makes this process's request ids distinct from those a previous process left in a reopened journal, which
    // would otherwise replay the old record instead of acquiring authority again.
    private readonly string _process = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    private readonly Dictionary<World, int> _attempts = [];
    private readonly HashSet<World> _settled = [];

    // A settled world was observed with live authority, so one later loss re-arms exactly one resume cycle rather
    // than looping against a world native keeps refusing.
    private readonly HashSet<World> _running = [];
    private readonly Dictionary<World, int> _cycles = [];

    // The steps a lost world still waits before re-acquiring.
    private readonly Dictionary<World, int> _backoff = [];

    // Consecutive live steps since the last loss.
    private readonly Dictionary<World, int> _healthy = [];

    public AutoResumer(FacadeWorldSource worlds, Player player, StoreJournal controls, StepSupervisor supervisor, HostLog log)
    {
        _worlds = worlds;
        _player = player;
        _controls = controls;
        _supervisor = supervisor;
        _log = log;
    }

    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ = await StepAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // The resumer is a convenience: it must never take the host down.
                _log.Warn("auto_resume_error", "auto resume step failed", ("error", e.Message));
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Offers the currently observed fresh world a resume if it has not been settled yet; true when a resume was attempted.</summary>
    internal async Task<bool> StepAsync(CancellationToken ct)
    {
        World world;
        try
        {
            world = await _worlds.ReadWorldAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
        if (_supervisor.Suspension is not null)
        {
            return false;
        }
        var state = _player.State();
        var live = state.Enabled && state.ObservationKnown && state.Snapshot.Colony == world.Colony && state.Snapshot.Load == world.Load && state.Snapshot.Map == world.Map;
        if (_settled.Contains(world))
        {
            if (live)
            {
                _ = _running.Add(world);
                var healthy = _healthy.GetValueOrDefault(world) + 1;
                _healthy[world] = healthy;
                if (healthy >= HealthySteps)
                {
                    _cycles[world] = 0;
                    _ = _backoff.Remove(world);
                }
                return false;
            }
            _healthy[world] = 0;
            if (!_running.Contains(world) || !await ResumeIntendedAsync(world, ct).ConfigureAwait(false))
            {
                return false;
            }
            var cycles = _cycles.GetValueOrDefault(world);
            if (cycles >= Cycles)
            {
                _ = _running.Remove(world);
                _log.Warn("auto_resume_gave_up", "authority lost too often; leaving it to the launcher's Resume", ("world", Name(world)), ("cycles", Cycles));
                return false;
            }
            if (cycles >= 2)
            {
                if (!_backoff.TryGetValue(world, out var waiting))
                {
                    waiting = Math.Min(1 << (cycles - 2), BackoffMax);
                }
                if (waiting > 0)
                {
                    _backoff[world] = waiting - 1;
                    return false;
                }
                _ = _backoff.Remove(world);
            }
            _ = _running.Remove(world);
            _ = _settled.Remove(world);
            _attempts[world] = 0;
            _cycles[world] = cycles + 1;
            _log.Info("auto_resume_reacquire", "authority lost while its control intent is resume; re-acquiring", ("world", Name(world)));
        }
        if (live)
        {
            _ = _settled.Add(world);
            _ = _running.Add(world);
            return false;
        }
        var attempt = _attempts.GetValueOrDefault(world) + 1;
        _attempts[world] = attempt;
        if (attempt > Attempts)
        {
            _ = _settled.Add(world);
            _log.Warn("auto_resume_gave_up", "giving up on the world; use the launcher's Resume", ("world", Name(world)), ("attempts", Attempts));
            return false;
        }
        var request = new ControlRequest(
            string.Create(CultureInfo.InvariantCulture, $"auto-resume/{_process}/{world.Colony.Value}/{world.Load.Value}/{world.Map.Value}/{_cycles.GetValueOrDefault(world)}/{attempt}"),
            ControlKind.Resume,
            world);
        string failure;
        try
        {
            var record = await _player.ResumeAsync(request, ct).ConfigureAwait(false);
            if (record.Phase == ControlPhase.Running)
            {
                _ = _settled.Add(world);
                _ = _running.Add(world);
                _log.Info("auto_resume_running", "auto resume: running", ("world", Name(world)));
                return true;
            }
            failure = "control phase " + record.Phase;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            failure = e.Message;
        }
        _log.Warn("auto_resume_failed", "auto resume attempt failed", ("world", Name(world)), ("attempt", attempt), ("error", failure));
        return true;
    }

    /// <summary>
    /// Whether the journal's current control intent is a running Resume for <paramref name="world"/>. Anything
    /// else -- a Pause, a different world, an unfinished or refused record, no record, or an unreadable journal --
    /// leaves the lost authority to the launcher.
    /// </summary>
    private async Task<bool> ResumeIntendedAsync(World world, CancellationToken ct)
    {
        try
        {
            var record = await _controls.CurrentControlAsync(ct).ConfigureAwait(false);
            return record.Request.Kind == ControlKind.Resume && record.Phase == ControlPhase.Running && record.Request.World == world;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string Name(World world) => string.Create(CultureInfo.InvariantCulture, $"{world.Colony.Value}/{world.Load.Value}/{world.Map.Value}");
}
