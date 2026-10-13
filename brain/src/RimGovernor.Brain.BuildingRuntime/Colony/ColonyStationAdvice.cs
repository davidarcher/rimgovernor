using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// Runs the station chain over the live colony at a layout review and keeps its advice (<c>_stationLast</c>,
    /// held when the chain made one) and the facts it read (<c>_stationFacts</c>), which the replan tiles. The
    /// chain is skipped, and the last advice kept, while its inputs are unchanged: the facts' key and the survey,
    /// as for the replan. A run records one layout_stations decision.
    /// </summary>
    internal void StationAdviceFor(GenerationSnapshot snapshot, ColonyProjection projection, LayoutPlan plan, IReadOnlySet<int> pinned, RoomGrowth growth, RoomDemand demand, MapSurvey survey, DefinitionCatalog catalog)
    {
        var f = StationFactsBuilder.StationFactsOf(projection, plan, survey, StationNeeds(snapshot), WorkLedgerView()) with
        {
            Pinned = pinned,
            ThroneArea = Known(growth.ThroneArea),
            RetireShelter = growth.RetireShelter,
            Demand = demand,
            Child = PolicyFunctions.ChildRoomShapes(ChildNeeds.ChildRoomNeeds(projection), FurnitureDefinitions.Of(projection)),
            Crops = FieldCrops.Of(projection),
            FoodReserveDays = Seasonal(projection.Facts).FoodTargetDays,
            Suites = SuiteWishes.Of(projection, plan, _stage),
        };
        var inputs = new LayoutInputs(default, string.Create(CultureInfo.InvariantCulture, $"{snapshot.Load.Value}|{f.InputsKey()}"), survey.Bounds, survey.Cells);
        if (inputs.Same(_stationInputs))
        {
            return;
        }
        var start = DateTimeOffset.UtcNow;
        StationFacts facts = f;
        Exception? error = null;
        try
        {
            facts = StationFactsBuilder.StationFacts(catalog, f);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = e;
        }
        var (decision, advice, ok) = StationAdviceLog.StationDecision(facts, error);
        decision.Dur = DateTimeOffset.UtcNow - start;
        Tel.Decide(decision);
        foreach (var d in advice.Regroup)
        {
            Tel.Decide(StationAdviceLog.RegroupDecision(d));
        }
        _stationInputs = inputs;
        _stationLast = advice;
        _stationHeld = ok;
        _stationFacts = facts;
    }
}

internal static class StationAdviceLog
{
    /// <summary>The layout_replan_tiled row's attrs: the diff's counts per verdict and its rooms, the standing rooms with no node, and the standing and tiled room counts.</summary>
    public static Dictionary<string, object?> TiledReplanAttrs(ReplanTileDiff diff, int built, int tiledRooms)
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
            if (d.Short > 0)
            {
                row["short"] = d.Short;
            }
            rooms.Add(row);
        }
        var unmatched = diff.Unmatched
            .Select(u => new Dictionary<string, object?> { ["room"] = u.Room, ["role"] = Wire.Name(u.Role), ["reason"] = u.Reason })
            .ToList();
        return new Dictionary<string, object?>
        {
            ["same"] = diff.Count(TileDiffVerdict.Same),
            ["moved"] = diff.Count(TileDiffVerdict.Moved),
            ["new"] = diff.Count(TileDiffVerdict.New),
            ["dropped"] = diff.Count(TileDiffVerdict.Dropped),
            ["rooms"] = rooms,
            ["unmatched_built"] = unmatched,
            ["built"] = built,
            ["tiled_rooms"] = tiledRooms,
        };
    }

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
        },
    };

    /// <summary>Runs the chain over <paramref name="facts"/> (when they were read, <paramref name="error"/> null) and is the layout_stations row of the outcome: advised, skipped for an unknown fact, or failed. The advice is returned when it was made.</summary>
    public static (Decision Row, StationAdvice Advice, bool Ok) StationDecision(StationFacts facts, Exception? error)
    {
        var advice = new StationAdvice();
        if (error is null)
        {
            try
            {
                advice = PolicyFunctions.AdviseStations(facts);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                error = e;
            }
        }
        return StationOutcome(advice, error);
    }

    /// <summary>The layout_stations row of advice made (or <paramref name="error"/>, the chain's).</summary>
    public static (Decision Row, StationAdvice Advice, bool Ok) StationOutcome(StationAdvice advice, Exception? error)
    {
        var decision = new Decision { Kind = "layout_stations", Component = "layout", Target = "plan" };
        switch (error)
        {
            case StationFactsException:
                decision.Verdict = "skipped";
                decision.Reason = "facts_unknown";
                decision.Attrs = new Dictionary<string, object?> { ["fact"] = error.Message };
                break;
            case not null:
                decision.Level = TelemetryLevel.Warn;
                decision.Verdict = "failed";
                decision.Reason = "chain_error";
                decision.Attrs = new Dictionary<string, object?> { ["error"] = error };
                break;
            default:
                decision.Verdict = "advised";
                decision.Reason = "station_chain";
                decision.Attrs = StationAdviceAttrs(advice);
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
        var issues = new List<Dictionary<string, object?>>();
        var built = new List<Dictionary<string, object?>>();
        foreach (var d in a.Clustering.Decisions)
        {
            if (d.Conflict != "")
            {
                conflicts.Add(new Dictionary<string, object?> { ["a"] = d.A, ["b"] = d.B, ["conflict"] = d.Conflict });
            }
        }
        foreach (var d in a.Embedding.Decisions)
        {
            if (d.Verdict is PolicyFunctions.EmbedKeep or PolicyFunctions.EmbedMove)
            {
                built.Add(new Dictionary<string, object?> { ["group"] = d.Group, ["room"] = d.Room, ["verdict"] = d.Verdict, ["reason"] = d.Reason });
            }
            else if (d.Verdict != PolicyFunctions.EmbedPlace)
            {
                issues.Add(new Dictionary<string, object?> { ["group"] = d.Group, ["other"] = d.Other, ["verdict"] = d.Verdict, ["reason"] = d.Reason, ["requirement"] = Wire.Name(d.Requirement), ["field"] = d.Field });
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
                    row["rotation"] = Wire.Name(p.Rotation);
                    row["blocked"] = l.Blocked;
                    row["output"] = l.WindOutput();
                }
            }
            sites.Add(row);
        }
        var siteIssues = a.Sites.Decisions
            .Where(d => d.Verdict != PolicyFunctions.EmbedPlace)
            .Select(d => new Dictionary<string, object?> { ["node"] = d.Node, ["verdict"] = d.Verdict, ["reason"] = d.Reason })
            .ToList();
        var fields = a.Embedding.Fields.Select(f => new Dictionary<string, object?>
        {
            ["field"] = f.Id,
            ["x"] = f.Interior.X,
            ["z"] = f.Interior.Z,
            ["width"] = f.Interior.Width,
            ["height"] = f.Interior.Height,
            ["reason"] = PolicyFunctions.ReasonEmbedFieldPlaced,
        }).ToList();
        // The tiled plan's vents and why a room has none.
        var vents = new List<Dictionary<string, object?>>();
        var ventIssues = new List<Dictionary<string, object?>>();
        if (a.Tiled is not null)
        {
            foreach (var v in a.Tiled.Plan.LiveSites(PolicyFunctions.NodeVent))
            {
                vents.Add(new Dictionary<string, object?> { ["x"] = v.Origin.X, ["z"] = v.Origin.Z, ["rotation"] = Wire.Name(v.Rotation), ["reason"] = v.Reason });
            }
            foreach (var d in a.Tiled.Decisions)
            {
                if (d.Reason.StartsWith("tile_vent", StringComparison.Ordinal) && d.Verdict != PolicyFunctions.TilePlace)
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
            ["seed_only"] = a.SeedOnly,
            ["sites"] = sites,
            ["site_issues"] = siteIssues,
            ["vents"] = vents,
            ["vent_issues"] = ventIssues,
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
        var (stackLimit, perishable) = catalog.StationItemFacts(stock, recipes, PolicyFunctions.PenItems(f.Herds, f.Races));
        return f with { Recipes = recipes, StackLimit = stackLimit, Perishable = perishable };
    }

    /// <summary>
    /// Every station fact the projection, the resource demand and the work ledger hold; the catalog rows (recipe
    /// flows, stack limits, rot) are added by the caller.
    /// </summary>
    public static StationFacts StationFactsOf(ColonyProjection projection, LayoutPlan plan, MapSurvey survey, IReadOnlyDictionary<Resource, long> needs, LedgerView ledger)
    {
        var f = new StationFacts
        {
            Catalog = projection.Stations,
            Furniture = projection.Shapes.Furniture,
            Items = projection.Facts.Items,
            Colonists = projection.Facts.Colonists,
            Patients = projection.Facts.MedicalPawns,
            Surgical = PolicyFunctions.SurgeryBedShortPatients(projection.Facts.MedicalPawns),
            Prisoners = Unknown<int>(),
            Animals = projection.Facts.PenAnimals(),
            Herds = projection.Facts.PenHerds(),
            Races = projection.Facts.AnimalUpkeep.AnimalRaces,
            Pasture = projection.Facts.Pasture,
            WorkPawns = projection.WorkPawns,
            Food = projection.FoodSupply,
            Orders = Unknown<IReadOnlyList<OrderSpec>>(),
            Stock = Unknown<IReadOnlyDictionary<Resource, long>>(),
            RoofDistance = projection.RoofSupport,
            Plan = plan,
            Survey = survey,
            OutdoorCurve = projection.OutdoorCurve,
            FullRotRateC = projection.FullRotRateC,
            VentAvailable = projection.DefinitionAvailable(PolicyFunctions.ClimateDefs().VentDef),
            ExpectedMood = ExpectedMood(projection.Facts.MoodPawns),
        };
        try
        {
            // an unread or inconsistent census leaves the guard off
            f = f with { Mismatches = Roles.RoleMismatches(projection) };
        }
        catch (DomainException)
        {
        }
        if (projection.Rooms.TryGetValue(out var rooms))
        {
            f = f with { Dining = rooms.Dining };
        }
        if (ColonyGround.TryOf(projection, out var ground))
        {
            f = f with { Built = PolicyFunctions.BuiltRooms(plan, ground) };
        }
        if (projection.Facts.Prisoners.TryGetValue(out var prisoners))
        {
            f = f with { Prisoners = Known(Prisoners.HeldPrisoners(prisoners)) };
        }
        if (needs.Count > 0)
        {
            f = f with { Stock = Known(needs) };
        }
        // An inactive ledger (no planner declares into it) wants no order: a known empty list, where an
        // unreviewed or unread one is unknown.
        if (ledger.Status is LedgerViewStatus.Reconciled or LedgerViewStatus.Inactive)
        {
            var orders = ledger.Orders.Select(o => new OrderSpec
            {
                Recipe = o.Recipe,
                Ingredients = o.Ingredients,
                Worker = o.Worker,
                Mode = (BillMode)o.Mode,
                Target = o.Target,
                BenchKind = o.BenchKind,
                Product = new Resource(o.Product),
            }).ToList();
            f = f with { Orders = Known<IReadOnlyList<OrderSpec>>(orders) };
        }
        return f;
    }

    /// <summary>
    /// The station facts of a first plan over <paramref name="survey"/> (<c>DeriveLayoutPlan</c>): every fact the
    /// projection, the resource demand needs and the work ledger hold, nothing standing, the throne room a title
    /// asks (ThroneArea) and what the colony has not read left unknown, which a first plan grows from the seed
    /// rooms. The catalog rows are added by the caller (<see cref="StationFacts"/>).
    /// </summary>
    public static StationFacts FirstPlanFacts(ColonyProjection projection, MapSurvey survey, IReadOnlyDictionary<Resource, long> needs, LedgerView ledger)
    {
        var f = StationFactsOf(projection, new LayoutPlan(), survey, needs, ledger) with { Derive = true };
        var (need, owed) = Throne.ThroneNeed(projection);
        return f with { ThroneArea = Known(PolicyFunctions.ThroneAreaOwed(new LayoutPlan(), need, owed)) };
    }

    /// <summary>
    /// The colonists' mean mood level, each colonist at the level its thoughts forecast (Target) or else its
    /// current one: the mood price's baseline. Unknown while any colonist has neither, so the comfort tiers go
    /// unpriced rather than priced on part of the colony.
    /// </summary>
    public static Fact<double> ExpectedMood(Fact<IReadOnlyList<MoodPawn>> pawns)
    {
        if (!pawns.TryGetValue(out var rows) || rows.Count == 0)
        {
            return Unknown<double>();
        }
        var sum = 0.0;
        foreach (var p in rows)
        {
            if (!p.Target.TryGetValue(out var level) && !p.Mood.TryGetValue(out level))
            {
                return Unknown<double>();
            }
            sum += level;
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
        return Fields.CropChoices(projection).Where(choice => sown.Contains(choice.Name)).ToList();
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
        return PolicyFunctions.DefaultFieldCrop(Fields.CropChoices(projection)) is { } crop ? [crop] : [];
    }
}
