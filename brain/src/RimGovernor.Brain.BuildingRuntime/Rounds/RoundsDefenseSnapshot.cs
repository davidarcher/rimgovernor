using System.Globalization;
using Google.Protobuf;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Wraps the defense source and keeps the latest combat read for the snapshot recorder.</summary>
internal sealed class DefenseRecorder : IRoundsDefenseSource
{
    private readonly IRoundsDefenseSource _inner;

    public DefenseRecorder(IRoundsDefenseSource inner) => _inner = inner;

    public EmergencyObservation? Emergency { get; private set; }

    public O.ListPawnsReply? Pawns { get; private set; }

    public BridgeLinesOfFire? Lines { get; private set; }

    public (M.CombatGeometry Geometry, BridgeResult Result) CombatGeometry(M.CombatGeometryRequest request, CancellationToken ct) =>
        _inner.CombatGeometry(request, ct);

    public async Task<(BridgeDefenseSite Site, BridgeResult Result)> ReadDefenseSiteAsync(C.Identity identity, BridgeCellRect region, CancellationToken ct) =>
        await _inner.ReadDefenseSiteAsync(identity, region, ct).ConfigureAwait(false);

    public async Task<BridgeCombat> ReadCombatAsync(C.Identity identity, CancellationToken ct)
    {
        var v = await _inner.ReadCombatAsync(identity, ct).ConfigureAwait(false);
        if (v.Emergency.Context is null)
        {
            return v;
        }
        Emergency = v.Emergency;
        if (v.Frame?.Pawns is { } detail)
        {
            Pawns = new O.ListPawnsReply { Observed = detail };
        }
        if (v.Frame?.CombatLinesOfFire is { } lines)
        {
            Lines = new BridgeLinesOfFire(lines.Context, v.Lines, false);
        }
        return v;
    }
}

public sealed partial class RoundsDefensePlanner
{
    internal async Task<RoundsDefenseResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var dir = Environment.GetEnvironmentVariable(SnapshotApi.DirEnv) ?? "";
        if (dir == "")
        {
            return await DecideAsync(arbiter, call, epoch).ConfigureAwait(false);
        }
        var recorder = new DefenseRecorder(_native);
        var planner = new RoundsDefensePlanner(_reviewer, recorder, _hands).WithCombatTuning(_tuning);
        var result = await planner.DecideAsync(arbiter, call, epoch).ConfigureAwait(false);
        if (recorder.Emergency is not null)
        {
            try
            {
                var d = DefenseSnapshot(recorder, result, call);
                SnapshotApi.RecordDefense(dir, d);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DefenseSnapshotSkip("defense", "defense", ex, call);
            }
        }
        return result;
    }

    internal SnapshotDefense DefenseSnapshot(DefenseRecorder rec, RoundsDefenseResult result, CancellationToken ct)
    {
        var p = _reviewer.Player;
        var current = p.Session.State().Snapshot;
        var tick = new Tick(rec.Emergency!.Context!.Tick);
        var d = new SnapshotDefense
        {
            Recorded = string.Create(CultureInfo.InvariantCulture, $"colony {current.Colony} load {current.Load} map {current.Map} tick {tick.Value}"),
            Snapshot = current,
            Tick = tick,
            Emergency = rec.Emergency.Facts,
            Reason = result.Verdict.ToString(),
            Plan = result.Plan,
            EmergencyContext = JsonFormatter.Default.Format(rec.Emergency.Context),
        };
        if (rec.Pawns is not null)
        {
            d.CombatPawns = JsonFormatter.Default.Format(rec.Pawns);
        }
        if (rec.Lines is not null)
        {
            d.Lines = rec.Lines.Lines;
            d.LinesContext = JsonFormatter.Default.Format(rec.Lines.Context);
        }
        var layout = p.Journal.LoadDefenseLayout(new StoreWorld { Colony = current.Colony, Load = current.Load, Map = current.Map }, ct);
        if (layout is not null)
        {
            d.Layout = layout;
            var recorded = p.Journal.LayoutPlan(current, tick, ct);
            if (recorded?.Plan is { } plan)
            {
                var (killbox, opening) = PolicyApi.KillboxOpening(plan);
                if (opening)
                {
                    d.Toward = killbox.Toward;
                }
            }
        }
        if (result.Plan.IsEmpty)
        {
            return d;
        }
        var review = p.Journal.LoadRounds(ct);
        var (incident, _, hit) = RoundsIncident(p.Journal, review, PolicyApi.ActiveCombat, ct);
        if (!hit)
        {
            return d;
        }
        foreach (var m in incident!.Methods)
        {
            if (m.Plan == result.Plan)
            {
                d.Method = m.Method;
            }
        }
        return d;
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Records the layout request and record for the snapshot replay tests when the snapshot directory is set; a failed record only logs.</summary>
    internal static void RecordLayoutSnapshot(GenerationSnapshot current, Tick tick, SnapshotLayout l, CancellationToken ct)
    {
        var dir = Environment.GetEnvironmentVariable(SnapshotApi.DirEnv) ?? "";
        if (dir == "")
        {
            return;
        }
        l.Recorded = string.Create(CultureInfo.InvariantCulture, $"colony {current.Colony} load {current.Load} map {current.Map} tick {tick.Value}");
        l.Snapshot = current;
        l.Tick = tick;
        try
        {
            SnapshotApi.RecordLayout(dir, l);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DefenseSnapshotSkip("defense-layout", "layout", ex, ct);
        }
    }
}
