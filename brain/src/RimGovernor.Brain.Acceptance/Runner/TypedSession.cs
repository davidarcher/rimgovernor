using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using RimGovernor.Contracts.Wire;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The typed session over the runner's <see cref="CaseSession"/>: what <see cref="TypedCase.ToCase"/> hands a typed body,
/// the opened, staged game behind typed calls. Contracts records go in and come out (no flat JSON view), waits spend the
/// case's declared ticks and end on a tick count, a probe's signature or a telemetry event (never on wall time), and
/// telemetry is selected by a registered kind. Every wait spends from the case's declared ticks, and a wait past them
/// fails before the game runs.
/// </summary>
public sealed class TypedSession
{
    private readonly CaseSession _s;
    private readonly TypedCase _case;
    private int _spent;

    // The game an owned body launched last (LaunchAsync), and how many it launched.
    private GameReuse? _launched;
    private int _launches;

    internal TypedSession(CaseSession session, TypedCase typed)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(typed);
        _s = session;
        _case = typed;

        // An owned body opens its own games: there is no identity until it starts or loads a colony.
        Identity = typed.Start is OwnedStart ? NoColony : Harness.IdentityOf(session.Identity);
        Scope = TypedReads.Scope(Identity);
        Staged = typed.Stage is null ? null : FixtureStage.Prepared(session.Prepared);
    }

    // A reload polls lifecycle_read_load at most this many times, this far apart.
    private const int LoadPolls = 300;
    private static readonly TimeSpan LoadPollInterval = TimeSpan.FromMilliseconds(200);

    // A new-colony phase (world or map generation) is one long event that reports nothing inside it, so its stall floor
    // is longer than the shared one.
    private static readonly TimeSpan NewColonyStall = TimeSpan.FromMinutes(3);

    // An owned body's identity before it starts or loads a colony.
    private static readonly Common.Identity NoColony = new() { ColonyId = "", LoadToken = "", MapId = 0 };

    // The harness every call goes to: the game an owned body launched (its open reuse case, else its session), else the
    // runner's.
    private Harness H => _launched is null
        ? _s.Harness
        : _launched.Live ?? throw new AcceptanceException($"case {_case.Name}: the launched game's session is released or retired");

    /// <summary>The loaded colony's identity.</summary>
    public Common.Identity Identity { get; private set; }

    /// <summary>A read scope pinned to <see cref="Identity"/>, for a request's <c>Scope</c>.</summary>
    public Observations.ReadScope Scope { get; private set; }

    /// <summary>The typed reply of the case's <see cref="TypedCase.Stage"/>, null when it declared none.</summary>
    public Fx.StageReply? Staged { get; }

    /// <summary>The declared ticks the waits have not spent yet.</summary>
    public int TicksLeft => _case.Ticks - _spent;

    /// <summary>Calls the production op <paramref name="op"/> with its request record and returns its reply record.</summary>
    public Task<TReply> CallAsync<TRequest, TReply>(string label, NativeOp<TRequest, TReply> op, TRequest request, CancellationToken cancellationToken)
        where TRequest : class
        where TReply : class =>
        H.CallAsync(label, op, request, cancellationToken);

    /// <summary>Calls the fixture op <paramref name="op"/>, which the case declares in <see cref="TypedCase.Ops"/>, and returns its typed reply.</summary>
    public Task<TReply> FixtureAsync<TRequest, TReply>(string label, FixtureOp<TRequest, TReply> op, TRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (!_case.Ops.Contains(op.Tool, StringComparer.Ordinal))
        {
            throw new AcceptanceException($"{label}: case {_case.Name} calls {op.Tool} without declaring it in Ops");
        }

        return op.CallAsync(H, label, request, cancellationToken);
    }

    /// <summary>Stages <paramref name="spec"/> on the running game (<c>test/stage</c>, declared in <see cref="TypedCase.Ops"/>) and returns its typed reply.</summary>
    public Task<Fx.StageReply> StageAsync(string label, Fx.StageSpec spec, CancellationToken cancellationToken) =>
        FixtureAsync(label, FixtureStage.Op, spec, cancellationToken);

    /// <summary>The <c>observations_list_buildings</c> rows <paramref name="request"/> selects; its Scope defaults to <see cref="Scope"/>.</summary>
    public Task<IReadOnlyList<Observations.BuildingState>> BuildingsAsync(string label, Observations.ListBuildingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return H.BuildingsAsync(label, request.Scope is null ? request with { Scope = Scope } : request, cancellationToken);
    }

    /// <summary>Every zone row <c>observations_list_zones</c> answers.</summary>
    public Task<IReadOnlyList<Observations.ZoneState>> ZonesAsync(string label, CancellationToken cancellationToken) =>
        H.ZonesAsync(label, Scope, cancellationToken);

    /// <summary>Each growing zone with its crop, cells and planted cells (the zones read joined to the cells read).</summary>
    public Task<IReadOnlyList<GrowingZone>> GrowingZonesAsync(string label, CancellationToken cancellationToken) =>
        H.GrowingZonesAsync(label, Identity, cancellationToken);

    /// <summary>Every row of the registered <paramref name="kind"/> the game's log ring holds, oldest first.</summary>
    public IReadOnlyList<LogRow> Telemetry(TelemetryKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return [.. ServiceLogs.ReadLogs(LogsPath()).Where(row => row.Kind == kind.Name)];
    }

    /// <summary>Runs the game <paramref name="ticks"/> ticks from <see cref="TicksLeft"/>, then pauses.</summary>
    public async Task RunTicksAsync(string label, int ticks, CancellationToken cancellationToken)
    {
        var target = await H.TickAsync(cancellationToken).ConfigureAwait(false) + (ulong)Math.Max(ticks, 0);
        _ = await RunAsync(
            label,
            ticks,
            async ct => (string.Empty, await H.TickAsync(ct).ConfigureAwait(false) >= target),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the game until <paramref name="probe"/> is done, at most <paramref name="ticks"/> ticks from <see cref="TicksLeft"/>,
    /// then pauses; a signature that stops changing stalls the wait. Returns the ticks it ran.
    /// </summary>
    public Task<int> RunUntilAsync(string label, int ticks, Probe probe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return RunAsync(label, ticks, probe, cancellationToken);
    }

    /// <summary>
    /// Runs the game until a row of <paramref name="kind"/> written after the call satisfies <paramref name="match"/>, at
    /// most <paramref name="ticks"/> ticks from <see cref="TicksLeft"/>, then pauses; returns the matching row.
    /// </summary>
    public async Task<LogRow> RunUntilEventAsync(string label, int ticks, TelemetryKind kind, Func<LogRow, bool> match, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(match);
        var tail = LogTail.Create(LogsPath());
        LogRow? found = null;
        var seen = 0;
        _ = await RunAsync(
            label,
            ticks,
            ct =>
            {
                foreach (var row in tail.Next())
                {
                    if (row.Kind == kind.Name)
                    {
                        seen++;
                        if (found is null && match(row))
                        {
                            found = row;
                        }
                    }
                }

                return Task.FromResult((string.Create(CultureInfo.InvariantCulture, $"{kind.Name}={seen}"), found is not null));
            },
            cancellationToken).ConfigureAwait(false);
        return found ?? throw new AcceptanceException($"{label}: no matching {kind.Name} row");
    }

    /// <summary>
    /// Runs the native clock under <paramref name="request"/> (its watch policy decides what holds it) until the clock
    /// holds itself, at most <paramref name="ticks"/> ticks from <see cref="TicksLeft"/>, beating while it runs; returns
    /// the held status. A wait that ends otherwise holds the clock before it throws.
    /// </summary>
    public Task<Clock.Status> RunClockUntilHeldAsync(string label, int ticks, Clock.RunRequest request, CancellationToken cancellationToken) =>
        RunClockUntilHeldAsync(label, ticks, request, static (_, _) => Task.FromResult(false), cancellationToken);

    /// <summary>
    /// <see cref="RunClockUntilHeldAsync(string, int, Clock.RunRequest, CancellationToken)"/> that calls
    /// <paramref name="whileRunning"/> once per round the clock still runs, before the heartbeat: the hook that acts
    /// mid-run (an injection at a tick) and can end the wait without a hold by returning true. Returns the held status, or
    /// the running status of the round that ended the wait (<see cref="Clock.ClockState.Running"/>); either way the clock
    /// is held when it returns.
    /// </summary>
    public async Task<Clock.Status> RunClockUntilHeldAsync(string label, int ticks, Clock.RunRequest request, ClockRound whileRunning, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(whileRunning);
        Budget(label, ticks);
        if (request.HoldAtTick is null || request.PlannedHoldSerial is null)
        {
            // Native requires both: the review tick is the budget's end (the wait's own ceiling), the hold serial the
            // one the clock holds under now, so a hold that entered between the read and the Run refuses it.
            var before = await CallAsync(label + "-before", NativeOps.ReadClockStatus, new Clock.StatusRequest { Identity = Identity }, cancellationToken).ConfigureAwait(false);
            var current = before.Outcome?.Status ?? throw new AcceptanceException($"{label}: no clock status before the run: {before.Outcome}");
            request = request with
            {
                HoldAtTick = request.HoldAtTick ?? (ulong)(Math.Max(current.Context?.Tick ?? 0, 0) + ticks),
                PlannedHoldSerial = request.PlannedHoldSerial ?? current.HoldSerial,
            };
        }
        var run = await CallAsync(label + "-run", NativeOps.RunClock, request with { Identity = Identity }, cancellationToken).ConfigureAwait(false);
        var status = run.Outcome?.Status is { State: Clock.ClockState.Running } started
            ? started
            : throw new AcceptanceException($"{label}: clock run refused or left held: {run.Outcome}");
        try
        {
            // Each round reads the status (the wait's tick), then beats while it still runs: the native ceiling drops to
            // Normal after five seconds without contact.
            var ran = await ServedWait.UntilAsync(
                label,
                ticks,
                async ct =>
                {
                    var reply = await CallAsync(label + "-status", NativeOps.ReadClockStatus, new Clock.StatusRequest { Identity = Identity }, ct).ConfigureAwait(false);
                    status = reply.Outcome?.Status ?? throw new AcceptanceException($"{label}: no clock status: {reply.Outcome}");
                    return (ulong)Math.Max(status.Context?.Tick ?? 0, 0);
                },
                async ct =>
                {
                    if (status.State == Clock.ClockState.Held || await whileRunning(status, ct).ConfigureAwait(false))
                    {
                        return (string.Empty, true);
                    }

                    var beat = await CallAsync(label + "-heartbeat", NativeOps.HeartbeatClock, new Clock.HeartbeatRequest { Identity = Identity }, ct).ConfigureAwait(false);
                    _ = beat.Outcome?.Ack ?? throw new AcceptanceException($"{label}: heartbeat refused: {beat.Outcome}");
                    return (Wait.Signature(status.Context?.Tick, status.State), false);
                },
                new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromMilliseconds(250) },
                cancellationToken).ConfigureAwait(false);
            _spent += ran;
            return status;
        }
        finally
        {
            if (status.State != Clock.ClockState.Held)
            {
                // Native keeps the first reason, so a hold that raced this one is not overwritten.
                _ = await CallAsync(
                    label + "-hold",
                    NativeOps.HoldClock,
                    new Clock.HoldRequest { Identity = Identity, Reason = Clock.HoldReason.Review, Detail = label + ": the wait ended before a hold" },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The <c>clock_hold</c> row of the hold <paramref name="held"/> reports (a held status, as
    /// <see cref="RunClockUntilHeldAsync"/> returns): the row whose <c>stop_at_ms</c> is its <c>HeldAtUnixMs</c>. The
    /// game is held, so the wait spends no ticks; it ends when the row reaches the log ring, under the hang guard.
    /// </summary>
    public async Task<LogRow> HoldRowAsync(string label, Clock.Status held, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(held);
        var heldAt = held.HeldAtUnixMs ?? throw new AcceptanceException($"{label}: the status is not held: {held}");
        var deadline = DateTime.UtcNow + HoldRowWait;
        using var pace = Wait.Pace(TimeSpan.FromMilliseconds(100));
        while (true)
        {
            if (Telemetry(TelemetryKinds.ClockHold).LastOrDefault(row => ClockRows.Whole(row, ColonyEventTelemetry.StopAtMsKey) == heldAt) is { } row)
            {
                return row;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AcceptanceException($"{label}: no clock_hold row with stop_at_ms {heldAt} within {HoldRowWait} ({held.Reason}: {held.Detail})");
            }

            await pace.NextAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The native clock's acceptance-only accounting now (<see cref="ClockAccounting"/>): the supervisor's declared hazard
    /// bounds beside the widest gap it observed. A read; it spends no ticks.
    /// </summary>
    public Task<ClockAccounting> ClockAccountingAsync(string label, CancellationToken cancellationToken) =>
        ClockAccounting.ReadAsync(H, label, cancellationToken);

    // The game is held while a hold row is awaited: only the log sink's batch flush stands between the hold and its row.
    private static readonly TimeSpan HoldRowWait = TimeSpan.FromSeconds(30);

    /// <summary>Records <paramref name="value"/> in the report under <paramref name="key"/>: the evidence a reviewer reads.</summary>
    public void Record(string key, JsonNode? value) => _s.Report[key] = value;

    /// <summary>Records the Contracts record <paramref name="value"/> in the report under <paramref name="key"/>.</summary>
    public void RecordWire<T>(string key, T value) => _s.Report[key] = JsonNode.Parse(WireJson.Serialize(value));

    /// <summary>
    /// Records the Contracts records <paramref name="rows"/> as a JSON array under <paramref name="key"/>. The wire context
    /// registers element records, not the list or array types a case collects them in.
    /// </summary>
    public void RecordWireRows<T>(string key, IEnumerable<T> rows) =>
        _s.Report[key] = new JsonArray([.. rows.Select(row => JsonNode.Parse(WireJson.Serialize(row)))]);

    /// <summary>Records the Fixtures.Contracts reply <paramref name="value"/> of a <c>test/*</c> op under <paramref name="key"/>.</summary>
    public void RecordFixture<T>(string key, T value) => _s.Report[key] = JsonNode.Parse(Fx.FixtureJson.Serialize(value));

    /// <summary>
    /// Hands the clock to the case's declared <see cref="TypedCase.Serve"/>: starts the brain service, resumes automatic
    /// control under a kept authority and observes while the brain drives the game until <paramref name="probe"/> is done,
    /// at most <paramref name="ticks"/> game ticks from <see cref="TicksLeft"/>; then stops the service and parks the brain.
    /// The service's exit or a signature that stops changing ends the wait, and the hang guard is its wall-clock backstop.
    /// Returns the ticks the game advanced.
    /// </summary>
    public async Task<int> ServeUntilAsync(string label, int ticks, Probe probe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var spec = _case.Serve ?? throw new AcceptanceException($"{label}: case {_case.Name} serves without declaring Serve");
        Budget(label, ticks);
        var service = await _s.ServeAsync(spec, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!spec.Resume)
            {
                _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            }

            service.KeepAuthority(cancellationToken);
            var ran = await ServedWait.UntilAsync(
                label,
                ticks,
                H.TickAsync,
                probe,
                new Wait { Stall = Wait.StallBudget(), Terminal = service.Exited },
                cancellationToken).ConfigureAwait(false);
            _spent += ran;
            return ran;
        }
        finally
        {
            // The in-mod brain outlives the handle: park it so the body's later reads see a paused game.
            _ = await service.StopAsync().ConfigureAwait(false);
            try
            {
                _s.Report[service.Label + "_park"] = await service.ParkAsync(service.Label, service.Identity!, service.Token, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                _s.Report[service.Label + "_park_error"] = e.Message;
            }
        }
    }

    /// <summary>
    /// The review the brain last persisted in the run's durable journal (its world, cursor tick, enabled flag and
    /// progress records), null while none was persisted: what a served case's probe reads of the brain's own state.
    /// Spends no ticks.
    /// </summary>
    public async Task<Store.Rounds?> ReviewAsync(CancellationToken cancellationToken)
    {
        using var journal = await ServiceJournal.OpenWithRetryAsync(_s.Config, cancellationToken).ConfigureAwait(false);
        return await ServiceJournal.TryRoundsAsync(journal, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every need_transitions row the run's durable journal holds, oldest first. Spends no ticks.</summary>
    public async Task<IReadOnlyList<Store.NeedTransition>> NeedTransitionsAsync(CancellationToken cancellationToken)
    {
        using var journal = await ServiceJournal.OpenWithRetryAsync(_s.Config, cancellationToken).ConfigureAwait(false);
        return await journal.NeedTransitionsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A real reload: saves the paused game under a fresh name (<c>lifecycle_save</c>) and loads that save back to map
    /// readiness (<c>lifecycle_load</c>, polled through <c>lifecycle_read_load</c>). <see cref="Identity"/> and
    /// <see cref="Scope"/> follow the loaded game. Spends no ticks.
    /// </summary>
    public async Task ReloadAsync(string label, CancellationToken cancellationToken)
    {
        var name = label + "-" + Guid.NewGuid().ToString("N");
        var player = new Lifecycle.PlayerLifecycleContext { Identity = Identity, PlayerDirection = 1, RequestId = name };
        var saved = await CallAsync(label + "-save", NativeOps.SaveGame, new Lifecycle.SaveRequest { Player = player, SaveName = name }, cancellationToken).ConfigureAwait(false);
        if (saved.Outcome?.Completed is null)
        {
            throw new AcceptanceException($"{label}: lifecycle_save did not complete: {saved.Outcome}");
        }

        var loadId = name + "-load";
        var reply = await CallAsync(
            label + "-load",
            NativeOps.StartLoad,
            new Lifecycle.LoadRequest
            {
                RequestId = loadId,
                SaveName = name,
                Readiness = Lifecycle.Readiness.Map,
                ExpectedPlayer = player with { RequestId = loadId },
            },
            cancellationToken).ConfigureAwait(false);
        using var pace = Wait.Pace(LoadPollInterval);
        for (var poll = 0; reply.Outcome?.Completed is null; poll++)
        {
            if (reply.Outcome?.Pending is null || poll >= LoadPolls)
            {
                throw new AcceptanceException($"{label}: lifecycle_load did not complete: {reply.Outcome}");
            }

            await pace.NextAsync(cancellationToken).ConfigureAwait(false);
            reply = await CallAsync(
                string.Create(CultureInfo.InvariantCulture, $"{label}-load-{poll}"),
                NativeOps.PollLoad,
                new Lifecycle.RequestStatus { RequestId = loadId },
                cancellationToken).ConfigureAwait(false);
        }

        Identity = reply.Outcome.Completed.Loaded?.Context?.Identity
            ?? throw new AcceptanceException($"{label}: the completed load names no identity: {reply.Outcome}");
        Scope = TypedReads.Scope(Identity);
    }

    /// <summary>
    /// <see cref="OwnedStart"/> only (<see cref="TypedCase.ClaimsStart"/>): launches a fresh game at the main menu on the
    /// case's prepared profile, at most <see cref="StartClaim.Launches"/> times, and returns its reusable-game lifecycle.
    /// Until the next launch every call, read and wait of this session goes to that game (to its open reuse case while
    /// one is open). The case retires it.
    /// </summary>
    public async Task<GameReuse> LaunchAsync(string label, CancellationToken cancellationToken)
    {
        if (_case.Start is not OwnedStart || _case.ClaimsStart is not { } claim)
        {
            throw new AcceptanceException($"{label}: case {_case.Name} launches a game without an owned start it claims");
        }

        if (_launches >= claim.Launches)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: case {_case.Name} declares {claim.Launches} launches and has used them"));
        }

        _launches++;
        GameReuse reuse;
        try
        {
            reuse = await GameReuse.OpenReusableGameAsync(_s.Config, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: {e.Message}", e);
        }

        _launched = reuse;
        Identity = NoColony;
        Scope = TypedReads.Scope(Identity);
        return reuse;
    }

    /// <summary>
    /// Starts a colony from the launched game's main menu (<c>lifecycle_new_colony</c>) and polls it
    /// (<c>lifecycle_read_new_colony</c>) to its terminal outcome. Each phase is one long event outside the tick loop, so
    /// the wait ends on the outcome or a stalled phase, not on ticks, and spends none. Phases must not go backwards, and a
    /// completed start must have reported SAVING. A completed start moves <see cref="Identity"/> and <see cref="Scope"/>
    /// to the new colony.
    /// </summary>
    public async Task<NewColonyResult> NewColonyAsync(string label, Lifecycle.NewColonyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = await CallAsync(label + "-start", NativeOps.StartNewColony, request, cancellationToken).ConfigureAwait(false);
        return await FollowNewColonyAsync(label, request, reply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="NewColonyAsync"/> started through the in-process brain's player route (<c>POST /api/lifecycle/new</c>,
    /// <see cref="Harness.StartNewColonyThroughBrainAsync"/>), for a spec that asks the brain to resolve a part before
    /// native creation (<c>governorIdeoligion</c>); the native poll follows it from its first reply.
    /// </summary>
    public async Task<NewColonyResult> NewColonyThroughBrainAsync(string label, Lifecycle.NewColonyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await H.StartNewColonyThroughBrainAsync(label + "-start", request, Wait.StallBudget(), cancellationToken).ConfigureAwait(false);
        return await FollowNewColonyAsync(label, request, null, cancellationToken).ConfigureAwait(false);
    }

    // Polls a started request to its terminal outcome; a null start reply is polled at once.
    private async Task<NewColonyResult> FollowNewColonyAsync(string label, Lifecycle.NewColonyRequest request, Lifecycle.NewColonyReply? started, CancellationToken cancellationToken)
    {
        var reply = started;
        var phases = new List<Lifecycle.NewColonyPhase>();
        var polls = 0;
        var stall = Wait.StallBudget();
        try
        {
            await new Wait { Stall = stall > NewColonyStall ? stall : NewColonyStall, Interval = TimeSpan.FromMilliseconds(250) }.WaitProgressAsync(
                async ct =>
                {
                    if (polls++ > 0 || reply is null)
                    {
                        reply = await CallAsync(
                            string.Create(CultureInfo.InvariantCulture, $"{label}-poll-{polls}"),
                            NativeOps.PollNewColony,
                            new Lifecycle.RequestStatus { RequestId = request.RequestId },
                            ct).ConfigureAwait(false);
                    }

                    if (reply.Outcome?.Pending is not { } pending)
                    {
                        return (string.Empty, true);
                    }

                    var phase = pending.Phase ?? Lifecycle.NewColonyPhase.Unspecified;
                    if (phases.Count > 0 && phase < phases[^1])
                    {
                        throw new AcceptanceException($"{label}: phase went backwards to {phase} after {phases[^1]}");
                    }

                    if (phases.Count == 0 || phase != phases[^1])
                    {
                        phases.Add(phase);
                    }

                    return (Wait.Signature(phase.ToString(), pending.Detail, pending.RerollCount), false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"{label}: the start stalled in {(phases.Count == 0 ? "no phase" : phases[^1])}: {e.Message}", e);
        }

        switch (reply?.Outcome)
        {
            case Lifecycle.NewColonyReplyOutcome.CompletedCase { Value: var completed }:
                if (!phases.Contains(Lifecycle.NewColonyPhase.Saving))
                {
                    throw new AcceptanceException($"{label}: completed without reporting SAVING (phases [{string.Join(' ', phases)}])");
                }

                Identity = completed.Context?.Identity ?? throw new AcceptanceException($"{label}: the completed start names no identity: {completed}");
                Scope = TypedReads.Scope(Identity);
                return new NewColonyResult { Phases = phases, Completed = completed };
            case Lifecycle.NewColonyReplyOutcome.FailureCase { Value: var failure }:
                return new NewColonyResult { Phases = phases, Failure = failure };
            default:
                throw new AcceptanceException($"{label}: expected a completed or failed start, got {reply?.Outcome}");
        }
    }

    /// <summary>The profile path of save <c>&lt;saveName&gt;.rws</c>: where a start claim checks the file a start wrote, or squats it.</summary>
    public string ProfileSave(string saveName) => _s.Config.ProfileSave(saveName);

    /// <summary>
    /// The profile save <c>&lt;saveName&gt;.rws</c> parsed as XML: the evidence a start claim reads for what no
    /// observation reports (a scenario part, the saved storyteller). Spends no ticks.
    /// </summary>
    public async Task<XDocument> ReadSaveFileAsync(string saveName, CancellationToken cancellationToken)
    {
        var path = ProfileSave(saveName);
        try
        {
            await using var file = File.OpenRead(path);
            return await XDocument.LoadAsync(file, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new AcceptanceException($"read save {path}: {e.Message}", e);
        }
    }

    // A wait must fit what is left of the declared ticks; it fails before the game runs otherwise.
    private void Budget(string label, int ticks)
    {
        if (ticks <= 0 || ticks > TicksLeft)
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"{label}: a wait of {ticks} ticks with {TicksLeft} of the case's {_case.Ticks} left"));
        }
    }

    // One wait over the shared run loop: the game runs until the probe is done, at most ticks of the declared budget.
    private async Task<int> RunAsync(string label, int ticks, Probe probe, CancellationToken cancellationToken)
    {
        Budget(label, ticks);
        var ran = await RunAt.RunUntilAsync(
            H,
            label,
            (ulong)ticks,
            new Wait { Stall = Wait.StallBudget() },
            probe,
            quiet: true,
            _s.Report,
            cancellationToken).ConfigureAwait(false);
        var spent = (int)Math.Min(ran, (ulong)ticks);
        _spent += spent;
        return spent;
    }

    // The game's log ring: the channel directory the opened game writes its OTLP rings to.
    private string LogsPath()
    {
        var dir = H.Transport is HttpNativeTransport http
            ? http.EndpointDirectory
            : GameSessions.EndpointDirectory(_s.Config.Configuration, _s.Config.GameId);
        return OtlpFiles.RingPath(dir, OtlpSignal.Logs);
    }
}
