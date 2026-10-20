using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsAnimalContainment
{
    /// <summary>
    /// Whether a free colonist can take animal handling work: unknown while any pawn's availability or work
    /// settings are unread.
    /// </summary>
    internal static Fact<bool> AnimalHandlerAvailable(IReadOnlyList<WorkPawn> pawns)
    {
        var known = true;
        var available = false;
        foreach (var p in pawns)
        {
            if (!p.Available.TryGetValue(out var avail))
            {
                known = false;
                continue;
            }
            if (!avail)
            {
                continue;
            }
            if (!p.Work.TryGetValue(out var work))
            {
                known = false;
                continue;
            }
            foreach (var w in work)
            {
                if (Wire.Name(w.Work) == "Handling" && !w.Disabled && w.Priority > 0)
                {
                    available = true;
                }
            }
        }
        return known ? Known(available) : Unknown<bool>();
    }

    internal static PlanningDefinition? AnimalContainmentDefinition(IReadOnlyList<PlanningDefinition> definitions, string name)
    {
        foreach (var d in definitions)
        {
            if (d.Name == name)
            {
                return d;
            }
        }
        return null;
    }

    internal static string? AnimalContainmentStuff(PlanningDefinition a, PlanningDefinition b) => StuffChoiceReads.SharedStuff(a, b, out var stuff) ? stuff : null;

    /// <summary>The stuff both shell definitions are made of: the styled wall stuff (ShellStyleOf), then the cheapest in stock, then any shared.</summary>
    internal static string? ShellSharedStuff(ColonyProjection facts, PlanningDefinition a, PlanningDefinition b)
    {
        var want = RoundsTierStyle.ShellStyleOf(facts);
        if (want != "" && a.StuffOptions.Count > 0 && a.MakeableFrom(want) && b.MakeableFrom(want))
        {
            return want;
        }
        if (facts.TryStock(out var stock))
        {
            try
            {
                var price = a.StuffChoice(StuffCriterion.CheapestStuff, stock);
                if (b.MakeableFrom(price.Stuff))
                {
                    return price.Stuff;
                }
            }
            catch (DomainException)
            {
            }
        }
        return AnimalContainmentStuff(a, b);
    }

    internal static Verdict ContainmentWait(AnimalContainmentReason reason) => reason switch
    {
        AnimalContainmentReason.WaitingHandler => NoWorker("animal_handler"),
        AnimalContainmentReason.WaitingNativePen => AwaitingPlan("native_pen", "delivery"),
        AnimalContainmentReason.MarkerExhausted => AwaitingPlan("native_pen", "marker_placed"),
        AnimalContainmentReason.NoPen => AwaitingPlan("pen", "planned"),
        _ => AwaitingPlan("pen", Wire.Name(reason)),
    };

    /// <summary>Whether the planned animal pens stand.</summary>
    internal static bool PensStand(ColonyProjection facts)
    {
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var (ground, gk) = RoundsBuildingPlanner.ColonyGround(facts);
        if (!pk || !ck || !gk)
        {
            return false;
        }
        return HerdPenRules.PensStand(plan!, plan!.GroundWithRock(ground, RoundsBuildingPlanner.NaturalRock(facts)), census!.Buildings);
    }
}
