using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// A layout replan running off the review's hold path (#3565): what the hourly check read when it started it and
/// the task the chain and the tiler run on. A later review adopts its result (the rows, the recorded plan) when
/// the world and the saved plan are still the ones it started from.
/// </summary>
internal sealed record PendingLayoutReplan(
    GenerationSnapshot Snapshot,
    Tick Started,
    HeldLayout Layout,
    ColonyProjection Projection,
    MapSurvey Survey,
    DefinitionCatalog Catalog,
    RoomGrowth Growth,
    int Colonists,
    string Grown,
    string Reason,
    IReadOnlyList<PowerGeyser> Geysers,
    PerimeterWall Wall,
    IReadOnlyList<string> Drift,
    Task<LayoutReplanRun> Work);

/// <summary>What a replan's run produced: the chain's row, advice and facts, then the tiled plan or the tiler's error.</summary>
internal sealed record LayoutReplanRun
{
    public required Decision StationRow { get; init; }

    public StationAdvice Advice { get; init; } = new();

    /// <summary>Whether the chain made advice (the layout_stations row is advised).</summary>
    public bool Advised { get; init; }

    public StationFacts Facts { get; init; } = new();

    /// <summary>The tiled plan, null when the chain made no advice or the tiler failed (<see cref="Error"/>).</summary>
    public LayoutPlan? Next { get; init; }

    /// <summary>The advice with its tiling, when the tiler succeeded.</summary>
    public StationAdvice? Tiled { get; init; }

    public Exception? Error { get; init; }

    public TimeSpan ChainDur { get; init; }

    public TimeSpan TileDur { get; init; }
}

public sealed partial class Rounder
{
    /// <summary>
    /// Starts the replan of the saved plan off the hold path: the chain and the tiler run on a worker over what
    /// the hourly check read (<c>RunLayoutReplan</c>); the review that finds the run complete adopts it
    /// (<c>AdoptLayoutReplanAsync</c>).
    /// </summary>
    internal static PendingLayoutReplan StartLayoutReplan(GenerationSnapshot snapshot, Tick tick, HeldLayout layout, ColonyProjection projection, MapSurvey survey, DefinitionCatalog catalog, RoomGrowth growth, StationFacts facts, int colonists, string grown, string reason, IReadOnlyList<PowerGeyser> geysers, PerimeterWall wall, IReadOnlyList<string> drift)
    {
        var plan = layout.Plan;
        var work = Task.Run(() => RunLayoutReplan(plan, facts, survey, catalog, geysers, growth));
        return new PendingLayoutReplan(snapshot, tick, layout, projection, survey, catalog, growth, colonists, grown, reason, geysers, wall, drift, work);
    }

    /// <summary>
    /// The replan's work, pure: the catalog rows onto the facts, the replan's facts over the saved plan
    /// (<c>ReplanFacts</c>), the chain over them once (the layout_stations row), then the tiler over its advice
    /// (<c>TileReplan</c>). Every failure is returned in the run, none thrown.
    /// </summary>
    internal static LayoutReplanRun RunLayoutReplan(LayoutPlan plan, StationFacts f, MapSurvey survey, DefinitionCatalog catalog, IReadOnlyList<PowerGeyser> geysers, RoomGrowth growth)
    {
        var start = DateTimeOffset.UtcNow;
        var facts = f;
        var prev = plan;
        Exception? error = null;
        try
        {
            facts = StationFactsBuilder.StationFacts(catalog, f);
            (prev, facts) = LayoutPlan.ReplanFacts(plan, facts, survey, geysers, growth);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = e;
        }
        var (row, advice, ok) = StationAdviceLog.StationDecision(facts, error);
        var chainDur = DateTimeOffset.UtcNow - start;
        var run = new LayoutReplanRun { StationRow = row with { Dur = chainDur }, Advice = advice, Advised = ok, Facts = facts, ChainDur = chainDur };
        if (!ok)
        {
            return run;
        }
        var tileStart = DateTimeOffset.UtcNow;
        try
        {
            var (next, tiled) = LayoutPlan.TileReplan(prev, facts, survey, growth, advice);
            return run with { Next = next, Tiled = tiled, TileDur = DateTimeOffset.UtcNow - tileStart };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return run with { Error = e, TileDur = DateTimeOffset.UtcNow - tileStart };
        }
    }

    /// <summary>
    /// Whether a finished replan still applies at this review: the same loaded world, the saved plan it started
    /// from still the one held (its tick) and the game not rewound past its start.
    /// </summary>
    internal static bool LayoutReplanAdoptable(PendingLayoutReplan pending, GenerationSnapshot snapshot, Tick tick, HeldLayout layout, bool haveLayout) =>
        haveLayout && snapshot.SameWorld(pending.Snapshot) && layout.Tick == pending.Layout.Tick && tick >= pending.Started;

    /// <summary>
    /// Adopts a finished replan at this review: the layout_stations row (and the regroup rows) of its chain run,
    /// the layout_replan_tiled row (<c>adopted</c> the ticks since the run started), the recorded plan when it
    /// changed and the layout_plan row. The advice becomes the held one the overlay draws. Reports whether the
    /// plan was replanned (changed or not); false keeps the inputs of the last replan so the next hourly check
    /// tries again.
    /// </summary>
    internal async Task<bool> AdoptLayoutReplanAsync(PendingLayoutReplan pending, GenerationSnapshot snapshot, Tick tick, CancellationToken ct)
    {
        LayoutReplanRun run;
        try
        {
            run = await pending.Work.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Tel.Decide(new Decision { Kind = "layout_replan_tiled", Component = "layout", Target = "plan", Level = TelemetryLevel.Warn, Verdict = "failed", Reason = "tile_error", Attrs = new Dictionary<string, object?> { ["error"] = e.ToString() } });
            return false;
        }
        Tel.Decide(run.StationRow);
        foreach (var d in run.Advice.Regroup)
        {
            Tel.Decide(StationAdviceLog.RegroupDecision(d));
        }
        StationLast = run.Advice;
        StationHeld = run.Advised;
        var waited = tick.Value - pending.Started.Value;
        var decision = new Decision { Kind = "layout_replan_tiled", Component = "layout", Target = "plan", Dur = run.ChainDur + run.TileDur };
        if (!run.Advised)
        {
            Tel.Decide(decision with { Verdict = "skipped", Reason = "station_advice_unavailable", Attrs = new Dictionary<string, object?> { ["adopted"] = waited } });
            return false;
        }
        switch (run.Error)
        {
            case StationFactsException e:
                Tel.Decide(decision with { Verdict = "skipped", Reason = "facts_unknown", Attrs = new Dictionary<string, object?> { ["fact"] = e.Message, ["adopted"] = waited } });
                return false;
            case not null:
                Tel.Decide(decision with { Level = TelemetryLevel.Warn, Verdict = "failed", Reason = "tile_error", Attrs = new Dictionary<string, object?> { ["error"] = run.Error.ToString(), ["adopted"] = waited } });
                return false;
        }
        var plan = pending.Layout.Plan;
        var next = run.Next!;
        var advice = run.Tiled!;
        var changed = !LayoutPlan.SamePlan(plan, next);
        var tiledAttrs = StationAdviceLog.TiledReplanAttrs(LayoutPlan.DiffReplanTile(plan, run.Facts.Built ?? new HashSet<int>(), advice), run.Facts.Built?.Count ?? 0, advice.Tiled!.Plan.Rooms.Count);
        tiledAttrs["changed"] = changed;
        tiledAttrs["adopted"] = waited;
        if (pending.Drift.Count > 0)
        {
            // Which replan inputs drifted since the last replan: the hourly check replans on any of them.
            tiledAttrs["inputs_changed"] = pending.Drift;
        }
        Tel.Decide(decision with { Verdict = "replanned", Reason = "tiled_plan", Attrs = tiledAttrs });
        LayoutPlanning.LogInterior(advice);
        LayoutPlanning.LogConduits(advice);
        if (!changed)
        {
            return true;
        }
        await Player.Journal.RecordLayoutPlanAsync(snapshot, tick, next, ct).ConfigureAwait(false);
        Tel.Decide(LayoutPlanning.LayoutPlanDecision("replanned", pending.Reason, pending.Colonists, next.Summary(), null));
        if (LayoutPlan.PerimeterChanged(plan, next))
        {
            LayoutPlanning.LogPerimeter(next, pending.Colonists);
            ColonyPlanCut.LogPerimeterCut(next, pending.Survey, pending.Wall, pending.Colonists);
        }
        return true;
    }
}
