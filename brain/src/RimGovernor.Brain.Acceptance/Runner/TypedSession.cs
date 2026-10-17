using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using RimGovernor.Brain.NativeFacade;
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
/// The typed session over the runner's <see cref="ISession"/>: what <see cref="TypedCase.ToCase"/> hands a typed body.
/// Every wait spends from the case's declared ticks, and a wait past them fails before the game runs.
/// </summary>
public sealed class TypedSession : ITypedSession
{
    private readonly ISession _s;
    private readonly TypedCase _case;
    private int _spent;

    // The game an owned body launched last (LaunchAsync), and how many it launched.
    private GameReuse? _launched;
    private int _launches;

    public TypedSession(ISession session, TypedCase typed)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(typed);
        _s = session;
        _case = typed;

        // An owned body opens its own games: there is no identity until it starts or loads a colony.
        Identity = typed.Start is OwnedStart ? NoColony : TypedReads.Identity(session.Identity);
        Scope = TypedReads.Scope(Identity);
        Staged = typed.Stage is null ? null : FixtureStage.Prepared(session);
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

    public Common.Identity Identity { get; private set; }

    public Observations.ReadScope Scope { get; private set; }

    public Fx.StageReply? Staged { get; }

    public int TicksLeft => _case.Ticks - _spent;

    public Task<TReply> CallAsync<TRequest, TReply>(string label, NativeOp<TRequest, TReply> op, TRequest request, CancellationToken cancellationToken)
        where TRequest : class
        where TReply : class =>
        H.CallAsync(label, op, request, cancellationToken);

    public Task<TReply> FixtureAsync<TRequest, TReply>(string label, FixtureOp<TRequest, TReply> op, TRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (!_case.Ops.Contains(op.Tool, StringComparer.Ordinal))
        {
            throw new AcceptanceException($"{label}: case {_case.Name} calls {op.Tool} without declaring it in Ops");
        }

        return op.CallAsync(H, label, request, cancellationToken);
    }

    public Task<Fx.StageReply> StageAsync(string label, Fx.StageSpec spec, CancellationToken cancellationToken) =>
        FixtureAsync(label, FixtureStage.Op, spec, cancellationToken);

    public Task<IReadOnlyList<Observations.BuildingState>> BuildingsAsync(string label, Observations.ListBuildingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return H.BuildingsAsync(label, request.Scope is null ? request with { Scope = Scope } : request, cancellationToken);
    }

    public Task<IReadOnlyList<Observations.ZoneState>> ZonesAsync(string label, CancellationToken cancellationToken) =>
        H.ZonesAsync(label, Scope, cancellationToken);

    public Task<IReadOnlyList<GrowingZone>> GrowingZonesAsync(string label, CancellationToken cancellationToken) =>
        H.GrowingZonesAsync(label, _s.Identity, cancellationToken);

    public IReadOnlyList<LogRow> Telemetry(TelemetryKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return [.. ServiceLogs.ReadLogs(LogsPath()).Where(row => row.Kind == kind.Name)];
    }

    public async Task RunTicksAsync(string label, int ticks, CancellationToken cancellationToken)
    {
        var target = await H.TickAsync(cancellationToken).ConfigureAwait(false) + (ulong)Math.Max(ticks, 0);
        _ = await RunAsync(
            label,
            ticks,
            async ct => (string.Empty, await H.TickAsync(ct).ConfigureAwait(false) >= target),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<int> RunUntilAsync(string label, int ticks, Probe probe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return RunAsync(label, ticks, probe, cancellationToken);
    }

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

    public async Task<Clock.Status> RunClockUntilHeldAsync(string label, int ticks, Clock.RunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Budget(label, ticks);
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
                    if (status.State == Clock.ClockState.Held)
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

    public async Task<LogRow> HoldRowAsync(string label, Clock.Status held, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(held);
        var heldAt = held.HeldAtUnixMs ?? throw new AcceptanceException($"{label}: the status is not held: {held}");
        var deadline = DateTime.UtcNow + HoldRowWait;
        while (true)
        {
            if (Telemetry(TelemetryKinds.ClockHold).LastOrDefault(row => ClockRows.Whole(row, ClockEventTelemetry.StopAtMsKey) == heldAt) is { } row)
            {
                return row;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AcceptanceException($"{label}: no clock_hold row with stop_at_ms {heldAt} within {HoldRowWait} ({held.Reason}: {held.Detail})");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    // The game is held while a hold row is awaited: only the log sink's batch flush stands between the hold and its row.
    private static readonly TimeSpan HoldRowWait = TimeSpan.FromSeconds(30);

    public void Record(string key, JsonNode? value) => _s.Report[key] = value;

    public void RecordWire<T>(string key, T value) => _s.Report[key] = JsonNode.Parse(WireJson.Serialize(value));

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

    public async Task<Store.Rounds?> ReviewAsync(CancellationToken cancellationToken)
    {
        using var journal = await ServiceJournal.OpenWithRetryAsync(_s.Config, cancellationToken).ConfigureAwait(false);
        return await ServiceJournal.TryRoundsAsync(journal, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Store.NeedTransition>> NeedTransitionsAsync(CancellationToken cancellationToken)
    {
        using var journal = await ServiceJournal.OpenWithRetryAsync(_s.Config, cancellationToken).ConfigureAwait(false);
        return await journal.NeedTransitionsAsync(cancellationToken).ConfigureAwait(false);
    }

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
                PlayerDirection = 1,
                ExpectedPlayer = player with { RequestId = loadId },
            },
            cancellationToken).ConfigureAwait(false);
        for (var poll = 0; reply.Outcome?.Completed is null; poll++)
        {
            if (reply.Outcome?.Pending is null || poll >= LoadPolls)
            {
                throw new AcceptanceException($"{label}: lifecycle_load did not complete: {reply.Outcome}");
            }

            await Task.Delay(LoadPollInterval, cancellationToken).ConfigureAwait(false);
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

    public async Task<NewColonyResult> NewColonyAsync(string label, Lifecycle.NewColonyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = await CallAsync(label + "-start", NativeOps.StartNewColony, request, cancellationToken).ConfigureAwait(false);
        var phases = new List<Lifecycle.NewColonyPhase>();
        var polls = 0;
        var stall = Wait.StallBudget();
        try
        {
            await new Wait { Stall = stall > NewColonyStall ? stall : NewColonyStall, Interval = TimeSpan.FromMilliseconds(250) }.WaitProgressAsync(
                async ct =>
                {
                    if (polls++ > 0)
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

        switch (reply.Outcome)
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
                throw new AcceptanceException($"{label}: expected a completed or failed start, got {reply.Outcome}");
        }
    }

    public string ProfileSave(string saveName) => _s.Config.ProfileSave(saveName);

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
