using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// The station chain's facts over the live colony at a layout review and what they key (<c>LayoutInputs</c>):
    /// the facts' key parts and the load beside the survey. Pure and cheap; the chain itself runs once, inside the
    /// replan the inputs' drift starts (<c>StartLayoutReplan</c>), off the review's hold path.
    /// </summary>
    internal (StationFacts Facts, LayoutInputs Inputs) StationInputsOf(GenerationSnapshot snapshot, ColonyProjection projection, LayoutPlan plan, RoomGrowth growth, MapSurvey survey, BedMergeFacts bedMerge, IReadOnlyDictionary<string, double>? hostBuild = null)
    {
        var f = StationFactsBuilder.StationFactsOf(projection, plan, survey, StationNeeds(snapshot), WorkLedgerView()) with
        {
            BedMerge = bedMerge,
            HostBuild = hostBuild ?? new Dictionary<string, double>(),
            Pinned = LayoutPlan.TilePinned(plan, growth),
            Held = LayoutPlan.HeldRooms(plan, growth),
            ThroneArea = growth.ThroneArea,
            Demand = growth.Demand,
            Child = LayoutChildRoomsRules.ChildRoomShapes(RoundsSleepingUpkeepPlanner.ChildRoomNeeds(projection), RoundsSleepingUpkeepPlanner.FurnitureDefinitions(projection)),
            Crops = FieldCrops.Of(projection),
            FoodReserveDays = Seasonal(projection.Facts).FoodTargetDays,
            Suites = RoundsSleepingUpkeepPlanner.SuiteWishesOf(projection, plan) is { } wishes ? [.. wishes] : [],
        };
        var parts = new Dictionary<string, string>(StringComparer.Ordinal) { ["load"] = string.Create(CultureInfo.InvariantCulture, $"{snapshot.Load.Value}") };
        foreach (var (name, value) in f.InputsKeyParts())
        {
            parts[name] = value;
        }
        return (f, new LayoutInputs(default, string.Create(CultureInfo.InvariantCulture, $"{snapshot.Load.Value}|{f.InputsKey()}"), survey.Bounds, [.. survey.Cells]) { Parts = parts });
    }

    /// <summary>
    /// The bed merge's facts over the live colony (#3658, #3748): the sleepers' gains per Impressiveness stage and the
    /// catalog's stages (LayoutClusterRules.SleeperGainsOf), and the capability-days and work to build of one wall cell and one
    /// door of a bedroom's ring (BedMergeShell), each priced by LayoutClusterRules.PiecePrice from its WorkToBuild for its
    /// stuff and cost list (LayoutClusterRules.BedMergeFactsOf). A piece the catalog shows no work or cost list for is
    /// unread, which the chain waits on (#3747).
    /// </summary>
    internal async Task<BedMergeFacts> BedMergeFactsAsync(GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
    {
        var sleepers = LayoutClusterRules.SleeperGainsOf(BedroomGainFacts.Of(projection.Facts), projection.Impressiveness);
        var (wall, door) = BedMergeShell(projection);
        var materials = new[] { wall, door }.SelectMany(p => p?.Costs ?? []).Select(a => a.Resource);
        var values = await MaterialValuesAsync(snapshot, materials, projection.Stations, projection.Facts.Items, ct).ConfigureAwait(false);
        return LayoutClusterRules.BedMergeFactsOf(sleepers, projection.Impressiveness, wall, door, values);
    }

    /// <summary>
    /// One wall cell and one door of a bedroom's ring (#3674): the Wall and the Door in the stuff a planned room's base ring
    /// sites are read in (the first of PlannedRingStuffsOf: the cheapest stocked, else the map's wood where trees stand,
    /// else its rock's blocks, #3747), the door in the wall's stuff as the planned sites build it. Either is null where the
    /// catalog shows no work or cost list for it.
    /// </summary>
    internal static ((double Work, IReadOnlyList<Amount> Costs)? Wall, (double Work, IReadOnlyList<Amount> Costs)? Door) BedMergeShell(ColonyProjection projection)
    {
        var (wallDef, doorDef) = (ContainmentCellRules.ShellWallDefinition, ContainmentCellRules.ShellDoorDefinition);
        var stuffs = PlannedRingStuffsOf(projection, wallDef, doorDef);
        return stuffs.Count == 0 ? (null, null) : (ShellPiece(projection, wallDef, stuffs[0]), ShellPiece(projection, doorDef, stuffs[0]));
    }

    /// <summary>
    /// The capability-days of building each work station that hosts a recipe the ledger orders (#3659, StationFacts.HostBuild):
    /// its WorkToBuild and cost list, a stuffed station's in its cheapest common stuff, priced by LayoutClusterRules.PiecePrice.
    /// A station the catalog shows no work or cost list for is left out, which leaves its recipe's hosts unpriced.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, double>> HostBuildAsync(GenerationSnapshot snapshot, ColonyProjection projection, StationFacts facts, CancellationToken ct)
    {
        var output = new Dictionary<string, double>(StringComparer.Ordinal);
        if (facts.Ledger.Status is not (WorkRules.LedgerViewReconciled or WorkRules.LedgerViewInactive) || facts.Ledger.Orders.Count == 0)
        {
            return output;
        }
        var orders = facts.Ledger.Orders;
        var recipes = orders.Select(o => o.Recipe).ToHashSet(StringComparer.Ordinal);
        var pieces = new SortedDictionary<string, (double Work, IReadOnlyList<Amount> Costs)>(StringComparer.Ordinal);
        foreach (var (def, s) in facts.Catalog.Stations)
        {
            if (s.Capacity.Kind == CapacityKind.Workers && s.Capacity.Recipes.Any(r => recipes.Contains(r.Recipe)) && HostPiece(projection, def) is { } piece)
            {
                pieces[def] = piece;
            }
        }
        var values = await MaterialValuesAsync(snapshot, pieces.Values.SelectMany(p => p.Costs).Select(a => a.Resource), projection.Stations, projection.Facts.Items, ct).ConfigureAwait(false);
        foreach (var (def, piece) in pieces)
        {
            output[def] = LayoutClusterRules.PiecePrice(piece, values);
        }
        return output;
    }

    /// <summary>
    /// The colony's side of a pawn's bed (#3825, V6): free while a colonist bed stands vacant, else the capability-days of building
    /// the primary bed (its work and cost list in its cheapest common stuff, LayoutClusterRules.PiecePrice). Null while the sleeping
    /// census is unread or the catalog shows the bed no work or cost list, which the pawn value requires.
    /// </summary>
    internal async Task<PawnBedFacts?> PawnBedAsync(GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
    {
        var name = projection.Shapes.Furniture.PrimaryBed();
        if (projection.Facts.VacantColonistBeds is not { } vacant)
        {
            return null;
        }
        if (vacant.Count > 0)
        {
            return new(true, 0);
        }
        if (HostPiece(projection, name) is not { } piece)
        {
            return null;
        }
        var values = await MaterialValuesAsync(snapshot, piece.Costs.Select(a => a.Resource), projection.Stations, projection.Facts.Items, ct).ConfigureAwait(false);
        return new(false, LayoutClusterRules.PiecePrice(piece, values));
    }

    /// <summary>A work station's work to build and cost list: its own for a def made from no stuff, else its cheapest common stuff's (by cost value).</summary>
    private static (double Work, IReadOnlyList<Amount> Costs)? HostPiece(ColonyProjection colony, string name)
    {
        if (RoundsAnimalContainment.AnimalContainmentDefinition(colony.Definitions, name) is not { } def)
        {
            return null;
        }
        if (def.StuffOptions.Count == 0)
        {
            return ShellPiece(colony, name, "");
        }
        var cheapest = def.StuffOptions.Where(o => o.Common).OrderBy(o => o.Value).ThenBy(o => o.Stuff, StringComparer.Ordinal).FirstOrDefault();
        return cheapest is null ? null : ShellPiece(colony, name, cheapest.Stuff);
    }

    /// <summary>A shell piece's work to build and cost list in <paramref name="stuff"/> (its own for a def made from no stuff); null where the catalog shows neither.</summary>
    private static (double Work, IReadOnlyList<Amount> Costs)? ShellPiece(ColonyProjection colony, string name, string stuff)
    {
        if (RoundsAnimalContainment.AnimalContainmentDefinition(colony.Definitions, name) is not { } def)
        {
            return null;
        }
        if (def.StuffOptions.Count == 0)
        {
            return def.WorkToBuild.TryGetValue(out var own) && def.Costs.TryGetValue(out var costs) ? (own, costs) : null;
        }
        return def.StuffOptions.FirstOrDefault(o => o.Stuff == stuff) is { } option && option.Stats.TryGetValue(Bridge.Contracts.StatDefNames.StatWorkToBuild, out var work)
            ? (work, option.Costs) : null;
    }
}

internal static class StationAdviceLog
{
    /// <summary>
    /// The layout_replan_tiled row's attrs: the diff's counts per verdict and its rooms, the standing rooms with no node, and
    /// the standing and tiled room counts. Each room of the held plan carries <c>started</c>, whether it is in the run's
    /// pinned set <paramref name="started"/> (anything of ours on it); absent while the census is unknown (#3751).
    /// </summary>
    public static Dictionary<string, object?> TiledReplanAttrs(ReplanTileDiff diff, int built, int tiledRooms, EmbedReplan? replan, IReadOnlySet<int>? started)
    {
        var rooms = new List<Dictionary<string, object?>>(diff.Rooms.Count);
        foreach (var d in diff.Rooms)
        {
            var row = new Dictionary<string, object?>
            {
                ["room"] = d.Room,
                ["tiled"] = d.Tiled,
                ["role"] = Wire.Name(d.Role),
                ["verdict"] = d.Verdict,
                ["reason"] = d.Reason,
                ["built"] = d.Built,
            };
            if (d.ShortBy > 0)
            {
                row["short"] = d.ShortBy;
            }
            if (d.Identity != "")
            {
                row["id"] = d.Identity;
            }
            if (started is not null && d.Room != 0)
            {
                row["started"] = started.Contains(d.Room);
            }
            rooms.Add(row);
        }
        var unmatched = diff.Unmatched
            .Select(u => new Dictionary<string, object?> { ["room"] = u.Room, ["role"] = Wire.Name(u.Role), ["reason"] = u.Reason })
            .ToList();
        var attrs = new Dictionary<string, object?>
        {
            ["same"] = diff.Count(LayoutReplanTiledRules.TileDiffSame),
            ["moved"] = diff.Count(LayoutReplanTiledRules.TileDiffMoved),
            ["new"] = diff.Count(LayoutReplanTiledRules.TileDiffNew),
            ["dropped"] = diff.Count(LayoutReplanTiledRules.TileDiffDropped),
            ["rooms"] = rooms,
            ["unmatched_built"] = unmatched,
            ["built"] = built,
            ["tiled_rooms"] = tiledRooms,
        };
        if (replan is not null)
        {
            // The held arrangement of the unbuilt rooms against the new embedding (#3740), in score points (cost).
            attrs["held_score"] = replan.Held;
            attrs["tiled_score"] = replan.Tiled;
            attrs["held_rooms"] = replan.Rooms;
            attrs["replan_gain"] = PlanWeights.ReplanGain;
        }
        return attrs;
    }

    /// <summary>The layout_replan_tiled row's verdict and reason: the embedding's hysteresis verdict (#3740), else replanned/tiled_plan.</summary>
    public static (string Verdict, string Reason) TiledReplanVerdict(EmbedReplan? replan) =>
        replan is null ? ("replanned", "tiled_plan") : (replan.Verdict, replan.Reason);

    /// <summary>The layout_regroup row of one regrouping decision: a move applied (kind merge, split or move, the pair of nodes it rules and the total before and after, in score points) or the reason none was.</summary>
    public static Decision RegroupDecision(RegroupDecision d) => new()
    {
        Kind = "layout_regroup",
        Component = "layout",
        Verdict = d.Verdict,
        Reason = d.Reason,
        Target = "groups",
        Attrs = new Dictionary<string, object?>
        {
            ["move"] = d.Kind,
            ["a"] = d.A,
            ["b"] = d.B,
            ["before"] = d.Before,
            ["after"] = d.After,
            ["walk"] = d.Walk,
            ["poison"] = d.Poison,
            ["clean"] = d.Clean,
            ["lost_effects"] = d.Lost,
            ["effects"] = d.Effects,
        },
    };

    /// <summary>Runs the chain over <paramref name="facts"/> (when they were read, <paramref name="error"/> null) and is the layout_stations row of the outcome: advised or failed, or the required_fact row for an unread fact. The advice is returned when it was made.</summary>
    public static (Decision Row, StationAdvice Advice, bool Ok) StationDecision(StationFacts facts, Exception? error)
    {
        var advice = new StationAdvice();
        if (error is null)
        {
            try
            {
                advice = LayoutStationPlanRules.AdviseStations(facts);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                error = e;
            }
        }
        return StationOutcome(advice, error);
    }

    /// <summary>The layout_stations row of advice made (or <paramref name="error"/>, the chain's); an unread fact is the shared required_fact row (target layout_stations).</summary>
    public static (Decision Row, StationAdvice Advice, bool Ok) StationOutcome(StationAdvice advice, Exception? error)
    {
        var decision = new Decision { Kind = TelemetryKinds.LayoutStationsKind, Component = "layout", Target = "plan" };
        switch (error)
        {
            case RequiredFactException unread:
                decision = RequiredFactTelemetry.Skipped("layout", TelemetryKinds.LayoutStationsKind, unread.Fact, unread.Site);
                break;
            case not null:
                decision = decision with { Level = TelemetryLevel.Warn };
                decision = decision with { Verdict = "failed" };
                decision = decision with { Reason = "chain_error" };
                decision = decision with { Attrs = new Dictionary<string, object?> { ["error"] = error.ToString() } };
                break;
            default:
                decision = decision with { Verdict = "advised" };
                decision = decision with { Reason = "station_chain" };
                decision = decision with { Attrs = StationAdviceAttrs(advice) };
                return (decision, advice, true);
        }
        return (decision, new StationAdvice(), false);
    }

    /// <summary>The layout_stations row's attrs: each group with its stations, floor, predicted role, costs and the built rooms matched to it, the clustering's conflicts and the embedding's report.</summary>
    public static Dictionary<string, object?> StationAdviceAttrs(StationAdvice a)
    {
        var blocks = new Dictionary<int, Rectangle>();
        foreach (var b in a.Embedding.Blocks)
        {
            blocks[b.Group] = b.Interior;
        }
        var groups = new List<Dictionary<string, object?>>(a.Groups.Count);
        foreach (var g in a.Groups)
        {
            var stations = g.Group.Stations.Select(s => new Dictionary<string, object?> { ["node"] = s.Node, ["count"] = s.Count }).ToList();
            var requires = g.Group.Requires.Select(req => Wire.Name(req)).ToList();
            var costTicks = g.Group.Costs.Sum(c => c.TicksPerDay);
            var row = new Dictionary<string, object?>
            {
                ["group"] = g.Group.Id,
                ["role"] = Wire.Name(g.Group.Role),
                ["stations"] = stations,
                ["requires"] = requires,
                ["footprint"] = g.Group.Footprint,
                ["clearance"] = g.Group.Clearance,
                ["area"] = g.Group.Area,
                ["cost_ticks_per_day"] = costTicks,
                ["lost_effects"] = g.Group.LostEffects.Count,
                ["rooms"] = g.Rooms,
                ["built_area"] = g.Capacity,
                ["outgrown"] = g.Outgrown,
            };
            if (g.Climate is { } c)
            {
                var options = c.Options.Select(o => new Dictionary<string, object?>
                {
                    ["opening"] = Wire.Name(o.Opening),
                    ["mean_w"] = o.Watts.MeanW,
                    ["peak_w"] = o.Watts.PeakW,
                    ["heating_w"] = o.Watts.HeatingW,
                    ["cooling_w"] = o.Watts.CoolingW,
                    ["build_w"] = o.BuildW,
                    ["total_w"] = o.TotalW,
                }).ToList();
                row["climate"] = new Dictionary<string, object?> { ["cells"] = c.Cells, ["max_c"] = c.Band.MaxC, ["best"] = Wire.Name(c.Best), ["options"] = options };
            }
            if (blocks.TryGetValue(g.Group.Id, out var block))
            {
                row["block"] = new Dictionary<string, object?> { ["x"] = block.X, ["z"] = block.Z, ["width"] = block.Width, ["height"] = block.Height };
            }
            groups.Add(row);
        }
        var conflicts = new List<Dictionary<string, object?>>();
        var bedMerge = new List<Dictionary<string, object?>>();
        var issues = new List<Dictionary<string, object?>>();
        var built = new List<Dictionary<string, object?>>();
        foreach (var d in a.Clustering.Decisions)
        {
            if (d.Conflict != "")
            {
                conflicts.Add(new Dictionary<string, object?> { ["a"] = d.A, ["b"] = d.B, ["conflict"] = d.Conflict });
            }
            if (d.Reason.StartsWith("cluster_bed_merge", StringComparison.Ordinal))
            {
                bedMerge.Add(new Dictionary<string, object?> { ["node"] = d.A, ["verdict"] = d.Verdict, ["reason"] = d.Reason });
            }
        }
        foreach (var d in a.Embedding.Decisions)
        {
            if (d.Verdict is LayoutBuiltRules.EmbedKeep or LayoutBuiltRules.EmbedMove)
            {
                built.Add(new Dictionary<string, object?> { ["group"] = d.Group, ["room"] = d.Room, ["verdict"] = d.Verdict, ["reason"] = d.Reason });
            }
            else if (d.Verdict != LayoutEmbedRules.EmbedPlace)
            {
                issues.Add(new Dictionary<string, object?> { ["group"] = d.Group, ["other"] = d.Other, ["verdict"] = d.Verdict, ["reason"] = d.Reason, ["requirement"] = d.Requirement is { } requirement ? Wire.Name(requirement) : "", ["field"] = d.Field });
            }
        }
        var sites = new List<Dictionary<string, object?>>();
        foreach (var p in a.Sites.Sites)
        {
            var row = new Dictionary<string, object?> { ["node"] = p.Node, ["x"] = p.Origin.X, ["z"] = p.Origin.Z, ["anchored"] = p.Anchored };
            foreach (var l in p.Lanes)
            {
                if (l.Wind)
                {
                    row["rotation"] = p.Rotation is { } rotation ? Wire.Name(rotation) : "";
                    row["blocked"] = l.Blocked;
                    row["output"] = l.WindOutput();
                }
            }
            sites.Add(row);
        }
        var siteIssues = a.Sites.Decisions
            .Where(d => d.Verdict != LayoutEmbedRules.EmbedPlace)
            .Select(d => new Dictionary<string, object?> { ["node"] = d.Node, ["verdict"] = d.Verdict, ["reason"] = d.Reason })
            .ToList();
        var fields = a.Embedding.Fields.Select(f => new Dictionary<string, object?>
        {
            ["field"] = f.Id,
            ["x"] = f.Interior.X,
            ["z"] = f.Interior.Z,
            ["width"] = f.Interior.Width,
            ["height"] = f.Interior.Height,
            ["reason"] = LayoutFieldPlaceRules.ReasonEmbedFieldPlaced,
        }).ToList();
        // The tiled plan's vents and why a room has none.
        var vents = new List<Dictionary<string, object?>>();
        var ventIssues = new List<Dictionary<string, object?>>();
        var dropped = new List<Dictionary<string, object?>>();
        if (a.Tiled is not null)
        {
            // The rooms the tiler left out because the placer could not furnish them (#3745); their groups stay owed.
            foreach (var d in a.Tiled.Decisions.Where(d => d.Verdict == LayoutTileRules.TileDropped))
            {
                dropped.Add(new Dictionary<string, object?> { ["group"] = d.Group, ["verdict"] = d.Verdict, ["reason"] = d.Reason, ["why"] = d.Why });
            }
            foreach (var v in a.Tiled.Plan.LiveSites(LayoutTileVentRules.NodeVent))
            {
                vents.Add(new Dictionary<string, object?> { ["x"] = v.Origin.X, ["z"] = v.Origin.Z, ["rotation"] = v.Rotation is { } ventRotation ? Wire.Name(ventRotation) : "", ["reason"] = v.Reason });
            }
            foreach (var d in a.Tiled.Decisions)
            {
                if (d.Reason.StartsWith("tile_vent", StringComparison.Ordinal) && d.Verdict != LayoutTileRules.TilePlace)
                {
                    ventIssues.Add(new Dictionary<string, object?> { ["group"] = d.Group, ["verdict"] = d.Verdict, ["reason"] = d.Reason });
                }
            }
        }
        var comfort = a.Comfort.Select(c => new Dictionary<string, object?>
        {
            ["node"] = c.Node,
            ["def"] = c.Option.Def,
            ["stuff"] = c.Option.Stuff,
            ["comfort"] = c.Option.Comfort,
            ["priced"] = c.Priced,
            ["gain"] = c.Gain,
            ["benefit_w"] = c.BenefitW,
            ["cost_w"] = c.CostW,
        }).ToList();
        var s = a.Embedding.Score;
        return new Dictionary<string, object?>
        {
            ["nodes"] = a.Graph.Nodes.Count,
            ["edges"] = a.Graph.Edges.Count,
            ["groups"] = groups,
            ["conflicts"] = conflicts,
            ["bed_merge"] = bedMerge,
            ["sites"] = sites,
            ["site_issues"] = siteIssues,
            ["vents"] = vents,
            ["vent_issues"] = ventIssues,
            ["dropped"] = dropped,
            ["comfort"] = comfort,
            ["embed"] = new Dictionary<string, object?>
            {
                ["level"] = a.Embedding.ObstacleLevel(),
                ["placed"] = a.Embedding.Blocks.Count,
                ["issues"] = issues,
                ["fields"] = fields,
                ["cells"] = s.Cells,
                ["total"] = s.Total(),
                ["soil"] = s.Soil,
                ["edge"] = s.Edge,
                ["centre"] = s.Centre,
                ["walk"] = s.Walk,
                ["enclosure"] = s.Enclosure,
                ["unmet_links"] = s.UnmetLinks,
                ["switch"] = s.Switch,
                ["built"] = built,
                ["seed"] = string.Create(CultureInfo.InvariantCulture, $"{a.Embedding.Seed.X},{a.Embedding.Seed.Z}"),
            },
        };
    }
}

internal static class StationFactsBuilder
{
    /// <summary>Adds the catalog rows (recipe flows, stack limits, rot) to <paramref name="f"/>, the facts the colony read. A part the colony has not read stays unknown.</summary>
    public static StationFacts StationFacts(DefinitionCatalog catalog, StationFacts f)
    {
        var recipes = catalog.StationRecipeFlows(f.Catalog);
        var stock = f.Stock.TryGetValue(out var held) ? held : new Dictionary<Resource, long>();
        var (stackLimit, perishable) = catalog.StationItemFacts(stock, recipes, LayoutPenNodesRules.PenItems(f.Herds, f.Races));
        return f with { Recipes = recipes, StackLimit = stackLimit, Perishable = perishable };
    }

    /// <summary>
    /// Every station fact the projection, the resource demand and the work ledger hold; the catalog rows (recipe
    /// flows, stack limits, rot) are added by the caller. A demand a review derived for this world is known even when
    /// empty: it targets no stock.
    /// </summary>
    public static StationFacts StationFactsOf(ColonyProjection projection, LayoutPlan plan, MapSurvey survey, Fact<IReadOnlyDictionary<Resource, long>> needs, LedgerView ledger)
    {
        var f = new StationFacts
        {
            Catalog = projection.Stations,
            Furniture = projection.Shapes.Furniture,
            Items = projection.Facts.Items,
            Colonists = projection.Facts.Colonists,
            Patients = projection.Facts.MedicalPawns,
            Surgical = SurgeryRules.SurgeryBedShortPatients(projection.Facts.MedicalPawns),
            GoalBuild = projection.Facts.GoalBuild,
            Prisoners = Unknown<int>(),
            Herds = projection.Facts.PenHerds(),
            Races = projection.Facts.AnimalUpkeep.AnimalRaces,
            Pasture = projection.Facts.Pasture,
            WorkPawns = projection.WorkPawns,
            Rest = projection.Rest,
            Food = projection.FoodSupply,
            Ledger = ledger,
            Stock = needs,
            RoofDistance = projection.RoofSupport,
            Plan = plan,
            Survey = survey,
            OutdoorCurve = projection.OutdoorCurve,
            FullRotRateC = projection.FullRotRateC,
            VentAvailable = projection.DefinitionAvailable(ClimateRules.ClimateDefs().VentDef),
            ExpectedMood = ExpectedMood(projection.Facts.MoodPawns),
        };
        try
        {
            // an unread or inconsistent census leaves the guard off
            f = f with { Mismatches = RoundsBuildingPlanner.RoleMismatches(projection) };
        }
        catch (DomainException)
        {
        }
        if (projection.Rooms.TryGetValue(out var rooms))
        {
            f = f with { Dining = rooms.Dining };
        }
        if (RoundsBuildingPlanner.ColonyGround(projection) is (var ground, true))
        {
            f = f with { Built = LayoutPlan.BuiltRooms(plan, ground), ShellStanding = LayoutPlan.ShellStanding(plan, ground) };
        }
        if (projection.Facts.Population.TryGetValue(out var census))
        {
            f = f with { Prisoners = Known(PrisonersRounds.HeldPrisoners(census.Prisoners)) };
        }
        return f;
    }

    /// <summary>
    /// The station facts of a first plan over <paramref name="survey"/> (<c>DeriveLayoutPlan</c>): every fact the
    /// projection, the resource demand needs and the work ledger hold, nothing standing, the throne room a title
    /// asks (ThroneArea) and what the colony has not read left unknown, which the plan waits for. The catalog rows
    /// are added by the caller (<see cref="StationFacts"/>).
    /// </summary>
    public static StationFacts FirstPlanFacts(ColonyProjection projection, MapSurvey survey, Fact<IReadOnlyDictionary<Resource, long>> needs, LedgerView ledger)
    {
        var f = StationFactsOf(projection, new LayoutPlan(), survey, needs, ledger) with { Derive = true };
        var (need, owed) = RoundsSleepingUpkeepPlanner.ThroneNeed(projection);
        return f with { ThroneArea = ThroneRules.ThroneAreaOwed(new LayoutPlan(), need, owed) };
    }

    /// <summary>
    /// The colonists' mean mood level, each colonist at the level its thoughts forecast (Target): the mood price's
    /// baseline. Unknown while any colonist has no mood need, so the comfort tiers go unpriced rather than priced on
    /// part of the colony.
    /// </summary>
    public static Fact<double> ExpectedMood(Fact<EquatableArray<MoodPawn>> pawns)
    {
        if (!pawns.TryGetValue(out var rows) || rows.Count == 0)
        {
            return Unknown<double>();
        }
        var sum = 0.0;
        foreach (var p in rows)
        {
            if (p.Mood is not MoodLevel.Has { Target: var target })
            {
                return Unknown<double>();
            }
            sum += target;
        }
        return Known(sum / rows.Count);
    }
}

internal static class FieldCrops
{
    /// <summary>The crops the colony's growing zones are sown with, each once, in plant-definition order: the field nodes of the station graph follow the crops the field planner has chosen, never a choice of the chain's.</summary>
    public static List<CropChoice> Growing(ColonyProjection projection)
    {
        var sown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var farm in projection.Farms)
        {
            if (farm.Crop != "")
            {
                sown.Add(farm.Crop);
            }
        }
        return RoundsFieldPlanner.CropChoices(projection).Where(choice => sown.Contains(choice.Name)).ToList();
    }

    /// <summary>
    /// The crops the plan's fields are sized for: the sown crops, and while none is sown the first of the default
    /// table the catalog offers, because a field node exists only for a crop and a colony that sows nothing would
    /// never get a field to sow.
    /// </summary>
    public static IReadOnlyList<CropChoice> Of(ColonyProjection projection)
    {
        var sown = Growing(projection);
        if (sown.Count > 0)
        {
            return sown;
        }
        return LayoutFieldReplanRules.DefaultFieldCrop(RoundsFieldPlanner.CropChoices(projection)) is ({ } crop, true) ? [crop] : [];
    }
}
