using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The player routes' control seam over the <see cref="Player"/> (the player coordinator of the building runtime).
/// A resume the player asks for also clears a watchdog suspension: it is the one thing that does.
/// </summary>
internal sealed class HostPlayerControl(Player player, StoreJournal journal, StepSupervisor supervisor, HostLog log) : IPlayerControl
{
    public async Task<ControlRecord> ResumeAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        var record = await RunAsync(() => player.ResumeAsync(request, cancellationToken), request, cancellationToken).ConfigureAwait(false);
        if (record.Phase == ControlPhase.Running && supervisor.Suspension is not null)
        {
            supervisor.Clear();
            log.Info("brain_resumed", "the player resumed a suspended brain", ("request", request.RequestId));
        }
        return record;
    }

    public Task<ControlRecord> PauseAsync(ControlRequest request, CancellationToken cancellationToken) =>
        RunAsync(() => player.PauseAsync(request, cancellationToken), request, cancellationToken);

    public PlayerState State()
    {
        var state = player.State();
        return new PlayerState(state.Enabled, state.ObservationKnown, state.Snapshot);
    }

    /// <summary>
    /// A failure after the intent was journaled carries the record that says how far it got
    /// (<see cref="ControlFailureException"/>); one before it, or one the request never began, propagates as thrown.
    /// </summary>
    private async Task<ControlRecord> RunAsync(Func<Task<ControlRecord>> run, ControlRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not ControlFailureException)
        {
            ControlRecord? journaled = null;
            try
            {
                // The lookup is not tied to the caller's token: the failure may be that very token's, and the
                // record is what tells the player whether its intent stands.
                using var bound = new CancellationTokenSource(JournalLookupBound);
                journaled = await journal.LookupControlAsync(request.RequestId, bound.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // never begun, or the journal cannot say: the failure stands as thrown
            }
            log.Warn("control_failed", "a player control request failed", ("request", request.RequestId), ("kind", RimGovernor.Brain.Domain.Wire.Name(request.Kind)), ("phase", journaled is null ? "" : RimGovernor.Brain.Domain.Wire.Name(journaled.Phase)), ("error", e.ToString()));
            if (journaled is null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
            }
            throw new ControlFailureException(journaled, e);
        }
    }

    private static readonly TimeSpan JournalLookupBound = TimeSpan.FromSeconds(3);
}

/// <summary>The current control record, from the journal.</summary>
internal sealed class HostControlReader(StoreJournal journal) : IControlReader
{
    public Task<ControlRecord> CurrentControlAsync(CancellationToken cancellationToken) => journal.CurrentControlAsync(cancellationToken);
}

/// <summary>The brain's live target speed, set through the player API.</summary>
internal sealed class HostClockSpeed(ClockTargetSpeed target) : IClockSpeedControl
{
    public PbClock.Speed Speed => target.Speed;

    public void Change(PbClock.Speed speed) => target.Set(speed);
}

/// <summary>
/// The player's goals over the store (#3492): a cached snapshot for the colony the player is in, reloaded when
/// that colony changes and replaced by every save. <paramref name="world"/> is the observed player world, null while none is.
/// </summary>
internal sealed class HostGoals(StoreJournal journal, Func<RimGovernor.Brain.Store.World?> world, WakeSignal wake, GoalStatusBoard statuses) : IGoalsView, IGoalsEditor
{
    /// <summary>The goals Family's planner name: its id.</summary>
    internal const string PlannerName = "goals";

    private readonly object _mu = new();
    private GoalsRecord? _cached;

    public GoalsSnapshot Current()
    {
        if (world() is not { } w)
        {
            return new GoalsSnapshot(new GoalsRecord(), [], []);
        }
        lock (_mu)
        {
            if (_cached is null || _cached.World != w)
            {
                _cached = journal.LoadGoals(w, CancellationToken.None);
            }
            var (goalStatuses, themeStatuses) = statuses.Read(w);
            return new GoalsSnapshot(_cached, goalStatuses, themeStatuses);
        }
    }

    public Task SaveAsync(GoalsRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var w = world() ?? throw new RimGovernor.Brain.Domain.DomainException("no colony is observed");
        var stored = record with { World = w };
        stored.Validate();
        lock (_mu)
        {
            journal.SaveGoals(stored, cancellationToken);
            _cached = stored;
        }
        wake.NotifyPlanner(PlannerName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adopts the <paramref name="presetId"/> theme into the goals of <paramref name="w"/> at normal importance with no
    /// deadline, for a colony started from that theme (#3511). Already adopted is no change; any other refusal throws.
    /// </summary>
    internal void AdoptTheme(RimGovernor.Brain.Store.World w, PresetBook book, string presetId, CancellationToken cancellationToken)
    {
        lock (_mu)
        {
            var record = journal.LoadGoals(w, cancellationToken);
            if (record.Themes.Any(t => t.Preset == presetId))
            {
                return;
            }
            var edit = ThemeEdits.Adopt(record, book, presetId, RimGovernor.Brain.Policy.Importance.Normal, null);
            if (!edit.Ok)
            {
                throw new InvalidOperationException("adopt theme " + presetId + ": " + edit.Error);
            }
            var stored = edit.Record with { World = w };
            stored.Validate();
            journal.SaveGoals(stored, cancellationToken);
            _cached = stored;
        }
        wake.NotifyPlanner(PlannerName);
    }
}
