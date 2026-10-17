using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using RimGovernor.Contracts.Wire;
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

    public TypedSession(ISession session, TypedCase typed)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(typed);
        _s = session;
        _case = typed;
        Identity = TypedReads.Identity(session.Identity);
        Scope = TypedReads.Scope(Identity);
        Staged = typed.Stage is null ? null : FixtureStage.Prepared(session);
    }

    // A reload polls lifecycle_read_load at most this many times, this far apart.
    private const int LoadPolls = 300;
    private static readonly TimeSpan LoadPollInterval = TimeSpan.FromMilliseconds(200);

    public Common.Identity Identity { get; private set; }

    public Observations.ReadScope Scope { get; private set; }

    public Fx.StageReply? Staged { get; }

    public int TicksLeft => _case.Ticks - _spent;

    public Task<TReply> CallAsync<TRequest, TReply>(string label, NativeOp<TRequest, TReply> op, TRequest request, CancellationToken cancellationToken)
        where TRequest : class
        where TReply : class =>
        _s.Harness.CallAsync(label, op, request, cancellationToken);

    public Task<TReply> FixtureAsync<TRequest, TReply>(string label, FixtureOp<TRequest, TReply> op, TRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (!_case.Ops.Contains(op.Tool, StringComparer.Ordinal))
        {
            throw new AcceptanceException($"{label}: case {_case.Name} calls {op.Tool} without declaring it in Ops");
        }

        return op.CallAsync(_s.Harness, label, request, cancellationToken);
    }

    public Task<Fx.StageReply> StageAsync(string label, Fx.StageSpec spec, CancellationToken cancellationToken) =>
        FixtureAsync(label, FixtureStage.Op, spec, cancellationToken);

    public Task<IReadOnlyList<Observations.BuildingState>> BuildingsAsync(string label, Observations.ListBuildingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _s.Harness.BuildingsAsync(label, request.Scope is null ? request with { Scope = Scope } : request, cancellationToken);
    }

    public Task<IReadOnlyList<Observations.ZoneState>> ZonesAsync(string label, CancellationToken cancellationToken) =>
        _s.Harness.ZonesAsync(label, Scope, cancellationToken);

    public Task<IReadOnlyList<GrowingZone>> GrowingZonesAsync(string label, CancellationToken cancellationToken) =>
        _s.Harness.GrowingZonesAsync(label, _s.Identity, cancellationToken);

    public IReadOnlyList<LogRow> Telemetry(TelemetryKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return [.. ServiceLogs.ReadLogs(LogsPath()).Where(row => row.Kind == kind.Name)];
    }

    public async Task RunTicksAsync(string label, int ticks, CancellationToken cancellationToken)
    {
        var target = await _s.Harness.TickAsync(cancellationToken).ConfigureAwait(false) + (ulong)Math.Max(ticks, 0);
        _ = await RunAsync(
            label,
            ticks,
            async ct => (string.Empty, await _s.Harness.TickAsync(ct).ConfigureAwait(false) >= target),
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
                _s.Harness.TickAsync,
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
            _s.Harness,
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
        var dir = _s.Harness.Transport is HttpNativeTransport http
            ? http.EndpointDirectory
            : GameSessions.EndpointDirectory(_s.Config.Configuration, _s.Config.GameId);
        return OtlpFiles.RingPath(dir, OtlpSignal.Logs);
    }
}
