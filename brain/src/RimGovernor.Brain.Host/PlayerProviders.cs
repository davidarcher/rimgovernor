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

/// <summary>The clock review: unreviewed clock events and the player's acknowledgement, from the journal and the profile.</summary>
internal sealed class HostClockReview(StoreJournal journal, string profile) : IClockReview
{
    public Task<ClockReviewState> ReadAsync(CancellationToken cancellationToken) => journal.ReadClockReviewAsync(profile, cancellationToken);

    public Task<ClockReviewState> AcknowledgeAsync(ClockAcknowledgement acknowledgement, CancellationToken cancellationToken) =>
        journal.AcknowledgeClockEventsAsync(profile, acknowledgement, cancellationToken);
}
