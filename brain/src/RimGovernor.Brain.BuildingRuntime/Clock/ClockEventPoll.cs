namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_poll.go (PollEvents and its page predicates). The Go lease and its renewal are
// not carried (Boundary.cs documents the lease as dropped). The poll pages the native ring after an in-memory cursor
// for wakes and the authority check; nothing about events is kept. It never disables authority for a hold and never releases
// one: native holds itself on a hazard and stays held until the run driver sends a Run whose hold serial matches.

/// <summary>What the native clock exposes to the event poll (Go <c>ClockEventNative</c>).</summary>
public interface IClockEventNative
{
    /// <summary>One bare or identified step read; the poll uses it only to learn the current world's identity.</summary>
    Task<PbObservations.BundleSnapshot?> ReadStepAsync(BridgeApi.StepRequest request, CancellationToken ct);

    /// <summary>The ring page after a cursor. A refusal is a <see cref="NativeFailureException"/>.</summary>
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
}

/// <summary>
/// Pages the native clock ring after an in-memory cursor (Go <c>ClockScheduler.PollEvents</c>). The cursor starts at
/// the ring's newest on the first page: a started or restarted brain replays no backlog, and its first step plans
/// Full. It never takes the player gate.
/// </summary>
internal sealed class ClockEventPoll(
    Session session,
    IClockEventNative native,
    ClockRunConfig config) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PbCommon.Identity? _identity;
    private long _cursor = -1;

    public void Dispose() => _gate.Dispose();

    /// <summary>The loaded world's identity as of the last page the poll read; null before the first page or after a failed read.</summary>
    public PbCommon.Identity? Identity => Volatile.Read(ref _identity);

    /// <summary>The ring cursor the poll has read through; -1 before its first page.</summary>
    public long Cursor => Volatile.Read(ref _cursor);

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
            // The poll's one native read: the ring page after the cursor. A load between the identity and the read
            // answers StaleIdentity with the current context; the poll re-anchors on it and asks again, once.
            var identity = await EventsIdentityAsync(call).ConfigureAwait(false);
            PbClock.EventsRequest request = new();
            PbClock.EventsReply? reply = null;
            Exception? readError = null;

            async Task ReadAsync(long after)
            {
                for (var attempt = 0; ; attempt++)
                {
                    request = new PbClock.EventsRequest { Identity = identity, AfterCursor = after, Limit = config.PageLimit };
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

            var cursor = Cursor;
            await ReadAsync(Math.Max(cursor, 0)).ConfigureAwait(false);
            // The first page only finds the ring's newest cursor: the poll starts there and reads what follows it.
            if (readError is null && cursor < 0 && reply?.Outcome?.Page is { } first)
            {
                if (first.Context?.Identity is { } adopted)
                {
                    identity = adopted;
                }
                cursor = first.NewestCursor.GetValueOrDefault();
                Volatile.Write(ref _cursor, cursor);
                await ReadAsync(cursor).ConfigureAwait(false);
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
                // The loaded world is not the one authority was granted for: authority ends, the page is still read.
                ClockEvents.AuthorityLost("events poll context does not match the held authority",
                    ("held_known", state.ObservationKnown), ("held", state.Snapshot.ToString()), ("observed_generation", current.NativeGeneration), ("observed_tick", current.Tick));
                if (!TryDisableObserved(state))
                {
                    ClockEvents.Event("clock-scheduler", "authority", "authority changed under the poll");
                }
            }
            BridgeApi.ValidateClockEventsPage(page, request);
            call.ThrowIfCancellationRequested();
            var next = page.NextCursor.GetValueOrDefault();
            var captured = next != cursor;
            ClockPageWake wake = new([], false, false, null);
            IReadOnlyList<Facts.Section> sections = [];
            if (captured)
            {
                Volatile.Write(ref _cursor, next);
                Tel.ObserveTick(current.Tick);
                wake = Pkg.PageWake(page);
                sections = Pkg.ClockPageSections(page);
            }
            return new ClockPollResult
            {
                Captured = captured,
                Invalidated = wake.Families,
                InvalidatedSections = sections,
                AuthorityChanged = wake.Authority,
                Held = wake.Stopped,
                HeldAt = wake.StopAt,
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
}
