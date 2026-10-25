using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

/// <summary>
/// A layout replan running off the review's hold path (#3565): what the hourly check read when it started it and
/// the task preparing its surveyed station facts. A later review compares and tiles its candidates when
/// the world and the saved plan are still the ones it started from.
/// </summary>
internal sealed record PendingLayoutReplan(
    GenerationSnapshot Snapshot,
    Tick Started,
    HeldLayout Layout,
    ColonyReading Projection,
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

/// <summary>The prepared facts and numbered previous plan; canonical candidate comparison runs on the completed review frame.</summary>
internal sealed record LayoutReplanRun
{
    public required Decision StationRow { get; init; }

    public LayoutPlan Previous { get; init; } = new();

    /// <summary>Whether the station facts were prepared without a required-fact or catalog failure.</summary>
    public bool Advised { get; init; }

    public StationFacts Facts { get; init; } = new();

    public TimeSpan ChainDur { get; init; }
}

public sealed partial class ColonyReview
{
    /// <summary>
    /// Prepares the replan of the saved plan on a worker over what
    /// the hourly check read (<c>RunLayoutReplan</c>); the review that finds the run complete adopts it
    /// (<c>AdoptLayoutReplanAsync</c>).
    /// </summary>
    internal static PendingLayoutReplan StartLayoutReplan(GenerationSnapshot snapshot, Tick tick, HeldLayout layout, ColonyReading projection, MapSurvey survey, DefinitionCatalog catalog, RoomGrowth growth, StationFacts facts, int colonists, string grown, string reason, IReadOnlyList<PowerGeyser> geysers, PerimeterWall wall, IReadOnlyList<string> drift)
    {
        var plan = layout.Plan;
        var work = Task.Run(() => RunLayoutReplan(plan, facts, survey, catalog, geysers, growth));
        return new PendingLayoutReplan(snapshot, tick, layout, projection, survey, catalog, growth, colonists, grown, reason, geysers, wall, drift, work);
    }

    /// <summary>
    /// The replan's work, pure: the catalog rows onto the facts, the replan's facts over the saved plan
    /// (<c>ReplanFacts</c>). Every failure is returned in the run, none thrown. No candidate is selected or saved here.
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
        var (row, _, ok) = StationAdviceLog.StationOutcome(new StationAdvice(), error);
        return new LayoutReplanRun { StationRow = row, Advised = ok, Facts = facts, Previous = prev, ChainDur = DateTimeOffset.UtcNow - start };
    }

    /// <summary>
    /// Whether a finished replan still applies at this review: the same loaded world, the saved plan it started
    /// from still the one held (its tick) and the game not rewound past its start.
    /// </summary>
    internal static bool LayoutReplanAdoptable(PendingLayoutReplan pending, GenerationSnapshot snapshot, Tick tick, HeldLayout layout, bool haveLayout) =>
        haveLayout && snapshot.SameWorld(pending.Snapshot) && layout.Tick == pending.Layout.Tick && tick >= pending.Started;

    /// <summary>
    /// The rooms of the saved plan an open journal plan works on now that the run did not hold when it started (#3537):
    /// a shell admitted in the review that started the run, whose blueprints the run may have sited elsewhere. Empty when
    /// the run held every room (the census was unknown) or the occupancy is unknown now.
    /// </summary>
    internal async Task<IReadOnlyCollection<int>> NewlyHeldRoomsAsync(PendingLayoutReplan pending, ColonyReading projection, CancellationToken ct)
    {
        if (pending.Growth.Fixed is null || await LayoutOccupiedAsync(projection, pending.Layout.Plan, ct).ConfigureAwait(false) is not { } found)
        {
            return [];
        }
        return NewlyHeldRooms(pending.Growth, LayoutPlan.InFlightRooms(pending.Layout.Plan, found.Origins, found.Rooms));
    }

    /// <summary>The rooms in flight now that the run's growth did not hold: outside its fixed, in-flight, slept and in-use rooms (#3537).</summary>
    internal static IReadOnlyCollection<int> NewlyHeldRooms(RoomGrowth growth, IEnumerable<int> inFlight) =>
        [.. inFlight.Where(id => growth.Fixed?.Contains(id) == false && !growth.InFlight.Contains(id) && !growth.Slept.Contains(id) && !growth.InUse.Contains(id)).Order()];

    /// <summary>
    /// Adopts a finished replan at this review: the layout_stations row (and the regroup rows) of its chain run,
    /// the layout_replan_tiled row (<c>adopted</c> the ticks since the run started), the recorded plan when it
    /// changed and the layout_plan row. The advice becomes the held one the overlay draws. Reports whether the
    /// plan was replanned (changed or not); false keeps the inputs of the last replan so the next hourly check
    /// tries again.
    /// </summary>
    internal async Task<EvaluatedLayoutCandidate?> AdoptLayoutReplanAsync(PendingLayoutReplan pending, GenerationSnapshot snapshot, Tick tick, ColonyReading projection, Latches latches, GoalsFacts? goals, StockpileRequest? stockpile, ProductionBatchCensus batches, WorkActionCensus actions, CancellationToken ct)
    {
        LayoutReplanRun run;
        try
        {
            run = await pending.Work.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Tel.Decide(new Decision { Kind = TelemetryKinds.LayoutReplanTiledKind, Component = "layout", Target = "plan", Level = TelemetryLevel.Warn, Verdict = "failed", Reason = "tile_error", Attrs = new Dictionary<string, object?> { ["error"] = e.ToString() } });
            return null;
        }
        var waited = tick.Value - pending.Started.Value;
        var decision = new Decision { Kind = TelemetryKinds.LayoutReplanTiledKind, Component = "layout", Target = "plan", Dur = run.ChainDur };
        if (!run.Advised)
        {
            Tel.Decide(run.StationRow);
            return null;
        }
        var compareStart = DateTimeOffset.UtcNow;
        EvaluatedLayoutCandidate winner;
        try
        {
            winner = await CompareLayoutCandidatesAsync(snapshot, projection, run.Facts, candidate =>
            {
                var result = LayoutPlan.TileReplan(run.Previous, run.Facts, pending.Survey, pending.Growth, candidate);
                return (result.Plan, result.Advice);
            }, latches, goals, stockpile, batches, actions, ct).ConfigureAwait(false);
        }
        catch (RequiredFactException unread)
        {
            Tel.Decide(RequiredFactTelemetry.Skipped("layout", decision.Kind, unread.Fact, unread.Site));
            return null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Tel.Decide(decision with { Level = TelemetryLevel.Warn, Verdict = "failed", Reason = "tile_error", Attrs = new Dictionary<string, object?> { ["error"] = error.ToString(), ["adopted"] = waited } });
            return null;
        }
        decision = decision with { Dur = run.ChainDur + (DateTimeOffset.UtcNow - compareStart) };
        var plan = pending.Layout.Plan;
        var next = winner.Plan;
        var advice = winner.Advice;
        Tel.Decide(StationAdviceLog.StationOutcome(advice, null).Row);
        foreach (var d in advice.Regroup) Tel.Decide(StationAdviceLog.RegroupDecision(d));
        StationLast = advice;
        StationHeld = true;
        var changed = !LayoutPlan.SamePlan(plan, next);
        var replan = advice.Embedding.Replan;
        var tiledAttrs = StationAdviceLog.TiledReplanAttrs(LayoutPlan.DiffReplanTile(plan, run.Facts.Built ?? new HashSet<int>(), advice), run.Facts.Built?.Count ?? 0, advice.Tiled!.Plan.Rooms.Count, replan, pending.Growth.Fixed);
        tiledAttrs["changed"] = changed;
        tiledAttrs["adopted"] = waited;
        if (pending.Drift.Count > 0)
        {
            // Which replan inputs drifted since the last replan (the hourly check replans on any of them), or "daily".
            tiledAttrs["inputs_changed"] = pending.Drift;
        }
        var (verdict, reason) = StationAdviceLog.TiledReplanVerdict(replan);
        Tel.Decide(decision with { Verdict = verdict, Reason = reason, Attrs = tiledAttrs });
        LayoutPlanning.LogInterior(advice);
        LayoutPlanning.LogConduits(advice);
        if (!changed)
        {
            return winner;
        }
        await Player.Journal.RecordLayoutPlanAsync(snapshot, tick, next, ct).ConfigureAwait(false);
        Tel.Decide(LayoutPlanning.LayoutPlanDecision("replanned", pending.Reason, pending.Colonists, next.Summary(), null));
        if (LayoutPlan.PerimeterChanged(plan, next))
        {
            LayoutPlanning.LogPerimeter(next, pending.Colonists);
            ColonyPlanCut.LogPerimeterCut(next, pending.Survey, pending.Wall, pending.Colonists);
        }
        return winner;
    }
}
