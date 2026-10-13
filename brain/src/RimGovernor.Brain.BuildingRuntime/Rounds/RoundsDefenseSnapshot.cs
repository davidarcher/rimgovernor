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

    public (BridgeDefenseSite Site, BridgeResult Result) ReadDefenseSite(C.Identity identity, BridgeCellRect region, CancellationToken ct) =>
        _inner.ReadDefenseSite(identity, region, ct);

    public BridgeCombat ReadCombat(C.Identity identity, CancellationToken ct)
    {
        var v = _inner.ReadCombat(identity, ct);
        if (v.Emergency.Context is null)
        {
            return v;
        }
        Emergency = v.Emergency;
        if (v.Frame.Pawns is { } detail)
        {
            Pawns = new O.ListPawnsReply { Observed = detail };
        }
        if (v.Frame.CombatLinesOfFire is { } lines)
        {
            Lines = new BridgeLinesOfFire { Context = lines.Context, Lines = v.Lines };
        }
        return v;
    }
}

public sealed partial class RoundsDefensePlanner
{
    internal RoundsDefenseResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var dir = Environment.GetEnvironmentVariable(SnapshotApi.DirEnv) ?? "";
        if (dir == "")
        {
            return Decide(call, epoch, arbiter);
        }
        var recorder = new DefenseRecorder(_native);
        var planner = new RoundsDefensePlanner(_reviewer, recorder, _hands).WithCombatTuning(_tuning);
        var result = planner.Decide(call, epoch, arbiter);
        if (recorder.Emergency is not null)
        {
            try
            {
                var d = DefenseSnapshot(call, recorder, result);
                SnapshotApi.RecordDefense(dir, d);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DefenseSnapshotSkip(call, "defense", "defense", ex);
            }
        }
        return result;
    }

    internal SnapshotDefense DefenseSnapshot(CancellationToken ct, DefenseRecorder rec, RoundsDefenseResult result)
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
        var (layout, ok) = p.Journal.LoadDefenseLayout(new StoreWorld { Colony = current.Colony, Load = current.Load, Map = current.Map }, ct);
        if (ok)
        {
            d.Layout = layout;
            var (recorded, found) = p.Journal.LayoutPlan(current, tick, ct);
            var opening = PolicyApi.KillboxOpening(recorded.Plan);
            if (found && opening.Found)
            {
                d.Toward = opening.Opening.Toward;
            }
        }
        if (result.Plan.IsZero)
        {
            return d;
        }
        var review = p.Journal.LoadRounds(ct);
        var (incident, _, hit) = RoundsIncident(ct, p.Journal, review, PolicyApi.ActiveCombat);
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
    internal static void RecordLayoutSnapshot(CancellationToken ct, GenerationSnapshot current, Tick tick, SnapshotLayout l)
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
            DefenseSnapshotSkip(ct, "defense-layout", "layout", ex);
        }
    }
}
