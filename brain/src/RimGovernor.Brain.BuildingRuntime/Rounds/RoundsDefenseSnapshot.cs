using System.Globalization;
using RimGovernor.Contracts.Wire;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Wraps the defense source and keeps the latest combat read for the snapshot recorder.</summary>
internal sealed partial class DefenseRecorder : IRoundsDefenseSource
{
    private readonly IRoundsDefenseSource _inner;

    public DefenseRecorder(IRoundsDefenseSource inner) => _inner = inner;

    public EmergencyObservation? Emergency { get; private set; }

    public O.ListPawnsReply? Pawns { get; private set; }

    public BridgeLinesOfFire? Lines { get; private set; }

    public M.CombatGeometry CombatGeometry(M.CombatGeometryRequest request, CancellationToken ct) =>
        _inner.CombatGeometry(request, ct);

    public async Task<BridgeDefenseSite> ReadDefenseSiteAsync(C.Identity identity, BridgeCellRect region, CancellationToken ct) =>
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
            Pawns = new O.ListPawnsReply { Outcome = detail };
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
        var dir = Environment.GetEnvironmentVariable(SnapshotRecorder.DirEnv) ?? "";
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
                SnapshotDefenseReplay.RecordDefense(dir, d);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DefensePipeline.DefenseSnapshotSkip("defense", "defense", ex, call);
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
            EmergencyContext = WireJson.Serialize(rec.Emergency.Context),
        };
        if (rec.Pawns is not null)
        {
            d.CombatPawns = WireJson.Serialize(rec.Pawns);
        }
        if (rec.Lines is not null)
        {
            d.Lines = rec.Lines.Lines;
            d.LinesContext = WireJson.Serialize(rec.Lines.Context);
        }
        var layout = p.Journal.LoadDefenseLayout(new StoreWorld { Colony = current.Colony, Load = current.Load, Map = current.Map }, ct);
        if (layout is not null)
        {
            d.Layout = layout;
            var recorded = p.Journal.LayoutPlan(current, tick, ct);
            if (recorded?.Plan is { } plan && RoundsDefensePlanner.CombatLayoutOf(plan).TryGetValue(out var combat))
            {
                d.Combat = combat;
            }
        }
        if (result.Plan.IsEmpty)
        {
            return d;
        }
        var review = p.Journal.LoadRounds(ct);
        if (review.Combat is null)
        {
            return d;
        }
        foreach (var m in p.Journal.LoadNeed(MilitaryFamily.Combat, review.Snapshot, ct).Methods)
        {
            if (m.Plan == result.Plan)
            {
                d.Method = m.Method;
            }
        }
        return d;
    }
}

partial class DefenseRecorder
{
    /// <summary>Records the layout request and record for the snapshot replay tests when the snapshot directory is set; a failed record only logs.</summary>
    internal static void RecordLayoutSnapshot(GenerationSnapshot current, Tick tick, SnapshotLayout l, CancellationToken ct)
    {
        var dir = Environment.GetEnvironmentVariable(SnapshotRecorder.DirEnv) ?? "";
        if (dir == "")
        {
            return;
        }
        l.Recorded = string.Create(CultureInfo.InvariantCulture, $"colony {current.Colony} load {current.Load} map {current.Map} tick {tick.Value}");
        l.Snapshot = current;
        l.Tick = tick;
        try
        {
            SnapshotLayoutReplay.RecordLayout(dir, l);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DefensePipeline.DefenseSnapshotSkip("defense-layout", "layout", ex, ct);
        }
    }
}
