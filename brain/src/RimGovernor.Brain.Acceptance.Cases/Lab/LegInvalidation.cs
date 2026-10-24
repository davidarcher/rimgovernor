using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/leg-invalidation</c>: the path leg store drops only the legs a map change could alter (#3686).
/// test/leg_invalidation stores a 30-cell leg on an open patch and reads it again after each change: a wall well off its
/// path (a region change, the leg kept), a wall line across it (re-searched, dearer), the line removed (re-searched,
/// cheaper, equal to FindPathNow), and a target walled in (no path), kept through an unrelated wall's region change
/// (#3726: the reachability still rules it out, so it is not searched again) then opened by removing one wall
/// (re-searched, reachable). A unit test cannot run the pathfinder.
/// </summary>
internal static class LabLegInvalidation
{
    private const string Tool = "test/leg_invalidation";

    private static readonly FixtureOp<NoArgs, Fx.LegInvalidationRead> Invalidation = ServedOps.Read<Fx.LegInvalidationRead>(Tool);

    internal static TypedCase Case() => new()
    {
        Name = "lab/leg-invalidation",
        Scope = "A stored leg is kept across a region change off its path, re-searched dearer when a wall crosses it and cheaper (equal to "
            + "FindPathNow) when the wall goes, and a walled-in target's no-path leg is kept through an unrelated wall's region change and "
            + "re-searched reachable once a wall is removed.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 3 },
        Ops = [Tool],
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            var read = await s.FixtureAsync("leg-invalidation", Invalidation, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("leg_invalidation", ServedOps.Json(read));
            var steps = read.Steps.ToDictionary(step => step.Step);
            var wrong = new List<string>();
            Fx.LegInvalidationStep Step(string name, int searches)
            {
                if (!steps.TryGetValue(name, out var step))
                {
                    wrong.Add($"{name}: missing");
                    return new Fx.LegInvalidationStep { Step = name };
                }

                s.Record($"searches_{name}", step.Searches);
                if (step.Searches != searches)
                {
                    wrong.Add($"{name}: {step.Searches} searches, want {searches}");
                }

                return step;
            }

            var open = Step("open", 1);
            var offPath = Step("wall-off-path", 0);
            var detour = Step("detour", 1);
            var removed = Step("detour-removed", 1);
            var walledIn = Step("walled-in", 1);
            var kept = Step("walled-in-kept", 0);
            var opened = Step("opened", 1);
            if (offPath.RegionChanges == 0)
            {
                wrong.Add("wall-off-path: no region change fired");
            }

            if (open.Cost is null || offPath.Cost != open.Cost)
            {
                wrong.Add($"wall-off-path: cost {offPath.Cost}, want the stored {open.Cost}");
            }

            if (detour.Cost is not { } dear || dear <= (open.Cost ?? double.MaxValue))
            {
                wrong.Add($"detour: cost {detour.Cost}, want over {open.Cost}");
            }

            if (removed.Cost is not { } cheap || cheap >= (detour.Cost ?? 0) || removed.Cost != removed.Search)
            {
                wrong.Add($"detour-removed: cost {removed.Cost}, want under {detour.Cost} and equal to FindPathNow's {removed.Search}");
            }

            if (walledIn.Cost is not null || walledIn.Search is not null)
            {
                wrong.Add($"walled-in: cost {walledIn.Cost}, search {walledIn.Search}, want no path");
            }

            if (kept.RegionChanges == 0)
            {
                wrong.Add("walled-in-kept: no region change fired");
            }

            if (kept.Cost is not null || kept.Search is not null)
            {
                wrong.Add($"walled-in-kept: cost {kept.Cost}, search {kept.Search}, want no path still");
            }

            if (opened.Cost is null || opened.Cost != opened.Search)
            {
                wrong.Add($"opened: cost {opened.Cost}, want FindPathNow's {opened.Search}");
            }

            if (wrong.Count != 0)
            {
                throw new AcceptanceException("leg invalidation: " + string.Join("; ", wrong));
            }
        },
    };
}
