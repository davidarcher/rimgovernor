using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    public static RoundsBuildingPlanner NewRoundsComfortPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsComfortPlanner: reviewer == nil || native == nil");
        }
        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsComfortPlanner: !ok");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = PolicyApi.EnsureComfort, Phase = Pol.Phase.ComfortRanked };
    }

    public static RoundsBuildingPlanner NewRoundsBasicComfortPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsBasicComfortPlanner: reviewer == nil || native == nil");
        }
        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsBasicComfortPlanner: !ok");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = PolicyApi.EnsureComfort, Phase = Pol.Phase.ComfortBasic };
    }

    /// <summary>The planner resolved to the comfort furniture the ranked review selects, or the verdict that explains why none is wanted.</summary>
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectComfort(ColonyProjection facts, ComfortHistory history)
    {
        if (!facts.Facts.Comfort.TryGetValue(out var v))
        {
            return (null, FieldUnavailable("comfort"));
        }
        var review = PolicyApi.ReviewComfort(facts.Facts.Comfort, history, facts.Identity.Tick);
        var method = PolicyApi.SelectComfortMethod(v, review);
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
        var role = Pol.RoomRole.DiningRoom;
        if (v.IsFoothold(method))
        {
            role = Pol.RoomRole.RecRoom;
        }
        resolved.Facility = PolicyApi.Facility(role);
        if (v.Furniture.IsChair(method.Value))
        {
            var adjacent = new List<Cell>(Adjacent);
            foreach (var s in v.Surfaces)
            {
                adjacent.AddRange(s.Adjacent);
            }
            resolved.Adjacent = adjacent;
        }
        resolved.Stuff = facts.BuildStuff(resolved.Definition);
        return (resolved, default);
    }

    /// <summary>The planner resolved to the basic comfort building the review selects (a chair, table or joy building).</summary>
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectBasicComfort(ColonyProjection facts)
    {
        if (!facts.Facts.BasicComfort.TryGetValue(out var v))
        {
            return (null, FieldUnavailable("basic_comfort"));
        }
        var review = PolicyApi.ReviewBasicComfort(facts.Facts.BasicComfort);
        var method = PolicyApi.SelectBasicComfortMethod(v, review);
        if (method == Pol.ComfortMethod.NoMethod && !review.VarietyKnown)
        {
            return (null, FieldUnavailable("comfort_variety"));
        }
        if (method == Pol.ComfortMethod.NoMethod && review.MissingVariety > 0)
        {
            method = PolicyApi.SelectRecreationVariety(v, m =>
            {
                if (m.PowerW > 0)
                {
                    var canSite = false;
                    foreach (var c in facts.Cells)
                    {
                        canSite = canSite || (c.Roofed == Known(true) && c.Indoors == Known(true) && !c.Occupied() && PoweredRecreationCell(facts, c.Cell, m.PowerW));
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
        if (v.Joy is not null)
        {
            foreach (var m in v.Joy.Methods)
            {
                if (m.Definition == resolved.Definition)
                {
                    resolved.RecreationPowerW = m.PowerW;
                }
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
            foreach (var s in v.Surfaces)
            {
                adjacent.AddRange(s.Adjacent);
            }
            resolved.Adjacent = adjacent;
        }
        resolved.Stuff = facts.BuildStuff(resolved.Definition);
        return (resolved, default);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The native work ticks the comfort furniture just built still need before the next comfort pick; the longest across the furniture.</summary>
    internal static uint ComfortUseAllowance(StoreJournal journal, StandardState goal, GenerationSnapshot current, Tick tick, DiningFurniture furniture, CancellationToken ct)
    {
        uint ticks = 0;
        foreach (var definition in furniture.Definitions())
        {
            PlanId methodPlan;
            try
            {
                methodPlan = journal.LoadOwnerMethod(goal, new MethodId("comfort-" + definition), ct).Plan;
            }
            catch (NotFoundException)
            {
                continue;
            }
            var plan = journal.LoadPlan(methodPlan, ct);
            ticks = Math.Max(ticks, ComfortNativeWorkTicks(plan, current, tick, furniture));
        }
        return ticks;
    }

    internal static DiningFurniture ComfortFurniture(ColonyProjection facts)
    {
        if (facts.Facts.Comfort.TryGetValue(out var v))
        {
            return v.Furniture;
        }
        if (facts.Facts.BasicComfort.TryGetValue(out var b))
        {
            return b.Furniture;
        }
        return new DiningFurniture();
    }

    internal static bool ComfortBuilderAvailable(ColonyProjection facts, string definition) => ComfortBuilderGate(facts, definition).IsZero;

    internal static Verdict ComfortBuilderGate(ColonyProjection facts, string definition)
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
        if (!def!.ConstructionSkill.TryGetValue(out var minimum) || minimum < 0)
        {
            return FieldUnavailable(definition + "_construction_skill");
        }
        if (!facts.WorkPawns.TryGetValue(out var pawns))
        {
            return FieldUnavailable("work_pawns");
        }
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

    /// <summary>The ticks (at most 120) a completed comfort building of this plan keeps the comfort review waiting, within 10000 ticks of the completion.</summary>
    internal static uint ComfortNativeWorkTicks(PlanState plan, GenerationSnapshot current, Tick tick, DiningFurniture furniture)
    {
        if (plan.Progress.Count != 1)
        {
            return 0;
        }
        var p = plan.Progress[0];
        if (p.Action.Building is not { } b || !furniture.Definitions().Contains(b.Definition))
        {
            return 0;
        }
        current = current with { Plan = plan.Spec.Id, Revision = plan.Spec.Revision };
        var v = p.View;
        var known = v.Effect.TryGetValue(out var effect);
        if (v.Stage != Stage.Completed || v.Unresolved || !known || effect != Effect.Completed || !v.Snapshot.Matches(current) || tick < v.Tick || tick.Value - v.Tick.Value >= 10000)
        {
            return 0;
        }
        return Math.Min(120u, (uint)(10000 - (tick.Value - v.Tick.Value)));
    }

    internal static bool BuilderAvailable(IReadOnlyList<WorkPawn> pawns, int minimum)
    {
        foreach (var pawn in pawns)
        {
            if (pawn.Available != Known(true) || pawn.Applies != Known(true))
            {
                continue;
            }
            if (!pawn.Work.TryGetValue(out var settings))
            {
                continue;
            }
            if (minimum > 0)
            {
                if (!pawn.Skills.TryGetValue(out var skills))
                {
                    continue;
                }
                var qualified = false;
                foreach (var skill in skills)
                {
                    if (skill.Name == "Construction" && !skill.Disabled && skill.Level >= minimum)
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
    internal static bool PoweredRecreationCell(ColonyProjection facts, Cell cell, double watts)
    {
        if (!facts.PowerPlanning.TryGetValue(out var topology) || topology.Blackout != Known(false))
        {
            return false;
        }
        foreach (var b in topology.Buildings)
        {
            var ok = b.OutputW.TryGetValue(out var output);
            var nk = b.Network.TryGetValue(out var net);
            var dx = cell.X - b.Cell.X;
            var dz = cell.Z - b.Cell.Z;
            if (!ok || output <= 0 || !nk || b.Connected != Known(true) || (dx * dx) + (dz * dz) > 36)
            {
                continue;
            }
            foreach (var n in topology.Networks)
            {
                var gk = n.GenerationW.TryGetValue(out var generation);
                var ck = n.ConsumptionW.TryGetValue(out var consumption);
                if (n.Id == net && gk && ck && generation - consumption >= watts)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
