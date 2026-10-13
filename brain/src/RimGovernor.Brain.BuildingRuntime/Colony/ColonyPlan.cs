using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>What an incremental replan reads; a survey whose inputs match the last replan's (of the same saved plan) replans nothing.</summary>
internal sealed record LayoutInputs(Tick PlanTick, string Key, Bounds Bounds, EquatableArray<SurveyCell> Cells)
{
    public static LayoutInputs Empty { get; } = new(default, "", default, default);

    public bool Same(LayoutInputs other) =>
        PlanTick == other.PlanTick && Key == other.Key && Bounds == other.Bounds && Cells.Equals(other.Cells);
}

/// <summary>The native overlay layer the layout plan draws on, and the layer that holds its field zones (hidden natively by default).</summary>
public interface ILayoutOverlayNative
{
    Task<PbPresentation.OverlayApplied> DrawOverlayAsync(PbCommon.Identity identity, string layer, LayoutOverlay overlay, bool visible, CancellationToken ct);
}

/// <summary>A saved layout plan the world holds, with the tick that recorded it.</summary>
internal readonly record struct HeldLayout(LayoutPlan Plan, Tick Tick);

internal static class LayoutPlanning
{
    /// <summary>The fewest ticks between survey reads for any layout trigger (one game hour): a colony the grown plan still cannot house waits an hour instead of re-reading the map every review.</summary>
    public static readonly Tick ReplanEvery = new(2500);

    /// <summary>The terrain check: every game hour a review re-reads the survey and replans the layout when it changed.</summary>
    public static readonly Tick TerrainCheckEvery = new(2500);

    /// <summary>How often an unchanged plan's overlay is redrawn (one game day).</summary>
    public static readonly Tick OverlayRedrawEvery = new(60000);

    /// <summary>How often the traffic heat layers are redrawn (one game hour).</summary>
    public static readonly Tick HeatRedrawEvery = new(2500);

    public const string OverlayLayer = "layout";
    public const string FieldLayer = "fields";

    /// <summary>The tier and sorted finished research a plan is grown for.</summary>
    public static string GrownFor(ColonyProjection projection)
    {
        var research = ObservationApi.FinishedResearch(projection.Facts.Research).TryGetValue(out var finished)
            ? finished.Select(id => id.Value).Order(StringComparer.Ordinal).ToList()
            : [];
        return string.Create(CultureInfo.InvariantCulture, $"{(int)LayoutTier(projection)}|{string.Join(",", research)}");
    }

    /// <summary>The tech tier new bedrooms are sized for; an unknown tier reads Camp.</summary>
    public static TechTier LayoutTier(ColonyProjection projection) =>
        projection.TechTier.TryGetValue(out var tier) ? tier : default;

    /// <summary>Names the triggers that fired, sorted, for the layout_plan replanned row.</summary>
    public static string LayoutReasons(IReadOnlyDictionary<string, bool> fired) =>
        string.Join(",", fired.Where(kv => kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal));

    /// <summary>
    /// The layout_plan row of a plan derived (planned), grown (replanned, reason the triggers that fired) or not
    /// derivable (skipped), with the colonist count and the plan summary as attrs.
    /// </summary>
    public static Decision LayoutPlanDecision(string verdict, string reason, int colonists, string summary, IReadOnlyDictionary<string, object?>? extra)
    {
        var attrs = new Dictionary<string, object?> { ["colonists"] = colonists, ["summary"] = summary };
        if (extra is not null)
        {
            foreach (var (k, v) in extra)
            {
                attrs[k] = v;
            }
        }
        return new Decision { Kind = "layout_plan", Component = "layout", Verdict = verdict, Reason = reason, Target = "plan", Attrs = attrs };
    }

    /// <summary>Writes the layout_interior row of each interior search move the tiler kept.</summary>
    public static void LogInterior(StationAdvice advice)
    {
        if (advice.Tiled is null)
        {
            return;
        }
        foreach (var d in advice.Tiled.Decisions)
        {
            if (d.Reason == PolicyFunctions.ReasonInteriorSearchMove)
            {
                Tel.Decide(new Decision
                {
                    Kind = "layout_interior",
                    Component = "layout",
                    Verdict = d.Verdict,
                    Reason = d.Reason,
                    Target = "interior",
                    Attrs = new Dictionary<string, object?> { ["group"] = d.Group, ["move"] = d.Move },
                });
            }
        }
    }

    /// <summary>Writes the layout_plan rows of the traced ring and its opening (target perimeter): the ring is traced, the opening stands where the raider routes cross it or is absent.</summary>
    public static void LogPerimeter(LayoutPlan plan, int colonists)
    {
        foreach (var d in PolicyFunctions.PerimeterDecisions(plan))
        {
            var dec = LayoutPlanDecision(d.Verdict, d.Reason, colonists, "", new Dictionary<string, object?> { ["node"] = d.Node });
            dec = dec with { Target = "perimeter" };
            Tel.Decide(dec);
        }
    }
}

public sealed partial class Rounder
{
    /// <summary>
    /// The whole-map survey behind the layout plan, read off the projection's planning window and the load's
    /// definition catalog. A reviewer whose native serves no definitions, or a projection without a window,
    /// plans nothing. The catalog comes back with it so one review reads the definitions once (the station
    /// chain reuses it).
    /// </summary>
    internal async Task<(MapSurvey Survey, DefinitionCatalog Catalog)> LayoutSurveyAsync(GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
    {
        if (Native is not IDefinitionSource source || projection.Region == default)
        {
            throw new InvalidOperationException("layout survey: no definitions or planning window");
        }
        var catalog = await source.DefinitionCatalogAsync(Control.ControlIdentity(snapshot), ct).ConfigureAwait(false) ?? throw new InvalidOperationException("layout survey: native served no definition catalog");
        return (catalog.SurveyFromCells(projection.Cells, projection.Bounds), catalog);
    }

    /// <summary>
    /// Serves the saved v2 layout plan on the projection. It derives one at once when none is saved (then at
    /// most once an hour) and re-reads the survey every hour, replanning when a pawn, tier, research, need or
    /// the terrain changed since the last replan. The replan is the station chain's tiled plan
    /// (<c>ReplanLayout</c>): the chain keeps the rooms that stand, the rooms a journal plan works on or a pawn
    /// sleeps in and the rooms it has a node for, and sites the rest again.
    /// </summary>
    internal async Task ReviewLayoutPlanAsync(GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
    {
        var tick = projection.Identity.Tick;
        var (layout, haveLayout) = await LayoutPlanAsync(snapshot, tick, ct).ConfigureAwait(false);
        projection.Facts.Colonists.TryGetValue(out var pawns);
        // The last check is the newer of the saved plan's tick and the last survey this process read; a rewind
        // past it or a restart falls back to the plan's tick.
        var checkedAt = layout.Tick;
        if (PlanChecked > checkedAt && PlanChecked <= tick)
        {
            checkedAt = PlanChecked;
        }
        var hourly = !PlanSurveyed || tick.Value - checkedAt.Value >= LayoutPlanning.ReplanEvery.Value;
        var missing = !haveLayout && hourly;
        // Every hour a review re-reads the survey and replans when its inputs (the chain's facts, the survey)
        // changed: a need, a pawn or a room that stands is among them, so no trigger of its own is needed.
        var terrain = haveLayout && tick.Value - checkedAt.Value >= LayoutPlanning.TerrainCheckEvery.Value;
        // New research or a new tier: other research unlocks rooms too.
        var grown = LayoutPlanning.GrownFor(projection);
        var research = haveLayout && grown != PlanGrownFor && hourly;
        if (missing || terrain || research)
        {
            var replanned = false;
            (MapSurvey Survey, DefinitionCatalog Catalog)? read;
            try
            {
                read = await LayoutSurveyAsync(snapshot, projection, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // a failed survey retries on the next review
                read = null;
            }
            if (read is { Catalog: { } catalog } surveyRead)
            {
                var survey = surveyRead.Survey;
                PlanChecked = tick;
                PlanSurveyed = true;
                // A fresh plan latches the map's climate; unknown reads warm.
                survey = survey with
                {
                    Cold = projection.ColdMap.TryGetValue(out var cold) && cold,
                    Hot = projection.HotMap.TryGetValue(out var hot) && hot,
                };
                projection.PowerPlanning.TryGetValue(out var knownTopology);
                var topology = knownTopology ?? new PowerTopology();
                // The barn follows the herd plan's target herd.
                var animals = projection.Facts.PenAnimals();
                var colonists = (int)pawns;
                if (!haveLayout)
                {
                    await DeriveLayoutPlanAsync(snapshot, tick, projection, survey, catalog, topology.Geysers, animals, ColonyPlanCut.PerimeterWallOf(projection), ct).ConfigureAwait(false);
                    PlanGrownFor = grown;
                    PlanPawns = colonists;
                    PlanInputs = LayoutInputs.Empty;
                }
                else
                {
                    var (inputs, growth) = await LayoutInputsOfAsync(snapshot, projection, layout, survey, catalog, grown, colonists, topology.Geysers, animals, ct).ConfigureAwait(false);
                    if (!inputs.Same(PlanInputs))
                    {
                        var reason = LayoutPlanning.LayoutReasons(new Dictionary<string, bool>
                        {
                            ["terrain"] = inputs.Bounds != PlanInputs.Bounds || !inputs.Cells.Equals(PlanInputs.Cells),
                            ["pawns"] = colonists != PlanPawns,
                            ["research"] = research,
                        });
                        replanned = await ReplanLayoutAsync(snapshot, tick, layout.Plan, survey, growth, animals, colonists, reason, topology.Geysers, ColonyPlanCut.PerimeterWallOf(projection), ct).ConfigureAwait(false);
                        if (replanned)
                        {
                            PlanGrownFor = grown;
                            PlanPawns = colonists;
                            PlanInputs = inputs;
                        }
                    }
                }
                (layout, haveLayout) = await LayoutPlanAsync(snapshot, tick, ct).ConfigureAwait(false);
                if (replanned && haveLayout)
                {
                    // The recorded replan is the plan the same inputs plan next: its inputs, the plan being among
                    // them, are read again over it.
                    (PlanInputs, _) = await LayoutInputsOfAsync(snapshot, projection, layout, survey, catalog, PlanGrownFor, PlanPawns, topology.Geysers, animals, ct).ConfigureAwait(false);
                }
            }
        }
        if (haveLayout)
        {
            projection = projection with { LayoutPlan = Known(layout.Plan) };
        }
        await DrawLayoutOverlayAsync(snapshot, projection, layout, haveLayout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the station chain over the layout's plan and returns what a replan of it varies with, with the growth
    /// it was read under: the chain's facts and the growth, whose fixed rooms every new building moves. The facts
    /// hold the plan itself, so a replan that records a plan changes them.
    /// </summary>
    internal async Task<(LayoutInputs Inputs, RoomGrowth Growth)> LayoutInputsOfAsync(GenerationSnapshot snapshot, ColonyProjection projection, HeldLayout layout, MapSurvey survey, DefinitionCatalog catalog, string grown, int pawns, IReadOnlyList<PowerGeyser> geysers, int animals, CancellationToken ct)
    {
        var growth = await RoomGrowthAsync(snapshot, projection, layout.Plan, survey, Stockpiles.LayoutDemand(StockpileWorld(snapshot)), ct).ConfigureAwait(false);
        var keyed = growth with { Grazing = null };
        StationAdviceFor(snapshot, projection, layout.Plan, PolicyFunctions.TilePinned(layout.Plan, growth), growth, growth.Demand, survey, catalog);
        var key = string.Create(CultureInfo.InvariantCulture, $"{grown} {pawns} {string.Join(",", geysers)} {keyed} {animals} {StationInputs.Key}");
        return (new LayoutInputs(layout.Tick, key, survey.Bounds, [.. survey.Cells]), growth);
    }

    /// <summary>
    /// What the review holds of the colony for the replan beyond the chain's facts: the throne room the title
    /// asks, the shelter's retirement, the rooms with something of ours on them (read here, with the journal's
    /// open plans, only once a replan is due), the rooms a journal plan works on, the rooms of an ended role in
    /// use, the bedrooms a pawn still sleeps in, the herd and the ring's hold. Fixed stays null while the census
    /// or the plan catalog is unknown, which pins every room.
    /// </summary>
    internal async Task<RoomGrowth> RoomGrowthAsync(GenerationSnapshot snapshot, ColonyProjection projection, LayoutPlan plan, MapSurvey survey, RoomDemand demand, CancellationToken ct)
    {
        var growth = new RoomGrowth { HerdUnits = projection.Facts.HerdUnits(), Pens = projection.Facts.PenWants(), Demand = demand };
        var (need, owed) = BuildingRuntimePackage.ThroneNeed(projection);
        growth = growth with { ThroneArea = PolicyFunctions.ThroneAreaOwed(plan, need, owed) };
        if (projection.Facts.Pasture.TryGetValue(out var pasture))
        {
            growth = growth with { Grazing = PolicyFunctions.GrazingGround(survey, pasture) };
        }
        // A room whose need has ended and that is in use stays.
        if (projection.Facts.CurrentConstruction.TryGetValue(out var construction) && construction.Colony)
        {
            var ended = PolicyFunctions.EndedRoomRoles(projection.WorkPawns, projection.Facts.Ideology, projection.Facts.Containment, projection.Isolation, BuildingRuntimePackage.ChildRoomNeeds(projection));
            if (PolicyFunctions.RoomsOfRoles(plan, ended) > 0)
            {
                growth = growth with { InUse = PolicyFunctions.RoomsInUse(plan, construction.Buildings, BuildingRuntimePackage.FurnitureDefinitions(projection)) };
            }
        }
        // Bedrooms, workshop and laboratory stand and every colonist is housed: the shelter leaves the plan.
        // Re-read every review, no latch.
        growth = growth with { RetireShelter = projection.Rooms.TryGetValue(out var census) && projection.Facts.Sleeping.TryGetValue(out var sleeping) && projection.Facts.CurrentConstruction.TryGetValue(out var built) && built.Colony && PolicyFunctions.ShelterRetirable(plan, census, sleeping, built.Buildings) };
        var occupancy = await LayoutOccupiedAsync(projection, plan, ct).ConfigureAwait(false);
        if (occupancy is { } found)
        {
            var (ground, _) = BuildingRuntimePackage.ColonyGround(projection);
            growth = growth with
            {
                Fixed = PolicyFunctions.FixedRooms(plan, ground, found.Cells),
                InFlight = PolicyFunctions.InFlightRooms(plan, found.Origins),
            };
        }
        else
        {
            // Open plans unknown: no shelter is dropped over work in flight.
            growth = growth with { RetireShelter = false };
        }
        growth = growth with { Slept = PolicyFunctions.SleptBedrooms(plan, projection.Rooms, projection.Facts.Sleeping) };
        // The ring is laid around what the defense record holds of it.
        return growth with { Perimeter = await PerimeterHoldAsync(snapshot, ct).ConfigureAwait(false) };
    }

    /// <summary>
    /// Rewrites the overlay when the plan changed or a day passed since the last draw; with the overlay off it
    /// deletes the owned plans once per process. Output only: a failure is dropped, never fatal.
    /// </summary>
    internal async Task DrawLayoutOverlayAsync(GenerationSnapshot snapshot, ColonyProjection projection, HeldLayout layout, bool haveLayout, CancellationToken ct)
    {
        if (Native is not ILayoutOverlayNative native)
        {
            return;
        }
        await DrawHeatOverlayAsync(native, snapshot, projection, ct).ConfigureAwait(false);
        var tick = projection.Identity.Tick;
        try
        {
            if (!LayoutOverlay || !haveLayout)
            {
                if (!OverlayCleared)
                {
                    await native.DrawOverlayAsync(Control.ControlIdentity(snapshot), LayoutPlanning.OverlayLayer, new LayoutOverlay(), false, ct).ConfigureAwait(false);
                    await native.DrawOverlayAsync(Control.ControlIdentity(snapshot), LayoutPlanning.FieldLayer, new LayoutOverlay(), false, ct).ConfigureAwait(false);
                    OverlayCleared = true;
                }
                return;
            }
            // The defense tier nodes are cut from the defense round's census, not stored with the plan: they draw
            // beside it and move with each cut.
            var drawn = layout.Plan;
            var key = string.Create(CultureInfo.InvariantCulture, $"v2@{layout.Tick.Value}");
            if (Tiers.Load() is { } tiers)
            {
                drawn = drawn.WithDefenseTiers(tiers.Nodes);
                key = string.Create(CultureInfo.InvariantCulture, $"{key}/tiers@{tiers.Key}");
            }
            if (key == OverlayKey && tick >= OverlayDrawn && tick.Value - OverlayDrawn.Value < LayoutPlanning.OverlayRedrawEvery.Value)
            {
                return;
            }
            var overlay = drawn.Overlay(projection.Bounds);
            if (StationHeld)
            {
                layout.Plan.RequirementOverlay(StationLast, projection.Bounds, overlay);
            }
            var (layer, fields) = PolicyFunctions.SplitFields(overlay);
            await native.DrawOverlayAsync(Control.ControlIdentity(snapshot), LayoutPlanning.FieldLayer, fields, fields.Layers.Count > 0, ct).ConfigureAwait(false);
            await native.DrawOverlayAsync(Control.ControlIdentity(snapshot), LayoutPlanning.OverlayLayer, layer, true, ct).ConfigureAwait(false);
            OverlayKey = key;
            OverlayDrawn = tick;
            OverlayCleared = false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // output only
        }
    }

    /// <summary>Reads what the defense record holds of the plan's ring nodes (none while it has no record).</summary>
    internal async Task<PerimeterHold> PerimeterHoldAsync(GenerationSnapshot snapshot, CancellationToken ct)
    {
        var record = await Player.Journal.LoadDefenseLayoutAsync(new World(snapshot.Colony, snapshot.Load, snapshot.Map), ct).ConfigureAwait(false);
        return record is null ? new PerimeterHold() : record.PerimeterHold();
    }

    /// <summary>Reads the v2 layout plan. A saved plan that no longer decodes or validates reads as none, noted once per process, so the next survey derives a fresh one.</summary>
    internal async Task<(HeldLayout Record, bool Found)> LayoutPlanAsync(GenerationSnapshot snapshot, Tick tick, CancellationToken ct)
    {
        var record = await Player.Journal.LayoutPlanAsync(snapshot, tick, ct).ConfigureAwait(false);
        if (record is { Invalid: true } && !LayoutInvalidLogged)
        {
            LayoutInvalidLogged = true;
        }
        return record is { Plan: { } plan } ? (new HeldLayout(plan, record.Tick), true) : (default, false);
    }

    /// <summary>
    /// Lays a fresh v2 plan over the survey and the reported geysers from the station chain and records it. A
    /// fact the colony has not read leaves the plan at the seed rooms (the layout_stations row says which); a
    /// chain that fails records nothing and the next hourly review derives again.
    /// </summary>
    internal async Task DeriveLayoutPlanAsync(GenerationSnapshot snapshot, Tick tick, ColonyProjection projection, MapSurvey survey, DefinitionCatalog catalog, IReadOnlyList<PowerGeyser> geysers, int animals, PerimeterWall wall, CancellationToken ct)
    {
        projection.Facts.Colonists.TryGetValue(out var pawns);
        var colonists = (int)pawns;
        var start = DateTimeOffset.UtcNow;
        var f = StationFactsBuilder.FirstPlanFacts(projection, survey, StationNeeds(snapshot), WorkLedgerView());
        f = f with { Crops = FieldCrops.Of(projection), FoodReserveDays = Seasonal(projection.Facts).FoodTargetDays };
        Fact<LayoutPlan> fact = default;
        StationAdvice advice = new();
        Exception? error = null;
        try
        {
            f = StationFactsBuilder.StationFacts(catalog, f);
            (fact, advice) = PolicyFunctions.DeriveLayoutPlan(survey, f, geysers, animals);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = e;
        }
        var row = StationAdviceLog.StationOutcome(advice, error).Row;
        row = row with { Dur = DateTimeOffset.UtcNow - start, Attrs = new Dictionary<string, object?>(row.Attrs) { ["derive"] = true } };
        Tel.Decide(row);
        LayoutPlanning.LogInterior(advice);
        switch (error)
        {
            case StationFactsException:
                Tel.Decide(LayoutPlanning.LayoutPlanDecision("skipped", "facts_unknown", colonists, "", new Dictionary<string, object?> { ["fact"] = error.Message }));
                return;
            case not null:
                Tel.Decide(LayoutPlanning.LayoutPlanDecision("failed", "chain_error", colonists, "", new Dictionary<string, object?> { ["error"] = error.Message }));
                return;
        }
        if (!fact.TryGetValue(out var plan))
        {
            Tel.Decide(LayoutPlanning.LayoutPlanDecision("skipped", "no_core", colonists, "", null));
            return;
        }
        await Player.Journal.RecordLayoutPlanAsync(snapshot, tick, plan, ct).ConfigureAwait(false);
        Tel.Decide(LayoutPlanning.LayoutPlanDecision("planned", "", colonists, plan.Summary(), null));
        LayoutPlanning.LogPerimeter(plan, colonists);
        ColonyPlanCut.LogPerimeterCut(plan, survey, wall, colonists);
    }

    /// <summary>
    /// Replans the recorded v2 plan from the station chain's tiled plan over a fresh survey
    /// (<c>ReplanLayout</c>) and records it when it changed. A chain or tiler that cannot plan keeps the plan
    /// the colony has: the layout_replan_tiled row says why, and the replan is reported undone (false) so the
    /// next trigger tries again. Reports whether the plan was replanned, changed or not.
    /// </summary>
    internal async Task<bool> ReplanLayoutAsync(GenerationSnapshot snapshot, Tick tick, LayoutPlan plan, MapSurvey survey, RoomGrowth growth, int animals, int pawns, string reason, IReadOnlyList<PowerGeyser> geysers, PerimeterWall wall, CancellationToken ct)
    {
        var decision = new Decision { Kind = "layout_replan_tiled", Component = "layout", Target = "plan" };
        if (!StationHeld)
        {
            decision = decision with { Verdict = "skipped" };
            decision = decision with { Reason = "station_advice_unavailable" };
            Tel.Decide(decision);
            return false;
        }
        var stationFacts = StationFacts;
        var start = DateTimeOffset.UtcNow;
        LayoutPlan next;
        StationAdvice advice;
        try
        {
            (next, advice) = PolicyFunctions.ReplanLayout(plan, stationFacts, survey, geysers, growth, animals);
        }
        catch (StationFactsException e)
        {
            decision = decision with { Dur = DateTimeOffset.UtcNow - start };
            decision = decision with { Verdict = "skipped" };
            decision = decision with { Reason = "facts_unknown" };
            decision = decision with { Attrs = new Dictionary<string, object?> { ["fact"] = e.Message } };
            Tel.Decide(decision);
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            decision = decision with { Dur = DateTimeOffset.UtcNow - start };
            decision = decision with { Level = TelemetryLevel.Warn };
            decision = decision with { Verdict = "failed" };
            decision = decision with { Reason = "tile_error" };
            decision = decision with { Attrs = new Dictionary<string, object?> { ["error"] = e } };
            Tel.Decide(decision);
            return false;
        }
        decision = decision with { Dur = DateTimeOffset.UtcNow - start };
        var changed = !PolicyFunctions.SamePlan(plan, next);
        decision = decision with { Verdict = "replanned" };
        decision = decision with { Reason = "tiled_plan" };
        var tiledAttrs = StationAdviceLog.TiledReplanAttrs(PolicyFunctions.DiffReplanTile(plan, stationFacts.Built ?? new HashSet<int>(), advice), stationFacts.Built?.Count ?? 0, advice.Tiled!.Plan.Rooms.Count);
        tiledAttrs["changed"] = changed;
        decision = decision with { Attrs = tiledAttrs };
        Tel.Decide(decision);
        LayoutPlanning.LogInterior(advice);
        if (!changed)
        {
            return true;
        }
        await Player.Journal.RecordLayoutPlanAsync(snapshot, tick, next, ct).ConfigureAwait(false);
        Tel.Decide(LayoutPlanning.LayoutPlanDecision("replanned", reason, pawns, next.Summary(), null));
        if (PolicyFunctions.PerimeterChanged(plan, next))
        {
            LayoutPlanning.LogPerimeter(next, pawns);
            ColonyPlanCut.LogPerimeterCut(next, survey, wall, pawns);
        }
        return true;
    }

    /// <summary>Redraws a "heat.&lt;layer&gt;" overlay layer per traffic layer from the census's busiest cells, on its own hourly cadence; with the overlay off it removes them once. Output only, like the layout.</summary>
    internal async Task DrawHeatOverlayAsync(ILayoutOverlayNative native, GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
    {
        var known = projection.Facts.Upkeep.Flooring.TryGetValue(out var census);
        var on = LayoutOverlay && known;
        if (!on && HeatCleared)
        {
            return;
        }
        var tick = projection.Identity.Tick;
        if (on && HeatDrawn.Value != 0 && tick >= HeatDrawn && tick.Value - HeatDrawn.Value < LayoutPlanning.HeatRedrawEvery.Value)
        {
            return;
        }
        try
        {
            foreach (var layer in PolicyFunctions.TrafficLayers)
            {
                var heat = on && census is not null ? PolicyFunctions.TrafficOverlay(census.Traffic, layer, projection.Bounds) : new LayoutOverlay();
                await native.DrawOverlayAsync(Control.ControlIdentity(snapshot), "heat." + Wire.Name(layer), heat, on && heat.Layers.Count > 0, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }
        HeatDrawn = tick;
        HeatCleared = !on;
    }
}
