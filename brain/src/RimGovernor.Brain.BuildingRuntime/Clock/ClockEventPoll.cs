namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_poll.go (PollEvents and its page predicates). The Go lease and its renewal are
// not carried (Boundary.cs documents the lease as dropped). The poll captures the native journal page into the
// controller's inbox and marks it reviewed; it never disables authority for a hold and never acknowledges one: native
// holds itself on a hazard and stays held until the run driver, having planned against the held world and
// acknowledged what it reviewed, sends Run.

/// <summary>What the native clock exposes to the event poll (Go <c>ClockEventNative</c>).</summary>
public interface IClockEventNative
{
    /// <summary>One bare or identified step read; the poll uses it only to learn the current world's identity.</summary>
    Task<PbObservations.BundleSnapshot?> ReadStepAsync(BridgeApi.StepRequest request, CancellationToken ct);

    /// <summary>The journal page after a cursor. A refusal is a <see cref="NativeFailureException"/>.</summary>
    Task<PbClock.EventsReply> ReadClockEventsAsync(PbClock.EventsRequest request, CancellationToken ct);
}

/// <summary>What one poll saw (Go <c>ClockPollResult</c>).</summary>
internal sealed record ClockPollResult
{
    public bool Captured { get; init; }

    public IReadOnlyList<FactFamily> Invalidated { get; init; } = [];

    public IReadOnlyList<Facts.Section> InvalidatedSections { get; init; } = [];

    public bool AuthorityChanged { get; init; }

    /// <summary>Whether the page carried a hold native took (not one the brain asked for).</summary>
    public bool Held { get; init; }

    public DateTimeOffset? HeldAt { get; init; }

    public ClockReviewState? Review { get; init; }
}

/// <summary>
/// Reads the native clock journal into the controller's inbox and marks it reviewed (Go
/// <c>ClockScheduler.PollEvents</c>). It never takes the player gate.
/// </summary>
internal sealed class ClockEventPoll(
    Player player,
    Session session,
    IClockEventNative native,
    IClock clock,
    ClockRunConfig config) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PbCommon.Identity? _identity;
    private bool _historyKnown;
    private long _reviewedAtTicks;

    public void Dispose() => _gate.Dispose();

    /// <summary>The loaded world's identity as of the last page the poll read; null before the first page or after a failed read.</summary>
    public PbCommon.Identity? Identity => Volatile.Read(ref _identity);

    /// <summary>When the review last moved forward; a step that began after it has planned against everything the review covers.</summary>
    public DateTimeOffset ReviewedAt => new(Volatile.Read(ref _reviewedAtTicks), TimeSpan.Zero);

    private static bool MatchesAuthority(PbCommon.ObservationContext observed, ControlState state) =>
        !state.Enabled
        || (state.ObservationKnown && Equals(observed.Identity, Boundary.Identity(state.Snapshot)) && observed.NativeGeneration is not null && observed.NativeGeneration.GetValueOrDefault() == (ulong)state.Snapshot.Native.Value);

    private async Task<PbCommon.Identity> EventsIdentityAsync(CancellationToken ct)
    {
        if (Identity is { } known)
        {
            return known;
        }
        var snapshot = await native.ReadStepAsync(new BridgeApi.StepRequest(), ct).ConfigureAwait(false)
            ?? throw new HeldException("events identity read returned nothing");
        return snapshot.Context?.Identity ?? throw new BridgeContractException("events identity read carried no identity");
    }

    /// <summary>Polls once (Go <c>PollEvents</c>).</summary>
    public async Task<ClockPollResult> PollAsync(CancellationToken ct)
    {
        if (config.PageLimit is < 1 or > 128)
        {
            throw new ControlException("PollEvents: limit < 1 || limit > 128");
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var span = Tel.StartSpan("buildingruntime", "clock.poll");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(config.CallTimeout);
            var call = timeout.Token;
            var journal = player.Journal;

            var review = await journal.ReadClockReviewAsync(config.Profile, call).ConfigureAwait(false);
            await journal.CompactClockHistoryAsync(config.Profile, call).ConfigureAwait(false);
            // The poll's one native read: the journal page after the review's cursor. A load between the identity and
            // the read answers StaleIdentity with the current context; the poll re-anchors on it and asks again, once.
            var identity = await EventsIdentityAsync(call).ConfigureAwait(false);
            PbClock.EventsRequest request = new();
            PbClock.EventsReply? reply = null;
            Exception? readError = null;

            async Task ReadAsync()
            {
                for (var attempt = 0; ; attempt++)
                {
                    request = new PbClock.EventsRequest { Identity = identity, AfterCursor = review.InboxCursor, Limit = config.PageLimit };
                    readError = null;
                    try
                    {
                        reply = await native.ReadClockEventsAsync(request, call).ConfigureAwait(false);
                    }
                    catch (NativeFailureException e) when (attempt == 0 && e.Value.Code.GetValueOrDefault() == PbCommon.FailureCode.StaleIdentity && e.Value.ObservedContext?.Identity is not null)
                    {
                        identity = e.Value.ObservedContext.Identity;
                        continue;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        readError = e;
                        reply = null;
                    }
                    break;
                }
            }

            await ReadAsync().ConfigureAwait(false);
            // A journal that never read this profile adopts the native backlog but its newest page as history
            // instead of replaying every earlier session a page per poll.
            if (readError is null && reply?.Outcome?.Page is { } first && review.InboxCursor == 0 && !_historyKnown && !first.Gap.GetValueOrDefault()
                && first.NewestCursor - config.PageLimit > 0 && first.NewestCursor > LastCursor(first))
            {
                var skip = first.NewestCursor.GetValueOrDefault() - config.PageLimit;
                if (await journal.AdoptClockBacklogAsync(config.Profile, skip, call).ConfigureAwait(false))
                {
                    ClockEvents.Event("clock-scheduler", "clock_journal", "clock journal adopted native backlog", ("through_cursor", skip), ("newest_cursor", first.NewestCursor));
                    review = await journal.ReadClockReviewAsync(config.Profile, call).ConfigureAwait(false);
                    if (first.Context?.Identity is { } adopted)
                    {
                        identity = adopted;
                    }
                    await ReadAsync().ConfigureAwait(false);
                }
            }
            if (readError is not null)
            {
                Volatile.Write(ref _identity, null);
                throw readError;
            }
            var page = reply?.Outcome?.Page ?? throw new BridgeContractException("events reply carried no page");
            var current = page.Context ?? throw new BridgeContractException("events page carried no context");
            Volatile.Write(ref _identity, current.Identity);
            var state = session.State();
            if (!MatchesAuthority(current, state))
            {
                // The loaded world is not the one authority was granted for: authority ends, the page is still captured.
                ClockEvents.AuthorityLost("events poll context does not match the held authority",
                    ("held_known", state.ObservationKnown), ("held", state.Snapshot.ToString()), ("observed_generation", current.NativeGeneration), ("observed_tick", current.Tick));
                if (!TryDisableObserved(state))
                {
                    ClockEvents.Event("clock-scheduler", "authority", "authority changed under the poll");
                }
            }
            BridgeApi.ValidateClockEventsPage(page, request);
            _historyKnown = true;
            call.ThrowIfCancellationRequested();
            var (_, captured) = await journal.AppendClockEventsAsync(config.Profile, request, page, call).ConfigureAwait(false);
            ClockPageWake wake = new([], false, false, null);
            IReadOnlyList<Facts.Section> sections = [];
            if (captured)
            {
                Tel.ObserveTick(current.Tick);
                Publish(page);
                wake = Pkg.PageWake(page);
                sections = Pkg.ClockPageSections(page);
            }
            review = await journal.ReadClockReviewAsync(config.Profile, call).ConfigureAwait(false);
            var before = review.ReviewedCursor;
            var reviewed = await journal.ReviewClockEventsAsync(config.Profile, review.Revision, call).ConfigureAwait(false);
            if (reviewed.ReviewedCursor != before)
            {
                Volatile.Write(ref _reviewedAtTicks, clock.Now().UtcTicks);
            }
            call.ThrowIfCancellationRequested();
            return new ClockPollResult
            {
                Captured = captured,
                Invalidated = wake.Families,
                InvalidatedSections = sections,
                AuthorityChanged = wake.Authority,
                Held = wake.Stopped,
                HeldAt = wake.StopAt,
                Review = reviewed,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    // Disables authority only if it still is the state the poll observed; false when it changed under the poll.
    private bool TryDisableObserved(ControlState observed)
    {
        try
        {
            session.DisableObserved(observed);
            return true;
        }
        catch (ConflictException)
        {
            return false;
        }
    }

    private static long LastCursor(PbClock.EventsPage page) => page.Events.Count == 0 ? -1 : page.Events[^1].Cursor.GetValueOrDefault();

    // The oneof case name of a wire choice record ("HealthCase" -> "Health"), "None" when no case is set.
    private static string CaseName(object? choice) =>
        choice is null ? "None" : choice.GetType().Name.Replace("Case", "", StringComparison.Ordinal);

    // Publishes the typed service events a committed page carries: clock_hold, clock_resume, authority and alert
    // rows. Every other kind is the step reason's business (Go clockPollEvents).
    private static void Publish(PbClock.EventsPage page)
    {
        foreach (var @event in page.Events)
        {
            switch (@event.Event)
            {
                case PbClock.ClockEventEvent.HoldEnteredCase { Value: var held }:
                    ClockEvents.Event("clock-scheduler", "clock_hold", "clock held", [("reason", held.Reason.GetValueOrDefault().ToString()), ("evidence", HoldEvidenceName(held)), ("cursor", @event.Cursor.GetValueOrDefault()), ("observed_at_unix_ms", @event.ObservedAtUnixMs.GetValueOrDefault()), ("benign", ClockJournal.BenignHoldEvent(held)), .. HoldLegs(@event, held)]);
                    break;
                case PbClock.ClockEventEvent.ResumedCase { Value: var resumed }:
                    ClockEvents.Event("clock-scheduler", "clock_resume", "clock resumed", ("from", resumed.From.GetValueOrDefault().ToString()), ("speed", resumed.Speed.GetValueOrDefault().ToString()), ("cursor", @event.Cursor.GetValueOrDefault()));
                    break;
                case PbClock.ClockEventEvent.AuthorityChangedCase { Value: var changed }:
                    ClockEvents.Event("clock-scheduler", "authority", "authority changed", ("change", "changed"), ("reason", (changed.Reason ?? "")), ("active", changed.Active.GetValueOrDefault()), ("generation", changed.Generation.GetValueOrDefault()), ("previous_generation", changed.PreviousGeneration.GetValueOrDefault()), ("cursor", @event.Cursor.GetValueOrDefault()));
                    break;
                case PbClock.ClockEventEvent.AlertCase { Value: var alert }:
                    ClockEvents.Event("clock-scheduler", "alert", "game alert", ("key", (alert.Key ?? "")), ("label", (alert.Label ?? "")), ("priority", (alert.Priority ?? "")), ("cursor", @event.Cursor.GetValueOrDefault()));
                    break;
            }
        }
    }

    // The hold's latency legs on the row: the live half of the split the throughput report computes offline (Go clockStopLegs).
    private static (string, object?)[] HoldLegs(PbClock.ClockEvent @event, PbClock.HoldEntered held)
    {
        var tick = @event.Context?.Tick ?? 0;
        var attrs = new List<(string, object?)> { ("tick", tick) };
        if (held.DetectedTick is not null)
        {
            attrs.Add(("detected_tick", held.DetectedTick.GetValueOrDefault()));
            attrs.Add(("hold_ticks", tick - held.DetectedTick.GetValueOrDefault()));
            if (held.OccurrenceTick is not null)
            {
                attrs.Add(("occurrence_tick", held.OccurrenceTick.GetValueOrDefault()));
                attrs.Add(("detect_ticks", held.DetectedTick.GetValueOrDefault() - held.OccurrenceTick.GetValueOrDefault()));
            }
        }
        if (@event.AgeAtReplyMs is { } ageAtReplyMs)
        {
            attrs.Add(("age_at_reply_ms", (double)ageAtReplyMs));
        }
        return [.. attrs];
    }

    // Names the evidence a hold carries (the oneof case: pause, health, watch, ...), "" when it carries none.
    private static string HoldEvidenceName(PbClock.HoldEntered held) =>
        held.Evidence is null ? "" : CaseName(held.Evidence).ToLowerInvariant();
}
