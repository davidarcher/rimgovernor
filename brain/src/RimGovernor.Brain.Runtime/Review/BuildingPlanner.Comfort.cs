using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed partial class BuildingPlanner
{
    /// <summary>The rooms/hosting Need's planner: hosting room furniture and proof of use.</summary>
    public static BuildingPlanner NewReviewComfortPlanner(ColonyReview reviewer, IReviewBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewComfortPlanner: reviewer == nil || native == nil");
        }
        if (native is not IReviewSource)
        {
            throw new ControlException("NewReviewComfortPlanner: !ok");
        }
        return new BuildingPlanner { Reviewer = reviewer, Native = native, Concern = RoomsFamily.Hosting.FilingKey, Need = RoomsFamily.Hosting, Phase = Pol.Phase.ComfortRanked };
    }

    /// <summary>The rooms/party-spot Need's planner: one PartySpot placed in, or retired from, the shared rooms.</summary>
    public static BuildingPlanner NewReviewPartySpotPlanner(ColonyReview reviewer, IReviewBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewPartySpotPlanner: reviewer or native is missing");
        }
        if (native is not IReviewSource)
        {
            throw new ControlException("NewReviewPartySpotPlanner: native is not a rounds source");
        }
        return new BuildingPlanner
        {
            Reviewer = reviewer, Native = native, Concern = RoomsFamily.PartySpot.FilingKey, Need = RoomsFamily.PartySpot, Phase = Pol.Phase.ComfortSpot,
            Definition = PartySpotRules.PartySpotDefinition, Environment = Pol.PlacementEnvironment.Indoors,
        };
    }

    /// <summary>The rooms/work-chairs Need's planner: the work chair the plan chose for a standing bench, on its interaction cell.</summary>
    public static BuildingPlanner NewReviewWorkChairPlanner(ColonyReview reviewer, IReviewBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewWorkChairPlanner: reviewer or native is missing");
        }
        if (native is not IReviewSource)
        {
            throw new ControlException("NewReviewWorkChairPlanner: native is not a rounds source");
        }
        return new BuildingPlanner { Reviewer = reviewer, Native = native, Concern = RoomsFamily.WorkChairs.FilingKey, Need = RoomsFamily.WorkChairs, Phase = Pol.Phase.ComfortWorkChair };
    }

    /// <summary>
    /// The planner resolved to the first owed work chair (WorkChairRules.OwedSeats): its chair definition in the
    /// colony's build stuff, placed only on the bench's interaction cell. No seat owed is no deficit.
    /// </summary>
    internal (BuildingPlanner? Planner, Verdict Verdict) SelectWorkChair(ColonyReading facts)
    {
        if (facts.Facts.LayoutPlan is not { } plan)
        {
            return (null, FieldUnavailable("layout_plan"));
        }
        var census = facts.Facts.CurrentConstruction;
        var seats = WorkChairRules.OwedSeats(plan, census, facts.Facts.Shapes);
        if (seats.Count == 0)
        {
            return (null, BuildingReasonNoDeficit);
        }
        var seat = seats[0];
        var resolved = Copy();
        resolved.Definition = seat.Chair;
        resolved.Cells = [seat.Cell];
        resolved.Adjacent = [seat.Cell];
        resolved.Environment = Pol.PlacementEnvironment.Anywhere;
        resolved.Stuff = ColonyPlanningRules.BuildStuff(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), seat.Chair);
        return (resolved, default);
    }

    /// <summary>The rooms/comfort Need's planner: a reachable table, seat and recreation source.</summary>

    public static BuildingPlanner NewReviewBasicComfortPlanner(ColonyReview reviewer, IReviewBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewBasicComfortPlanner: reviewer == nil || native == nil");
        }
        if (native is not IReviewSource)
        {
            throw new ControlException("NewReviewBasicComfortPlanner: !ok");
        }
        return new BuildingPlanner { Reviewer = reviewer, Native = native, Concern = RoomsFamily.Comfort.FilingKey, Need = RoomsFamily.Comfort, Phase = Pol.Phase.ComfortBasic };
    }

    /// <summary>The planner resolved to the comfort furniture the ranked review selects, or the verdict that explains why none is wanted.</summary>
    internal (BuildingPlanner? Planner, Verdict Verdict) SelectComfort(ColonyReading facts, ComfortHistory history)
    {
        var v = facts.Facts.RequireComfort();
        var review = ComfortRules.ReviewComfort(v, history, facts.Identity.Tick);
        var method = ComfortRules.SelectComfortMethod(v, review);
        if (method == Pol.ComfortMethod.NoMethod)
        {
            return (null, BuildingReasonNoDeficit);
        }
        if (method == Pol.ComfortMethod.Wait)
        {
            return (null, BuildingReasonComfortWait);
        }
        if (method == Pol.ComfortMethod.AccessBlocked)
        {
            return (null, ComfortAccessWait(review.Dining));
        }
        var resolved = Copy();
        resolved.Definition = method.Value;
        resolved.Environment = Pol.PlacementEnvironment.Indoors;
        if (v.Furniture.IsChair(method.Value))
        {
            var adjacent = new List<Cell>(Adjacent);
            foreach (var s in v.Census.Surfaces)
            {
                adjacent.AddRange(s.Adjacent);
            }
            resolved.Adjacent = adjacent;
        }
        resolved.Stuff = ColonyPlanningRules.BuildStuff(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), resolved.Definition);
        return (resolved, default);
    }

    /// <summary>The planner resolved to the basic comfort building the review selects (a chair, table or joy building).</summary>
    internal (BuildingPlanner? Planner, Verdict Verdict) SelectBasicComfort(ColonyReading facts)
    {
        var v = facts.Facts.RequireBasicComfort();
        var review = BasicComfortRules.ReviewBasicComfort(v);
        var method = BasicComfortRules.SelectBasicComfortMethod(v, review);

        if (method == Pol.ComfortMethod.NoMethod && review.MissingVariety > 0)
        {
            method = RecreationRules.SelectRecreationVariety(v, m =>
            {
                if (m.PowerW > 0)
                {
                    var canSite = false;
                    foreach (var c in facts.PlanningWindow.Cells)
                    {
                        canSite = canSite || (c.Roofed && c.Indoors && !c.Occupied() && PoweredRecreationCell(facts, c.Cell, m.PowerW));
                    }
                    if (!canSite)
                    {
                        return false;
                    }
                }
                return ComfortBuilderAvailable(facts, m.Definition);
            });
        }
        if (method == Pol.ComfortMethod.NoMethod)
        {
            return (null, BuildingReasonNoDeficit);
        }
        if (method == Pol.ComfortMethod.AccessBlocked)
        {
            return (null, ComfortAccessWait(review.Dining));
        }
        var resolved = Copy();
        resolved.Definition = method.Value;
        foreach (var m in v.JoyMethods)
        {
            if (m.Definition == resolved.Definition)
            {
                resolved.RecreationPowerW = m.PowerW;
            }
        }
        resolved.Environment = Pol.PlacementEnvironment.Indoors;
        if (v.IsFoothold(method))
        {
            resolved.Environment = Pol.PlacementEnvironment.Anywhere;
        }
        if (v.Furniture.IsChair(method.Value))
        {
            var adjacent = new List<Cell>(Adjacent);
            foreach (var s in v.Census.Surfaces)
            {
                adjacent.AddRange(s.Adjacent);
            }
            resolved.Adjacent = adjacent;
        }
        resolved.Stuff = ColonyPlanningRules.BuildStuff(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), resolved.Definition);
        return (resolved, default);
    }
}

partial class BuildingPlanner
{
    internal static DiningFurniture ComfortFurniture(ColonyReading facts) => facts.Facts.RequireBasicComfort().Furniture;

    internal static bool ComfortBuilderAvailable(ColonyReading facts, string definition) => ComfortBuilderGate(facts, definition).IsZero;

    internal static Verdict ComfortBuilderGate(ColonyReading facts, string definition)
    {
        var (def, found) = DefinitionRow(facts, definition);
        if (!found)
        {
            return FieldUnavailable(definition + "_definition");
        }
        var availability = DefinitionAvailability(def!);
        if (!availability.IsZero)
        {
            return availability;
        }
        var minimum = def!.ConstructionSkill;
        if (minimum < 0)
        {
            return FieldUnavailable(definition + "_construction_skill");
        }
        var pawns = facts.Facts.ForecastObserved.WorkPawns.Pawns;
        if (BuilderAvailable(pawns, (int)minimum))
        {
            return default;
        }
        var need = "construction_work_enabled";
        if (minimum > 0)
        {
            need = string.Create(CultureInfo.InvariantCulture, $"construction_skill_{minimum}");
        }
        return Refuse(Cause.NoWorker, "builder_for_" + definition, need);
    }

    internal static Verdict ComfortAccessWait(ComfortNeed dining) =>
        dining == Pol.ComfortNeed.Capacity ? WaitFor(Cause.FacilityAccess, "dining") : WaitFor(Cause.FacilityAccess, "recreation");

    internal static bool BuilderAvailable(IReadOnlyList<WorkPawn> pawns, int minimum)
    {
        foreach (var pawn in pawns)
        {
            if (!pawn.Available || pawn.Work is not PawnWorkTable.Works { Rows: var settings })
            {
                continue;
            }
            if (minimum > 0)
            {
                var qualified = false;
                foreach (var skill in pawn.Skills)
                {
                    if (skill.DefName == "Construction" && !skill.Disabled && skill.Level >= minimum)
                    {
                        qualified = true;
                    }
                }
                if (!qualified)
                {
                    continue;
                }
            }
            foreach (var setting in settings)
            {
                if (setting.Work == Pol.WorkType.Construction && !setting.Disabled && setting.Priority > 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Whether a powered network within six cells of the cell has the spare generation for a recreation building of these watts.</summary>
    internal static bool PoweredRecreationCell(ColonyReading facts, Cell cell, double watts)
    {
        var topology = facts.Facts.EssentialInputs.Power;
        if (topology.Blackout)
        {
            return false;
        }
        foreach (var b in topology.Buildings)
        {
            var dx = cell.X - b.Cell.X;
            var dz = cell.Z - b.Cell.Z;
            if (b.OutputW <= 0 || !b.Connected || (dx * dx) + (dz * dz) > 36)
            {
                continue;
            }
            foreach (var n in topology.Networks)
            {
                if (n.Id == b.Network && n.GenerationW - n.ConsumptionW >= watts)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
