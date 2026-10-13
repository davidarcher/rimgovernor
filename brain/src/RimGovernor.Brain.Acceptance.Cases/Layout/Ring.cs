using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/ring</c> (Go <c>ring.go</c>): on the tribal baseline colony at Masonry, staged one review short of
/// MaintainHousing's expansion phase, the expansion planner raises the capacity ring of stone-block walls and a stone Door.
/// </summary>
internal static class LayoutRing
{
    /// <summary>The prefix that starts every MaintainHousing expansion-phase building method (buildingruntime's capacity selection).</summary>
    private const string ExpansionMethod = "expansion-";

    /// <summary>The environment variable the service reads its harness-only fault injections from (buildingruntime <c>FaultsEnv</c>).</summary>
    internal const string FaultsEnv = "RIMGOVERNOR_FAULT_INJECT";

    internal static AcceptanceCase Case() => new()
    {
        Name = "layout/ring",
        Scope = "Issue #1271: on the tribal " + Baselines.BaselineName + " colony at Masonry, staged one review short of " +
            "MaintainHousing's expansion phase (a bedroom per single colonist and per couple, a used bed each, no spare place, Foothold's exits met, the food gap pinned to zero), " +
            "the expansion planner raises the capacity ring: the native audit reads a stone Door and stone-block walls. " +
            "A snapshot test cannot cover it: the ring is proven by native construction of the tier's stuff, and the run " +
            "records the step read TestLayoutRingStepIsMasonry replays.",
        Start = new FixtureStart
        {
            Op = LayoutFixture.GridPrepare,
            ArgsFrom = LayoutSiteHost.BedroomArgsAsync,
            Args = new JsonObject
            {
                ["sleepingSpots"] = 8,
                ["stoneBlocks"] = LayoutFixture.Blocks,
                ["builders"] = true,
                ["expansion"] = true,
            },
            On = new SaveStart { Name = Baselines.BaselineName },
        },
        RequiredOps = [LayoutFixture.GridAudit],
        Keep = [NeedDef.Food.Value],

        // The start builds no food economy: the food plan's gap is pinned to zero so the expansion gate opens.
        Serve = new ServeSpec
        {
            Families = ["shelter", "expansion", "sleeping"],
            NativeTimeout = TimeSpan.FromSeconds(30),
            Prefix = "layout-ring",
            Env = [FaultsEnv + "=foodgap=zero"],
        },
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Reason = "one watch: the fixture raises the ring the expansion step orders",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        report["fixture"] = s.Prepared?.DeepClone();
        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = TimeSpan.FromMinutes(2),
                    Extra = [PolicyFunctions.MaintainHousing.Value],
                    Until = sample => LayoutFixture.Planned(
                        sample[PolicyFunctions.MaintainHousing.Value].AsMap(),
                        method => method.StartsWith(ExpansionMethod, StringComparison.Ordinal),
                        complete: true),
                },
                Audit = async (h, rep, ct) =>
                {
                    var audit = await h.CallAsync("ring-audit", LayoutFixture.GridAudit, [], ct).ConfigureAwait(false);
                    var (doors, walls) = StoneRing(audit);
                    rep["ring"] = new JsonObject { ["stoneDoors"] = doors, ["stoneWalls"] = walls };
                    if (doors == 0 || walls == 0)
                    {
                        throw new AcceptanceException($"no stone ring stands: {doors} stone doors, {walls} stone-block walls");
                    }
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the audit's stone-block Doors and Walls, standing or planned: the watch ends once the step is planned, so
    /// the ring may still be blueprints and frames, whose planned rows carry the stuff each becomes.
    /// </summary>
    private static (int Doors, int Walls) StoneRing(JsonObject audit)
    {
        var categories = PolicyFunctions.CoreItemFacts().Categories;
        int doors = 0, walls = 0;
        foreach (var item in audit["walls"].AsSlice().Concat(audit["planned"].AsSlice()))
        {
            var row = item.AsMap() ?? [];
            if (!categories.TryGetValue(new Resource(row["stuff"].AsString()), out var held) || !held.Contains("StoneBlocks"))
            {
                continue;
            }

            switch (row["def"].AsString())
            {
                case "Door":
                    doors++;
                    break;
                case "Wall":
                    walls++;
                    break;
            }
        }

        return (doors, walls);
    }
}
