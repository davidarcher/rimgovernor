using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>food/fishing</c> (Go <c>fishing.go</c>).</summary>
internal static class FoodFishing
{
    private const string ObserveOp = "test/fishing_observe";

    internal static AcceptanceCase Case() => new()
    {
        Name = "food/fishing",
        Scope = "Odyssey: the live ledger opens fishing at min(regeneration nutrition, pawn capacity); the shared food standard creates a reachable zone on that water body at a 60% population floor, and two colonists feed on fishing for 15 days without soil.",
        Start = new FixtureStart
        {
            On = new DebugStart { Size = new DebugStartSize { MapSize = 150, PlanetCoverage = 0.05, Biomes = "TropicalRainforest", Seed = "fishing-426" } },
            Op = "test/fishing_prepare",
        },
        NoCheckpoint = true,
        Service = true,
        Keep = ["Food", "Rest"],
        RequiredOps = [ObserveOp],
        Budget = TimeSpan.FromMinutes(20),
        Crew = new Crew { Size = 3 },
        Reason = "15 native days of food consumption and water-body recovery; a programmatic no-soil coast starts ready to fish",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var facts = await NativeReads.ColonyFactsAsync(s, "fishing-before", planning: true, cancellationToken).ConfigureAwait(false);
        var water = facts["foodChannels"].AsMap()?["observed"].AsMap()?["fishableWater"].AsMap();
        var regions = water?["regions"].AsSlice() ?? [];
        if (regions.Count != 1 || water?["fishingResearched"].AsBool() != true)
        {
            throw new AcceptanceException($"missing Odyssey fishing census: {water.ToJson()}");
        }

        var region = regions[0].AsMap() ?? [];
        var root = region["root"].AsMap() ?? [];
        var id = PolicyFunctions.FishingRegionId(new Cell((int)root["x"].AsNumber(), (int)root["z"].AsNumber()));
        var rate = Math.Min(0.025 * region["maxPopulation"].AsNumber() * region["nutritionPerFish"].AsNumber(), region["pawnFishWorkCapacity"].AsNumber());
        if (rate <= 0 || region["proposedCells"].AsSlice().Count < (int)region["concurrentFishers"].AsNumber())
        {
            throw new AcceptanceException($"unusable fishing capacity: {region.ToJson()}");
        }

        s.Report["fishing_region"] = region.DeepClone();

        // Review without the field writer first: the unzoned region opens, neither designated nor delivering, before
        // the zone exists.
        var review = await BrainRun.StartAsync(s, ["work"], "fishing-review", keepPrefix: null, cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitOpenAsync(s, review, id, rate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await review.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        var create = await BrainRun.StartAsync(s, ["field", "work", "research"], "fishing-create", keepPrefix: "fishing", cancellationToken).ConfigureAwait(false);
        string methodPlan;
        try
        {
            methodPlan = await WaitZoneCreateAsync(create, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await create.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        s.Report["fishing_method"] = methodPlan;
        ulong start = 0;
        _ = await NativeReads.RunUntilAsync(
            s,
            "fishing-fifteen-days",
            2500,
            async ct =>
            {
                var v = await h.CallAsync("fishing-outcome", ObserveOp, null, ct).ConfigureAwait(false);
                if (v["colonists"].AsNumber() != 2 || v["malnutrition"].AsNumber() > 0.3 || v["soilCells"].AsNumber() != 0 || v["competingSources"].AsNumber() != 0)
                {
                    throw new AcceptanceException($"fishing-only feeding failed: {v.ToJson()}");
                }

                var zones = v["zones"].AsSlice();
                if (zones.Count != 1)
                {
                    throw new AcceptanceException($"expected one native fishing zone: {v.ToJson()}");
                }

                var z = zones[0].AsMap() ?? [];
                if (Math.Abs(z["floor"].AsNumber() - DomainConstants.FishingPopulationFloor) > 1e-5 || z["cells"].AsSlice().Count == 0
                    || z["mode"].AsString() != "DoForever" || z["sameBody"].AsBool() != true)
                {
                    throw new AcceptanceException($"native fishing zone settings or geometry differ: {z.ToJson()}");
                }

                if (start == 0)
                {
                    start = (ulong)Math.Max(0, v["tick"].AsNumber());
                }

                var done = v["tick"].AsNumber() - start >= 15 * 60000;
                if (done && (v["foodNutrition"].AsNumber() <= 0 || v["catches"].AsNumber() <= 0))
                {
                    throw new AcceptanceException($"no native fishing food outcome: {v.ToJson()}");
                }

                s.Report["fishing_after"] = v.DeepClone();
                return (Wait.Signature(v["tick"].ToJson()), done);
            },
            quiet: true,
            cancellationToken).ConfigureAwait(false);
    }

    // The live food plan opens the water body at the ledger's rate: neither designated nor delivering yet.
    private static async Task WaitOpenAsync(ISession s, BrainRun review, string id, double rate, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (true)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AcceptanceException("live food plan did not open fishing");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            var colony = await review.GetAsync("/api/player/colony", cancellationToken).ConfigureAwait(false);
            foreach (var row in colony["foodPlan"].AsMap()?["portfolio"].Rows() ?? [])
            {
                if (row["kind"].AsString() == "Fishing" && row["id"].AsString() == id && row["decision"].AsString() == "Open" && row["open"].AsBool() != true)
                {
                    if (Math.Abs(row["nutritionPerDay"].AsNumber() - rate) > 1e-5)
                    {
                        throw new AcceptanceException($"ledger rate is not regeneration/capacity derived: {row.ToJson()} want {NativeReads.Fmt(rate)}");
                    }

                    s.Report["fishing_plan_open"] = row.DeepClone();
                    return;
                }
            }
        }
    }

    // The standard method for EnsureFoodSupply must be one plan with a single zone_create. The player API's plan read
    // carries the action kind, not the zone's kind or footprint, so the zone itself is judged on the native side.
    private static async Task<string> WaitZoneCreateAsync(BrainRun create, CancellationToken cancellationToken)
    {
        var plan = await NativeReads.WithinAsync(
            TimeSpan.FromMinutes(2),
            "fishing method",
            async ct =>
            {
                var method = await create.Service.WaitMethodAsync(PolicyFunctions.EnsureFoodSupply.Value, null, ct).ConfigureAwait(false);
                var (state, _) = await create.Service.WaitPlanTerminalAsync(method.Plan, ct).ConfigureAwait(false);
                return (method.Plan, state);
            },
            cancellationToken).ConfigureAwait(false);
        var actions = plan.state["actions"].AsSlice();
        if (actions.Count != 1)
        {
            throw new AcceptanceException("fishing method has unexpected actions");
        }

        if (actions[0].AsMap()?["kind"].AsString() != Wire.Name(ActionKind.ZoneCreate))
        {
            throw new AcceptanceException("food method did not create a fishing zone");
        }

        return plan.Plan;
    }
}
