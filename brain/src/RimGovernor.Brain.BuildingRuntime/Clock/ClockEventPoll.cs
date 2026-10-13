using Google.Protobuf;

namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_poll.go (PollEvents and its page predicates). The lease and its renewal are
// not carried (Boundary.cs documents the lease as dropped), so the poll never reasons about a lease: it captures the
// native journal page, disables authority on interrupting evidence, and walks the review through the captured cursor.

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

    public bool Interrupted { get; init; }

    public IReadOnlyList<WakeOutcome> Wake { get; init; } = [];

    public IReadOnlyList<FactFamily> Invalidated { get; init; } = [];

    public IReadOnlyList<Facts.Section> InvalidatedSections { get; init; } = [];

    public bool AuthorityChanged { get; init; }

    public bool Stopped { get; init; }

    public DateTimeOffset? StoppedAt { get; init; }

    public ClockReviewState? Review { get; init; }
}

/// <summary>The newest cursor native held when this process first read events: below it events are history (Go <c>clockHistory</c>).</summary>
internal readonly record struct ClockHistory(long Cursor, bool Known, bool Unowned)
{
    /// <summary>The cursor at or below which events are history for reactions such as the manual note; -1 when the watermark was fixed under authority.</summary>
    public long Floor => !Known || !Unowned ? -1 : Cursor;
}

/// <summary>The latest grant a committed page carried (Go <c>clockGrant</c>).</summary>
internal readonly record struct ClockGrant(ulong Generation, long Cursor, bool Known)
{
    /// <summary>The cursor at which the generation the state holds enabled was granted, -1 when authority is off or the grant was not seen.</summary>
    public long CursorFor(ControlState state) =>
        !Known || !state.Enabled || !state.ObservationKnown || (ulong)state.Snapshot.Native.Value != Generation ? -1 : Cursor;

    /// <summary>Whether every hold precedes the grant of the generation the state holds: evidence that grant already answered.</summary>
    public bool Answers(ControlState state, IReadOnlyList<ClockHold> holds, long granted)
    {
        if (granted < 0)
        {
            granted = CursorFor(state);
        }
        if (granted < 0)
        {
            return false;
        }
        return holds.All(hold => hold.ThroughCursor <= granted);
    }
}

/// <summary>
/// Reads the native clock journal into the controller's inbox and walks the review forward (Go
/// <c>ClockScheduler.PollEvents</c>). It never takes the player gate: interruption invalidation precedes persistence
/// and owned cleanup, which may need to join an active command.
/// </summary>
internal sealed class ClockEventPoll(
    Player player,
    Session session,
    IClockEventNative native,
    ClockLatched latched,
    ClockWindowConfig config,
    IClock clock) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PbCommon.Identity? _identity;
    private ClockHistory _history;
    private ClockGrant _grant;
    private long _manualAt;
    private int _running;

    public void Dispose() => _gate.Dispose();

    /// <summary>Whether the last evidence showed an admitted window still running (Go <c>running</c>).</summary>
    public bool Running
    {
        get => Volatile.Read(ref _running) != 0;
        set => Volatile.Write(ref _running, value ? 1 : 0);
    }

    /// <summary>Records a Manual authority change: the player pressed a speed key at this wall time (Go <c>noteManual</c>).</summary>
    public void NoteManual(DateTimeOffset at) => Interlocked.Exchange(ref _manualAt, at.UtcTicks);

    /// <summary>How long since the last Manual bump and whether that is at least the quiet bound; with no bump seen the clock is quiet (Go <c>playerQuiet</c>).</summary>
    public (TimeSpan Since, bool Quiet) PlayerQuiet()
    {
        var at = Interlocked.Read(ref _manualAt);
        if (at == 0)
        {
            return (TimeSpan.Zero, true);
        }
        var since = clock.Now() - new DateTimeOffset(at, TimeSpan.Zero);
        return (since, since >= config.EffectivePlayerQuiet);
    }

    // Disables authority for what a poll found. Fresh evidence disables before persistence; evidence earlier polls
    // already applied is not applied again, because every Disable replaces the control epoch, which cancels an
    // Acquire's SetMode in flight (Go disableOnEvidence).
    private void DisableOnEvidence(bool fresh)
    {
        if (!fresh && !session.State().Enabled)
        {
            return;
        }
        session.Disable();
    }

    private static bool MatchesAuthority(PbCommon.ObservationContext observed, ControlState state) =>
        !state.Enabled
        || (state.ObservationKnown && observed.Identity.Equals(Boundary.Identity(state.Snapshot)) && observed.HasNativeGeneration && observed.NativeGeneration == (ulong)state.Snapshot.Native.Value);

    private async Task<PbCommon.Identity> EventsIdentityAsync(CancellationToken ct)
    {
        if (_identity is not null)
        {
            return _identity;
        }
        var snapshot = await native.ReadStepAsync(new BridgeApi.StepRequest(), ct).ConfigureAwait(false)
            ?? throw new HeldException("events identity read returned nothing");
        BridgeApi.ValidateContext(snapshot.Context);
        return snapshot.Context.Identity.Clone();
    }

    /// <summary>Polls once (Go <c>PollEvents</c>); a held review is a <see cref="HeldException"/>.</summary>
    public async Task<ClockPollResult> PollAsync(CancellationToken ct)
    {
        var interrupted = false;
        // Fresh is set once the poll holds a page that carries something earlier polls have not applied; until then a
        // disable is for evidence already applied (see DisableOnEvidence).
        var fresh = false;
        var failed = false;
        ClockPollResult result = new();

        async Task<ClockPollResult> FailAsync(Exception? cause)
        {
            failed = true;
            interrupted = true;
            Running = false;
            Exception? disabled = null;
            try
            {
                DisableOnEvidence(fresh);
            }
            catch (Exception e)
            {
                disabled = e;
            }
            Exception? cleaned = null;
            using var bounded = new CancellationTokenSource(config.CallTimeout);
            try
            {
                await session.CleanupClock(bounded.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                cleaned = e;
            }
            var error = ErrorJoin.Of(cause, disabled, cleaned);
            if (error is not null)
            {
                throw error;
            }
            return result with { Interrupted = true };
        }

        if (config.PageLimit is < 1 or > 128)
        {
            return await FailAsync(new ControlException("PollEvents: limit < 1 || limit > 128")).ConfigureAwait(false);
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var span = Tel.StartSpan("buildingruntime", "clock.poll");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(config.CallTimeout);
            var call = timeout.Token;
            var journal = player.Journal;

            async Task InvalidateAsync(string why, params (string Key, object? Value)[] attrs)
            {
                interrupted = true;
                if (fresh || session.State().Enabled)
                {
                    ClockEvents.AuthorityLost("poll " + why, [("fresh", fresh), .. attrs]);
                }
                DisableOnEvidence(fresh);
                await Task.CompletedTask.ConfigureAwait(false);
            }

            ClockReviewState review;
            try
            {
                review = await journal.ReadClockReviewAsync(config.Profile, call).ConfigureAwait(false);
                // A standing hold disables authority granted under it, unless the page read below shows the grant came
                // after the hold.
                var standing = review.Holds.Count > 0;
                await ClockMaintenance.MaintainAsync(journal, call).ConfigureAwait(false);
                await journal.CompactClockHistoryAsync(config.Profile, call).ConfigureAwait(false);
                var before = session.State();
                // The poll's one native read: the journal page after the review's cursor. A load between the
                // identity and the read answers StaleIdentity with the current context; the poll re-anchors on it and
                // asks again, once.
                var identity = await EventsIdentityAsync(call).ConfigureAwait(false);
                PbClock.EventsRequest request = new();
                PbClock.EventsReply? reply = null;
                Exception? readError = null;

                async Task ReadAsync()
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        request = new PbClock.EventsRequest { Identity = identity.Clone(), AfterCursor = review.InboxCursor, Limit = config.PageLimit };
                        readError = null;
                        try
                        {
                            reply = await native.ReadClockEventsAsync(request, call).ConfigureAwait(false);
                        }
                        catch (NativeFailureException e) when (attempt == 0 && e.Value.Code == PbCommon.FailureCode.StaleIdentity && e.Value.ObservedContext?.Identity is not null)
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
                if (readError is null && reply?.Page is { } first && review.InboxCursor == 0 && !_history.Known && !first.Gap
                    && first.NewestCursor - config.PageLimit > 0 && first.NewestCursor > LastCursor(first))
                {
                    var skip = first.NewestCursor - config.PageLimit;
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
                    _identity = null;
                    return await FailAsync(readError).ConfigureAwait(false);
                }
                var page = reply?.Page ?? throw new BridgeContractException("events reply carried no page");
                var current = page.Context;
                BridgeApi.ValidateContext(current);
                _identity = current.Identity.Clone();
                var state = session.State();
                if (!MatchesAuthority(current, state))
                {
                    if (state != before && !interrupted)
                    {
                        throw new AuthorityException();
                    }
                    ClockEvents.AuthorityLost("events poll context does not match the held authority",
                        ("held_known", state.ObservationKnown), ("held", state.Snapshot.ToString()), ("observed_generation", current.NativeGeneration), ("observed_tick", current.Tick));
                    try
                    {
                        session.DisableObserved(state);
                        interrupted = true;
                    }
                    catch (ConflictException)
                    {
                        if (interrupted)
                        {
                            return await FailAsync(new AuthorityException()).ConfigureAwait(false);
                        }
                        throw new AuthorityException();
                    }
                }
                BridgeApi.ValidateClockEventsPage(page, request);
                fresh = page.Gap || page.Events.Count > 0;
                // The first page fixes the history watermark: native's newest cursor when this process first read
                // events. While nothing is enabled, a page at or before it is not fresh evidence.
                if (!_history.Known)
                {
                    _history = new ClockHistory(page.NewestCursor, true, !state.Enabled);
                }
                // The grant the page carries is remembered first.
                if (Grant(page) is { } granted0)
                {
                    _grant = granted0;
                }
                var history = !page.Gap && LastCursor(page) <= _history.Cursor && (!state.Enabled || (_history.Unowned && _grant.CursorFor(state) < 0));
                if (history)
                {
                    fresh = false;
                }
                var latest = session.State();
                if (!history && !MatchesAuthority(page.Context, latest))
                {
                    if (latest != state && !interrupted && !page.Gap && !Interrupts(page))
                    {
                        throw new AuthorityException();
                    }
                    ClockEvents.AuthorityLost("events page context does not match the held authority",
                        ("held_known", latest.ObservationKnown), ("held", latest.Snapshot.ToString()), ("observed_generation", page.Context.NativeGeneration), ("observed_tick", page.Context.Tick), ("gap", page.Gap), ("events", EventKinds(page)));
                    try
                    {
                        session.DisableObserved(latest);
                        interrupted = true;
                    }
                    catch (ConflictException)
                    {
                        // Interruption evidence must still be captured even if authority changed.
                        if (!page.Gap && !Interrupts(page))
                        {
                            throw new AuthorityException();
                        }
                    }
                }
                // An interruption the page carries before the grant of the generation authority holds now was answered
                // by that grant; the hold it leaves still keeps a window from opening until acknowledged.
                var grantedAt = _grant.CursorFor(latest);
                // Events at or before the watermark are history: under a standing authority whose grant went unseen
                // they interrupt nothing it holds.
                if (grantedAt < 0 && _history.Unowned && latest.Enabled && latest.ObservationKnown)
                {
                    grantedAt = _history.Cursor;
                }
                var answered = !page.Gap && !InterruptsAfter(page, grantedAt);
                if (standing && !_grant.Answers(latest, review.Holds, grantedAt))
                {
                    await InvalidateAsync("found a standing hold the held grant does not answer", ("holds", review.Holds.Count), ("granted_cursor", grantedAt)).ConfigureAwait(false);
                }
                if (page.Gap || InterruptsAfter(page, grantedAt))
                {
                    await InvalidateAsync("found interrupting events", ("gap", page.Gap), ("events", EventKinds(page)), ("granted_cursor", grantedAt)).ConfigureAwait(false);
                }
                call.ThrowIfCancellationRequested();
                var (_, created) = await journal.AppendClockEventsAsync(config.Profile, request, page, call).ConfigureAwait(false);
                var captured = created;
                IReadOnlyList<WakeOutcome> wake = [];
                IReadOnlyList<FactFamily> families = [];
                IReadOnlyList<Facts.Section> sections = [];
                var authorityChanged = false;
                var stopped = false;
                DateTimeOffset? stoppedAt = null;
                if (captured)
                {
                    Tel.ObserveTick((long)page.Context.Tick);
                    Publish(page);
                    if (ManualAfter(page, _history.Floor))
                    {
                        NoteManual(clock.Now());
                    }
                    if (StoppedIn(page))
                    {
                        Running = false;
                    }
                    var parsed = Pkg.ClockPageWakeStopped(page);
                    (wake, families, authorityChanged, stopped, stoppedAt) = (parsed.Outcomes, parsed.Families, parsed.Authority, parsed.Stopped, parsed.StopAt);
                    sections = Pkg.ClockPageSections(page);
                    // Recorded here, not only through the step's wake reason: a step already past taking its reason
                    // must still see an outcome this page carried before it admits a window.
                    latched.Remember(wake);
                }
                result = new ClockPollResult
                {
                    Captured = captured,
                    Wake = wake,
                    Invalidated = families,
                    InvalidatedSections = sections,
                    AuthorityChanged = authorityChanged,
                    Stopped = stopped,
                    StoppedAt = stoppedAt,
                };
                review = await journal.ReadClockReviewAsync(config.Profile, call).ConfigureAwait(false);
                var reviewed = await journal.ReviewClockEventsAsync(config.Profile, review.Revision, call).ConfigureAwait(false);
                if (reviewed.Holds.Count > 0 && answered && !interrupted && _grant.Answers(latest, reviewed.Holds, grantedAt))
                {
                    // Holds the grant answers are acknowledged here: the player's own resume granted this generation
                    // after every interruption they cover, and nothing else would clear them while authority stands.
                    var ack = new ClockAcknowledgement($"clock-ack-{reviewed.Revision}-{reviewed.ReviewedCursor}", reviewed.Revision, reviewed.ReviewedCursor);
                    reviewed = await journal.AcknowledgeClockEventsAsync(config.Profile, ack, call).ConfigureAwait(false);
                }
                result = result with { Review = reviewed, Interrupted = interrupted };
                if (reviewed.Holds.Count > 0 || reviewed.ReviewedCursor != reviewed.InboxCursor || reviewed.ReviewedCursor != page.NewestCursor)
                {
                    if (answered && !interrupted && _grant.Answers(latest, reviewed.Holds, grantedAt))
                    {
                        Running = false;
                        using var cleanup = new CancellationTokenSource(config.CallTimeout);
                        Exception? cleaned = null;
                        try
                        {
                            await session.CleanupClock(cleanup.Token).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            cleaned = e;
                        }
                        throw ErrorJoin.Of(new HeldException(), cleaned)!;
                    }
                    return await FailAsync(new HeldException()).ConfigureAwait(false);
                }
                call.ThrowIfCancellationRequested();
                if (interrupted)
                {
                    return await FailAsync(null).ConfigureAwait(false);
                }
                return result;
            }
            catch (Exception e) when (!failed && e is not (OperationCanceledException or HeldException or AuthorityException))
            {
                return await FailAsync(e).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static long LastCursor(PbClock.EventsPage page) => page.Events.Count == 0 ? -1 : page.Events[^1].Cursor;

    private static ClockGrant? Grant(PbClock.EventsPage page)
    {
        ClockGrant? found = null;
        foreach (var @event in page.Events)
        {
            if (@event.EventCase == PbClock.Event.EventOneofCase.AuthorityChanged && @event.AuthorityChanged.Active && @event.AuthorityChanged.HasGeneration)
            {
                found = new ClockGrant(@event.AuthorityChanged.Generation, @event.Cursor, true);
            }
        }
        return found;
    }

    private static bool Interrupts(PbClock.EventsPage page) => InterruptsAfter(page, -1);

    private static bool InterruptsAfter(PbClock.EventsPage page, long granted) =>
        page.Events.Any(e => e.Cursor > granted && ClockJournal.PageInterrupts(e, page));

    private static bool StoppedIn(PbClock.EventsPage page) =>
        page.Events.Any(e => e.EventCase == PbClock.Event.EventOneofCase.Stopped && !ClockJournal.OtherWorld(e, page));

    // Whether the page carries a Manual authority change past the cursor: the player pressed a speed key, which
    // revokes authority before the service re-acquires it (Go clockPollManual).
    private static bool ManualAfter(PbClock.EventsPage page, long after) =>
        page.Events.Any(e => e.Cursor > after && e.EventCase == PbClock.Event.EventOneofCase.AuthorityChanged && e.AuthorityChanged.Reason == "Manual");

    // Names, for the clock trace, which events tripped the interruption check (Go clockPollEventKinds).
    private static string EventKinds(PbClock.EventsPage page) =>
        string.Join(",", page.Events.Select(e => e.EventCase == PbClock.Event.EventOneofCase.Stopped ? "Stopped(" + e.Stopped.Reason + ")" : e.EventCase.ToString()));

    // Publishes the typed service events a committed page carries: clock_stop, authority and alert rows; the rule
    // events file their rule rows. Every other kind is the step reason's business (Go clockPollEvents).
    private static void Publish(PbClock.EventsPage page)
    {
        foreach (var @event in page.Events)
        {
            switch (@event.EventCase)
            {
                case PbClock.Event.EventOneofCase.Stopped:
                    ClockEvents.Event("clock-scheduler", "clock_stop", "window stopped", [("reason", @event.Stopped.Reason.ToString()), ("evidence", StopEvidence(@event.Stopped)), ("cursor", @event.Cursor), ("observed_at_unix_ms", @event.ObservedAtUnixMs), ("benign", ClockJournal.BenignStopEvent(@event.Stopped)), .. StopLegs(@event, @event.Stopped)]);
                    break;
                case PbClock.Event.EventOneofCase.AuthorityChanged:
                    var changed = @event.AuthorityChanged;
                    ClockEvents.Event("clock-scheduler", "authority", "authority changed", ("change", "changed"), ("reason", changed.Reason), ("active", changed.Active), ("generation", changed.Generation), ("previous_generation", changed.PreviousGeneration), ("cursor", @event.Cursor));
                    break;
                case PbClock.Event.EventOneofCase.Alert:
                    ClockEvents.Event("clock-scheduler", "alert", "game alert", ("key", @event.Alert.Key), ("label", @event.Alert.Label), ("priority", @event.Alert.Priority), ("cursor", @event.Cursor));
                    break;
                case PbClock.Event.EventOneofCase.RuleFired or PbClock.Event.EventOneofCase.RuleLeaseExpired:
                    Tel.Decide(RuleDecision(@event));
                    break;
            }
        }
    }

    // The "rule" row of a native rule event: a firing is verdict fired, reason the trigger word, target the actor; a
    // lapsed lease is verdict expired, reason lease_expired, target the rule runtime (Go ruleDecision).
    private static Decision RuleDecision(PbClock.Event @event)
    {
        var attrs = new Dictionary<string, object?> { ["cursor"] = @event.Cursor };
        if (@event.EventCase == PbClock.Event.EventOneofCase.RuleFired)
        {
            var fired = @event.RuleFired;
            attrs["rule"] = fired.RuleId;
            attrs["target"] = fired.TargetId;
            attrs["params"] = new Dictionary<string, object?> { ["job"] = fired.Job, ["radius"] = fired.Radius };
            attrs["tick"] = fired.Tick;
            return new Decision { Kind = "rule", Component = "clock-scheduler", Verdict = "fired", Reason = "prey_killed", Target = fired.ActorId, Attrs = attrs };
        }
        var expired = @event.RuleLeaseExpired;
        attrs["expires_at_tick"] = expired.ExpiresAtTick;
        attrs["deactivated"] = expired.Deactivated;
        return new Decision { Kind = "rule", Component = "clock-scheduler", Verdict = "expired", Reason = "lease_expired", Target = "rules", Attrs = attrs };
    }

    // The stop's latency legs on the row: the live half of the split the throughput report computes offline (Go clockStopLegs).
    private static (string, object?)[] StopLegs(PbClock.Event @event, PbClock.StopEvent stop)
    {
        var tick = @event.Context?.Tick ?? 0;
        var attrs = new List<(string, object?)> { ("tick", tick) };
        if (stop.HasDetectedTick)
        {
            attrs.Add(("detected_tick", stop.DetectedTick));
            attrs.Add(("stop_ticks", tick - stop.DetectedTick));
            if (stop.HasOccurrenceTick)
            {
                attrs.Add(("occurrence_tick", stop.OccurrenceTick));
                attrs.Add(("detect_ticks", stop.DetectedTick - stop.OccurrenceTick));
            }
        }
        if (@event.HasAgeAtReplyMs)
        {
            attrs.Add(("age_at_reply_ms", (double)@event.AgeAtReplyMs));
        }
        return [.. attrs];
    }

    // Names the evidence a stop event carries (the oneof case: budget, pause, watch, ...), "" when it carries none.
    private static string StopEvidence(PbClock.StopEvent stop) =>
        stop.EvidenceCase == PbClock.StopEvent.EvidenceOneofCase.None ? "" : stop.EvidenceCase.ToString().ToLowerInvariant();
}
