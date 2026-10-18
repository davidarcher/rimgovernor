using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The goals family's building steps (#3496): the trade beacon and the standalone geothermal generator a built-count
/// goal owes. Each is one building Method of the goal's own Need, admitted by claims like the biotech family's charger
/// and gene bank (<see cref="BiotechRounds.PlaceAsync"/>); the turret is built by the defense layout instead.
/// </summary>
public sealed partial class GoalsRounds
{
    internal const int SiteTries = 8;

    /// <summary>The work of a ready goal Need: the beacon or generator step for a goal the family places itself, nothing for the rest (their Methods are holds).</summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, GoalsFacts facts, CancellationToken call)
    {
        var goal = facts.Goals.FirstOrDefault(g => GoalRules.NeedOf(g) == work.Need.Id);
        if (goal is null || !GoalRules.OwnStep(goal) || goal.Count <= 0)
        {
            return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        }
        return await BuildStepAsync(work, goal, GoalRules.PlacerOf(goal.Target), call).ConfigureAwait(false);
    }

    /// <summary>
    /// One building of the goal's definition on a free site: a trade beacon near the colony's centre, a geothermal generator
    /// on the nearest steam geyser nothing stands on. One Method per count standing, <c>built-&lt;def&gt;-&lt;n&gt;</c>, so
    /// the same building continues and is re-issued once released with its product gone.
    /// </summary>
    private async Task<Verdict> BuildStepAsync(NeedWork work, GoalRecord goal, BuiltPlacer placer, CancellationToken call)
    {
        var started = _reviewer.Clock.Now();
        var read = await BiotechRounds.StepReadingAsync(_reviewer, work, call).ConfigureAwait(false);
        var f = read.Projection;
        if (!GoalRules.BuiltOf(f.Facts, goal.Target).TryGetValue(out var standing))
        {
            return FieldUnavailable("built_buildings");
        }
        if (standing >= goal.Count)
        {
            return BuildingReasonNoDeficit;
        }
        if (read.Catalog is null)
        {
            return FieldUnavailable("definition_catalog");
        }
        read = read.WithProjection(f.AddDefinitions(read.Frame, read.Catalog, [goal.Target]));
        f = read.Projection;
        var defs = f.Definitions.Where(d => d.Name == goal.Target && d.Available.TryGetValue(out var available) && available).ToList();
        if (defs.Count == 0)
        {
            return FieldUnavailable(goal.Target + "_definition");
        }
        if (BiotechRounds.DefinitionIntentStanding(defs, f.Facts.ConstructionClaims, f.Facts.CurrentConstruction))
        {
            return BuildingReasonExistingWork;
        }
        var method = new MethodId($"built-{goal.Target}-{standing}");
        if (!_building.MethodOnce(work.Owner, method, call, f.Facts.CurrentConstruction))
        {
            return WaitFor(Cause.MethodUsed, "built_count_method");
        }
        if (!defs[0].Size.TryGetValue(out var size) || size.Width <= 0 || size.Height <= 0)
        {
            return FieldUnavailable(goal.Target + "_size");
        }
        IEnumerable<Rectangle> sites;
        if (placer == BuiltPlacer.Geothermal)
        {
            if (!f.PowerPlanning.TryGetValue(out var power))
            {
                return FieldUnavailable("power");
            }
            sites = GeyserSites(power.Geysers, f.Center(), size);
        }
        else
        {
            if (!f.Center().TryGetValue(out var center))
            {
                return BuildingReasonNoLayoutPlan;
            }
            sites = FreeSitesRules.FreeSites(new FreeSiteRequest { Bounds = f.Bounds, Anchor = center, Cells = f.Cells, Light = f.LightTerrains }, size.Width, size.Height);
        }
        var verdict = await BiotechRounds.PlaceAsync(_reviewer, _building, work, f, goal.Target, method, size, sites, SiteTries, "built_count_preview", started, call).ConfigureAwait(false);
        return verdict ?? NoSpace(placer == BuiltPlacer.Geothermal ? "geyser" : "beacon_cell");
    }

    /// <summary>The free geysers' footprints, nearest the colony's centre first (in map order while it is unread).</summary>
    internal static IEnumerable<Rectangle> GeyserSites(IReadOnlyList<PowerGeyser> geysers, Fact<Cell> center, Bounds size)
    {
        var free = geysers.Where(g => !g.Occupied && g.Cells.Count > 0);
        if (center.TryGetValue(out var at))
        {
            free = free.OrderBy(g => Math.Abs(g.Cell.X - at.X) + Math.Abs(g.Cell.Z - at.Z));
        }
        return free.Select(g => new Rectangle(g.Cells.Min(c => c.X), g.Cells.Min(c => c.Z), size.Width, size.Height));
    }
}
