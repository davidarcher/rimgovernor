namespace RimGovernor.Brain.BuildingRuntime;

internal static class ColonyPlanCut
{
    /// <summary>
    /// The strength of the wall the perimeter cut is advised for: the hit points of the wall made of the stuff
    /// the colony would build it from (the stocked stuff with the most hit points per cost), else of the wood the
    /// layout builds with, and of the wood wall laid on light footing.
    /// </summary>
    public static PerimeterWall PerimeterWallOf(ColonyProjection projection)
    {
        var output = new PerimeterWall(new WallStrength.Unrated(), new WallStrength.Unrated());
        foreach (var d in projection.Definitions)
        {
            if (d.Name != PerimeterCutRules.PerimeterDefs().WallDef)
            {
                continue;
            }
            var (price, priced) = RoundsDefenseLayoutPlanner.DefenseUnitPrice(projection, d);
            foreach (var option in d.StuffOptions)
            {
                if (!option.Stats.TryGetValue(Bridge.Contracts.StatDefNames.StatMaxHitPoints, out var hp))
                {
                    continue;
                }
                if (option.Stuff == LayoutDefenseRules.PerimeterLightStuff)
                {
                    output = output with { LightHP = new WallStrength.Rated(hp) };
                }
                if (priced && option.Stuff == price.Stuff)
                {
                    output = output with { HP = new WallStrength.Rated(hp) };
                }
            }
        }
        if (output.HP is WallStrength.Unrated)
        {
            output = output with { HP = output.LightHP };
        }
        return output;
    }

    /// <summary>
    /// Writes the layout_plan rows of the advised routing cut (target perimeter_cut): whether it closes every
    /// route but the opening, and the thickness of each of its segments. Advice only: nothing builds from the
    /// cut; the defense round builds the ring PlanPerimeter traces.
    /// </summary>
    public static void LogPerimeterCut(LayoutPlan plan, MapSurvey survey, PerimeterWall wall, int colonists)
    {
        foreach (var d in PerimeterCutRules.PlanPerimeterCut(plan, survey, wall).Decisions)
        {
            var attrs = new Dictionary<string, object?> { ["colonists"] = colonists };
            foreach (var (k, v) in d.Attrs)
            {
                attrs[k] = v;
            }
            Tel.Decide(new Decision { Kind = TelemetryKinds.LayoutPlanKind, Component = "layout", Verdict = d.Verdict, Reason = d.Reason, Target = "perimeter_cut", Attrs = attrs });
        }
    }
}
