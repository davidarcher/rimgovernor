using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/ring</c> (Go <c>ring.go</c>): on the tribal baseline colony at Masonry, staged one review short of
/// the shelter family's expansion Need, the expansion planner raises the capacity ring of stone-block walls and a stone Door.
/// </summary>
internal static class LayoutRing
{
    /// <summary>The prefix that starts every shelter expansion Need building method (buildingruntime's capacity selection).</summary>
    private const string ExpansionMethod = "expansion-";

    /// <summary>The environment variable the service reads its harness-only fault injections from (buildingruntime <c>FaultsEnv</c>).</summary>
    internal const string FaultsEnv = "RIMGOVERNOR_FAULT_INJECT";

    internal static AcceptanceCase Case() => new()
    {
        Name = "layout/ring",
        Tier = CaseTier.Nightly,
        Scope = "Issue #1271: on the tribal " + Baselines.BaselineName + " colony at Masonry, staged one review short of " +
            "the shelter family's expansion Need (a bedroom per single colonist and per couple, a used bed each, no spare place, Foothold's exits met, the food gap pinned to zero), " +
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
                    Extra = [ShelterFamily.Expansion.Value],
                    Until = sample => LayoutFixture.Planned(
                        sample[ShelterFamily.Expansion.Value].AsMap(),
                        method => method.StartsWith(ExpansionMethod, StringComparison.Ordinal),
                        complete: true),
                },
                Audit = async (h, rep, ct) =>
                {
                    var shells = await LayoutFixture.ShellsAsync(h, s.Identity, "ring-audit", ct).ConfigureAwait(false);
                    var (doors, walls) = StoneRing(shells);
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
    /// Counts the stone-block Doors and Walls, standing or planned: the watch ends once the step is planned, so the ring
    /// may still be blueprints and frames, whose rows carry the stuff each becomes.
    /// </summary>
    private static (int Doors, int Walls) StoneRing(IReadOnlyList<LayoutFixture.Shell> shells)
    {
        var categories = PolicyFunctions.CoreItemFacts().Categories;
        int doors = 0, walls = 0;
        foreach (var row in shells)
        {
            if (!categories.TryGetValue(new Resource(row.Stuff), out var held) || !held.Contains("StoneBlocks"))
            {
                continue;
            }

            switch (row.Def)
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
