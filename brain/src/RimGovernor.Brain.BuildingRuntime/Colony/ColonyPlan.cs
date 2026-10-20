using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>What an incremental replan reads; a survey whose inputs match the last replan's (of the same saved plan) replans nothing.</summary>
internal sealed record LayoutInputs(Tick PlanTick, string Key, Bounds Bounds, EquatableArray<SurveyCell> Cells)
{
    public static LayoutInputs Empty { get; } = new(default, "", default, default);

    /// <summary>The key's named parts (StationFacts.InputsKeyParts and the replan's own), so Changed can name what drifted.</summary>
    public IReadOnlyDictionary<string, string> Parts { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool Same(LayoutInputs other) =>
        PlanTick == other.PlanTick && Key == other.Key && Bounds == other.Bounds && Cells.Equals(other.Cells);

    /// <summary>
    /// What differs from other, by name: "plan_tick", "survey" (the bounds or cells) and each key part whose text
    /// changed or that only one side has; "key" when the keys differ but no part says why.
    /// </summary>
    public IReadOnlyList<string> Changed(LayoutInputs other)
    {
        var names = new List<string>();
        if (PlanTick != other.PlanTick)
        {
            names.Add("plan_tick");
        }
        if (Bounds != other.Bounds || !Cells.Equals(other.Cells))
        {
            names.Add("survey");
        }
        var parts = new List<string>();
        foreach (var (name, value) in Parts)
        {
            if (!other.Parts.TryGetValue(name, out var was) || was != value)
            {
                parts.Add(name);
            }
        }
        foreach (var name in other.Parts.Keys)
        {
            if (!Parts.ContainsKey(name))
            {
                parts.Add(name);
            }
        }
        parts.Sort(StringComparer.Ordinal);
        names.AddRange(parts);
        if (names.Count == 0 && Key != other.Key)
        {
            names.Add("key");
        }
        return names;
    }
}

/// <summary>A saved layout plan the world holds, with the tick that recorded it.</summary>
internal readonly record struct HeldLayout(LayoutPlan Plan, Tick Tick);

internal static class LayoutPlanning
{
    /// <summary>
    /// The fewest ticks between survey reads for any layout trigger (one game hour): a colony the grown plan still cannot
    /// house waits an hour instead of re-reading the map every review. A first plan that waits for a fact with the
    /// clock held retries every review (<see cref="Rounder.FirstPlanUnread"/>).
    /// </summary>
    public static readonly Tick ReplanEvery = new(2500);

    /// <summary>The terrain check: every game hour a review re-reads the survey and replans the layout when it changed.</summary>
    public static readonly Tick TerrainCheckEvery = new(2500);

    /// <summary>How often an unchanged plan's overlay is redrawn (one game day).</summary>
    public static readonly Tick OverlayRedrawEvery = new(60000);

    /// <summary>How often the traffic heat layers are redrawn (one game hour).</summary>
    public static readonly Tick HeatRedrawEvery = new(2500);

    /// <summary>What a recorded first plan holds the clock for until a review starts with it in hand (#3652).</summary>
    public const string FirstPlanPending = "layout plan";

    public const string OverlayLayer = "layout";
    public const string FieldLayer = "fields";

    /// <summary>The tier and sorted finished research a plan is grown for.</summary>
    public static string GrownFor(ColonyProjection projection)
    {
        var research = RoundsReads.FinishedResearch(projection.Facts.Research).TryGetValue(out var finished)
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
        return new Decision { Kind = TelemetryKinds.LayoutPlanKind, Component = "layout", Verdict = verdict, Reason = reason, Target = "plan", Attrs = attrs };
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
            if (d.Reason == LayoutTileRules.ReasonInteriorSearchMove)
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

    /// <summary>The layout_conduit row of each conduit decision the tiler made: the node, the node it joined (empty when it found no net) and why.</summary>
    internal static IEnumerable<Decision> ConduitRows(StationAdvice advice) =>
        advice.Tiled?.ConduitDecisions.Select(d => new Decision
        {
            Kind = "layout_conduit",
            Component = "layout",
            Verdict = d.Verdict,
            Reason = d.Reason,
            Target = d.From.Value,
            Attrs = new Dictionary<string, object?> { ["to"] = d.To.Value ?? "" },
        }) ?? [];

    /// <summary>Writes the layout_conduit rows of a derive or replan: which power links were kept, routed anew or left unrouted.</summary>
    public static void LogConduits(StationAdvice advice)
    {
        foreach (var row in ConduitRows(advice))
        {
            Tel.Decide(row);
        }
    }

    /// <summary>Writes the layout_plan rows of the traced ring and its opening (target perimeter): the ring is traced, the opening stands where the raider routes cross it or is absent.</summary>
    public static void LogPerimeter(LayoutPlan plan, int colonists)
    {
        foreach (var d in LayoutPlan.PerimeterDecisions(plan))
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
    /// Serves the saved v2 layout plan on the projection. A colony with none saved gets its first plan at the end of
    /// the review (<see cref="ReviewFirstPlanAsync"/>). With one, it re-reads the survey every hour, replanning when a
    /// pawn, tier, research, need or the terrain changed since the last replan. The replan is the station chain's tiled plan
    /// (<c>ReplanLayout</c>): the chain keeps the rooms that stand, the rooms a journal plan works on or a pawn
    /// sleeps in and the rooms it has a node for, and sites the rest again. It runs off the hold path
    /// (<see cref="PendingLayoutReplan"/>, #3565): the hourly check that finds the inputs drifted starts it and
    /// a later review adopts its result; no second replan starts while one is pending.
    /// </summary>
    internal async Task<ColonyProjection> ReviewLayoutPlanAsync(GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
    {
        var tick = projection.Identity.Tick;
        var (layout, haveLayout) = await LayoutPlanAsync(snapshot, tick, ct).ConfigureAwait(false);
        if (LayoutPending is { Work.IsCompleted: true } pending)
        {
            LayoutPending = null;
            if (LayoutReplanAdoptable(pending, snapshot, tick, layout, haveLayout))
            {
                if (await AdoptLayoutReplanAsync(pending, snapshot, tick, ct).ConfigureAwait(false))
                {
                    PlanGrownFor = pending.Grown;
                    PlanPawns = pending.Colonists;
                    (layout, haveLayout) = await LayoutPlanAsync(snapshot, tick, ct).ConfigureAwait(false);
                    if (haveLayout)
                    {
                        // The recorded replan is the plan the same inputs plan next: its inputs, the plan being
                        // among them, are read again over it, from what the replan was planned from.
                        (PlanInputs, _, _) = await LayoutInputsOfAsync(snapshot, pending.Projection, layout, pending.Survey, pending.Grown, pending.Colonists, pending.Geysers, ct).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                // The world or the saved plan moved on under the run: its plan is dropped, the next hourly check reads again.
                Tel.Decide(new Decision { Kind = "layout_replan_tiled", Component = "layout", Target = "plan", Verdict = "skipped", Reason = "superseded", Attrs = new Dictionary<string, object?> { ["started_tick"] = pending.Started.Value, ["adopted"] = tick.Value - pending.Started.Value } });
            }
        }
        projection.Facts.Colonists.TryGetValue(out var pawns);
        // The last check is the newer of the saved plan's tick and the last survey this process read; a rewind
        // past it or a restart falls back to the plan's tick.
        var checkedAt = layout.Tick;
        var surveyed = Surveyed(snapshot);
        if (surveyed && PlanChecked > checkedAt && PlanChecked <= tick)
        {
            checkedAt = PlanChecked;
        }
        var hourly = !surveyed || tick.Value - checkedAt.Value >= LayoutPlanning.ReplanEvery.Value;
        // Every hour a review re-reads the survey and replans when its inputs (the chain's facts, the survey)
        // changed: a need, a pawn or a room that stands is among them, so no trigger of its own is needed.
        var terrain = haveLayout && tick.Value - checkedAt.Value >= LayoutPlanning.TerrainCheckEvery.Value;
        // New research or a new tier: other research unlocks rooms too.
        var grown = LayoutPlanning.GrownFor(projection);
        var research = haveLayout && grown != PlanGrownFor && hourly;
        // A pending replan is the hour's: the check waits for it.
        if ((terrain || research) && LayoutPending is null)
        {
            if (await CheckSurveyAsync(snapshot, projection, ct).ConfigureAwait(false) is var (survey, catalog))
            {
                projection.PowerPlanning.TryGetValue(out var knownTopology);
                var topology = knownTopology ?? new PowerTopology();
                var colonists = (int)pawns;
                var (inputs, growth, facts) = await LayoutInputsOfAsync(snapshot, projection, layout, survey, grown, colonists, topology.Geysers, ct).ConfigureAwait(false);
                if (!inputs.Same(PlanInputs))
                {
                    var reason = LayoutPlanning.LayoutReasons(new Dictionary<string, bool>
                    {
                        ["terrain"] = inputs.Bounds != PlanInputs.Bounds || !inputs.Cells.Equals(PlanInputs.Cells),
                        ["pawns"] = colonists != PlanPawns,
                        ["research"] = research,
                    });
                    LayoutPending = StartLayoutReplan(snapshot, tick, layout, projection, survey, catalog, growth, facts, colonists, grown, reason, topology.Geysers, ColonyPlanCut.PerimeterWallOf(projection), inputs.Changed(PlanInputs));
                }
            }
        }
        if (haveLayout)
        {
            projection = projection with { LayoutPlan = Known(layout.Plan) };
        }
        await DrawLayoutOverlayAsync(snapshot, projection, layout, haveLayout, ct).ConfigureAwait(false);
        return projection;
    }

    /// <summary>Whether the last layout survey was read in <paramref name="world"/>'s colony, map and load (#3652: a load in the same process surveys again).</summary>
    private bool Surveyed(GenerationSnapshot world) =>
        PlanSurveyedIn is { } at && at.Colony == world.Colony && at.Map == world.Map && at.Load == world.Load;

    /// <summary>
    /// The survey read of a layout check, recorded as the last check, with the map's climate latched on it (unknown
    /// reads warm); null when the read failed, which the next review retries.
    /// </summary>
    private async Task<(MapSurvey Survey, DefinitionCatalog Catalog)?> CheckSurveyAsync(GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
    {
        (MapSurvey Survey, DefinitionCatalog Catalog) read;
        try
        {
            read = await LayoutSurveyAsync(snapshot, projection, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
        PlanChecked = projection.Identity.Tick;
        PlanSurveyedIn = snapshot;
        var survey = read.Survey with
        {
            Cold = projection.ColdMap.TryGetValue(out var cold) && cold,
            Hot = projection.HotMap.TryGetValue(out var hot) && hot,
        };
        return (survey, read.Catalog);
    }

    /// <summary>
    /// The first plan of a colony with none saved, at the end of the review (#3652): after the ledger and the resource
    /// demand, so every station fact the review reads is read. It derives at once, then once an hour, and every review
    /// while it holds the clock (<see cref="FirstPlanUnread"/>): named for the fact the derive found unread, or
    /// <see cref="LayoutPlanning.FirstPlanPending"/> once it recorded the plan, until a review starts with the plan in
    /// hand. A review with a saved plan, or a derive that fails for anything but an unread fact, holds nothing.
    /// </summary>
    internal async Task ReviewFirstPlanAsync(GenerationSnapshot snapshot, GenerationSnapshot demandWorld, ColonyProjection projection, CancellationToken ct)
    {
        var tick = projection.Identity.Tick;
        var (_, haveLayout) = await LayoutPlanAsync(snapshot, tick, ct).ConfigureAwait(false);
        if (haveLayout)
        {
            FirstPlanUnread = "";
            return;
        }
        // A rewind past the last survey or a restart reads again.
        var hourly = !Surveyed(snapshot) || PlanChecked > tick || tick.Value - PlanChecked.Value >= LayoutPlanning.ReplanEvery.Value;
        if ((!hourly && FirstPlanUnread.Length == 0) || LayoutPending is not null)
        {
            return;
        }
        if (await CheckSurveyAsync(snapshot, projection, ct).ConfigureAwait(false) is not var (survey, catalog))
        {
            return;
        }
        projection.PowerPlanning.TryGetValue(out var knownTopology);
        FirstPlanUnread = await DeriveLayoutPlanAsync(snapshot, demandWorld, tick, projection, survey, catalog, (knownTopology ?? new PowerTopology()).Geysers, ColonyPlanCut.PerimeterWallOf(projection), ct).ConfigureAwait(false);
        projection.Facts.Colonists.TryGetValue(out var pawns);
        PlanGrownFor = LayoutPlanning.GrownFor(projection);
        PlanPawns = (int)pawns;
        PlanInputs = LayoutInputs.Empty;
    }

    /// <summary>
    /// What a replan of the layout's plan varies with, with the growth and the chain's facts it was read under:
    /// the facts' key parts (<c>StationInputsOf</c>) and the growth, whose fixed rooms every new building moves.
    /// The facts hold the plan itself, so a replan that records a plan changes them. Pure beyond the growth's
    /// reads: the chain runs in the replan.
    /// </summary>
    internal async Task<(LayoutInputs Inputs, RoomGrowth Growth, StationFacts Facts)> LayoutInputsOfAsync(GenerationSnapshot snapshot, ColonyProjection projection, HeldLayout layout, MapSurvey survey, string grown, int pawns, IReadOnlyList<PowerGeyser> geysers, CancellationToken ct)
    {
        var growth = await RoomGrowthAsync(snapshot, projection, layout.Plan, Stockpiles.LayoutDemand(RoundsStockpilePlanner.StockpileWorld(snapshot)), ct).ConfigureAwait(false);
        var bedMerge = await BedMergeFactsAsync(snapshot, projection, ct).ConfigureAwait(false);
        var (facts, station) = StationInputsOf(snapshot, projection, layout.Plan, growth, survey, bedMerge);
        var key = string.Create(CultureInfo.InvariantCulture, $"{grown} {pawns} {string.Join(",", geysers)} {growth} {station.Key}");
        var parts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grown"] = grown,
            ["pawns"] = string.Create(CultureInfo.InvariantCulture, $"{pawns}"),
            ["geysers"] = string.Join(",", geysers),
            ["growth"] = string.Create(CultureInfo.InvariantCulture, $"{growth}"),
        };
        foreach (var (name, value) in station.Parts)
        {
            parts[name] = value;
        }
        return (new LayoutInputs(layout.Tick, key, survey.Bounds, [.. survey.Cells]) { Parts = parts }, growth, facts);
    }

    /// <summary>
    /// What the review holds of the colony for the replan beyond the chain's facts: the throne room the title
    /// asks, the shelter's retirement, the rooms with something of ours on them (read here, with the journal's
    /// open plans, only once a replan is due), the rooms a journal plan works on, the rooms of an ended role in
    /// use, the bedrooms a pawn still sleeps in, the herd and the ring's hold. Fixed stays null while the census
    /// or the plan catalog is unknown, which pins every room.
    /// </summary>
    internal async Task<RoomGrowth> RoomGrowthAsync(GenerationSnapshot snapshot, ColonyProjection projection, LayoutPlan plan, RoomDemand demand, CancellationToken ct)
    {
        // The stores' demand, plus the burial's: a tomb or morgue joins the plan only once the dead ask for it.
        var burial = RoundsBurialFacts.BurialDemand(projection, plan);
        var growth = new RoomGrowth { Pens = projection.Facts.PenWants(), Demand = demand with { Tomb = burial.Tomb, Morgue = burial.Morgue } };
        var (need, owed) = RoundsSleepingUpkeepPlanner.ThroneNeed(projection);
        growth = growth with { ThroneArea = ThroneRules.ThroneAreaOwed(plan, need, owed) };
        // A room whose need has ended and that is in use stays.
        if (projection.Facts.CurrentConstruction.TryGetValue(out var construction) && construction.Colony)
        {
            var ended = LayoutRoomsRules.EndedRoomRoles(projection.WorkPawns, projection.Facts.Ideology, projection.Facts.Containment, projection.Isolation, RoundsSleepingUpkeepPlanner.ChildRoomNeeds(projection));
            if (LayoutPlan.RoomsOfRoles(plan, ended) > 0)
            {
                growth = growth with { InUse = LayoutPlan.RoomsInUse(plan, construction.Buildings, RoundsSleepingUpkeepPlanner.FurnitureDefinitions(projection)) };
            }
        }
        // Bedrooms, workshop and laboratory stand and every colonist is housed: the shelter leaves the plan.
        // Re-read every review, no latch.
        growth = growth with { RetireShelter = projection.Rooms.TryGetValue(out var census) && projection.Facts.Sleeping.TryGetValue(out var sleeping) && projection.Facts.CurrentConstruction.TryGetValue(out var built) && built.Colony && LayoutPlan.ShelterRetirable(plan, census, sleeping, built.Buildings) };
        var occupancy = await LayoutOccupiedAsync(projection, plan, ct).ConfigureAwait(false);
        if (occupancy is { } found)
        {
            var (ground, _) = RoundsBuildingPlanner.ColonyGround(projection);
            growth = growth with
            {
                Fixed = LayoutPlan.FixedRooms(plan, ground, found.Cells),
                InFlight = LayoutPlan.InFlightRooms(plan, found.Origins, found.Rooms),
            };
        }
        else
        {
            // Open plans unknown: no shelter is dropped over work in flight.
            growth = growth with { RetireShelter = false };
        }
        growth = growth with { Slept = SleepingRules.SleptBedrooms(plan, projection.Rooms, projection.Facts.Sleeping) };
        // The ring is laid around what the defense record holds of it.
        return growth with { Perimeter = await PerimeterHoldAsync(snapshot, ct).ConfigureAwait(false) };
    }

    /// <summary>
    /// Rewrites the overlay when the plan changed or a day passed since the last draw; with the overlay off it
    /// deletes the owned plans once per process. Output only: a failure is dropped, never fatal.
    /// </summary>
    internal async Task DrawLayoutOverlayAsync(GenerationSnapshot snapshot, ColonyProjection projection, HeldLayout layout, bool haveLayout, CancellationToken ct)
    {
        if (Native is not FacadeNativeClient native)
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
            // The plan holds the defense tier nodes (#3583): a new siting records a new plan, so the tick keys them too.
            var drawn = layout.Plan;
            var key = string.Create(CultureInfo.InvariantCulture, $"v2@{layout.Tick.Value}");
            if (key == OverlayKey && tick >= OverlayDrawn && tick.Value - OverlayDrawn.Value < LayoutPlanning.OverlayRedrawEvery.Value)
            {
                return;
            }
            var overlay = drawn.Overlay(projection.Bounds);
            if (StationHeld)
            {
                layout.Plan.RequirementOverlay(StationLast, projection.Bounds, overlay);
            }
            var (layer, fields) = LayoutOverlayRules.SplitFields(overlay);
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
    /// Lays a fresh v2 plan over the survey and the reported geysers from the station chain and records it,
    /// reading the resource demand the review derived for <paramref name="demandWorld"/>. It returns what the first
    /// plan holds the clock for: the fact the colony has not read (the layout_plan row is skipped/facts_unknown and
    /// nothing is recorded), <see cref="LayoutPlanning.FirstPlanPending"/> when the plan was recorded, and empty when
    /// the chain failed or the map holds no core (nothing is recorded and the next hourly review derives again).
    /// </summary>
    internal async Task<string> DeriveLayoutPlanAsync(GenerationSnapshot snapshot, GenerationSnapshot demandWorld, Tick tick, ColonyProjection projection, MapSurvey survey, DefinitionCatalog catalog, IReadOnlyList<PowerGeyser> geysers, PerimeterWall wall, CancellationToken ct)
    {
        projection.Facts.Colonists.TryGetValue(out var pawns);
        var colonists = (int)pawns;
        var start = DateTimeOffset.UtcNow;
        var f = StationFactsBuilder.FirstPlanFacts(projection, survey, StationNeeds(demandWorld), WorkLedgerView());
        f = f with { Crops = FieldCrops.Of(projection), FoodReserveDays = Seasonal(projection.Facts).FoodTargetDays, BedMerge = await BedMergeFactsAsync(snapshot, projection, ct).ConfigureAwait(false) };
        Fact<LayoutPlan> fact = default;
        StationAdvice advice = new();
        Exception? error = null;
        try
        {
            f = StationFactsBuilder.StationFacts(catalog, f);
            (fact, advice) = LayoutDeriveRules.DeriveLayoutPlan(survey, f, geysers);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = e;
        }
        var row = StationAdviceLog.StationOutcome(advice, error).Row;
        row = row with { Dur = DateTimeOffset.UtcNow - start, Attrs = new Dictionary<string, object?>(row.Attrs) { ["derive"] = true } };
        Tel.Decide(row);
        LayoutPlanning.LogInterior(advice);
        LayoutPlanning.LogConduits(advice);
        switch (error)
        {
            case StationFactsException unread:
                Tel.Decide(LayoutPlanning.LayoutPlanDecision("skipped", "facts_unknown", colonists, "", new Dictionary<string, object?> { ["fact"] = error.Message }));
                return unread.Fact;
            case not null:
                Tel.Decide(LayoutPlanning.LayoutPlanDecision("failed", "chain_error", colonists, "", new Dictionary<string, object?> { ["error"] = error.ToString() }));
                return "";
        }
        if (!fact.TryGetValue(out var plan))
        {
            Tel.Decide(LayoutPlanning.LayoutPlanDecision("skipped", "no_core", colonists, "", null));
            return "";
        }
        await Player.Journal.RecordLayoutPlanAsync(snapshot, tick, plan, ct).ConfigureAwait(false);
        Tel.Decide(LayoutPlanning.LayoutPlanDecision("planned", "", colonists, plan.Summary(), null));
        LayoutPlanning.LogPerimeter(plan, colonists);
        ColonyPlanCut.LogPerimeterCut(plan, survey, wall, colonists);
        return LayoutPlanning.FirstPlanPending;
    }

    /// <summary>Redraws a "heat.&lt;layer&gt;" overlay layer per traffic layer from the census's busiest cells, on its own hourly cadence; with the overlay off it removes them once. Output only, like the layout.</summary>
    internal async Task DrawHeatOverlayAsync(FacadeNativeClient native, GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
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
            foreach (var layer in TrafficRules.TrafficLayers)
            {
                var heat = on && census is not null ? TrafficRules.TrafficOverlay(census.Traffic, layer, projection.Bounds) : new LayoutOverlay();
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
