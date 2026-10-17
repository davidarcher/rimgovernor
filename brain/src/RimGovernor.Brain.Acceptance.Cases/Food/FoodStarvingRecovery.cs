using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>food/starving-tribal-recovery</c> (Go <c>starvingrecovery.go</c>).</summary>
internal static class FoodStarvingRecovery
{
    // The day budget: the colony is sampled each game day and the case fails when the chain below has not completed by
    // the last. The malnutrition severity is what the recovered colony must be at or under; the hunger is the food level
    // the colonists start at, low enough that malnutrition really builds before the first meat.
    private const int Days = 8;
    private const double RecoveryMalnutrition = 0.3;
    private const double Hunger = 0.05;

    internal static AcceptanceCase Case() => new()
    {
        Name = "food/starving-tribal-recovery",
        Tier = CaseTier.Nightly,
        Scope = "Native postconditions and end-to-end signal (#2173; a Go snapshot test cannot cover the vanilla physics): the starving tribal colony of food/starving-tribal (tribal8, no food, no weapons, no butcher bill, six wild deer, Core only) under the autonomous controller "
            + "crafts a bow that a colonist wields, designates and kills prey, the killer's job after a kill is Hunt on the next live designated prey and not a haul of its own corpse, "
            + "the meat is butchered (delivery ledger) and eaten (native ingest counter), malnutrition has recovered to 0.3 or below and the food runway is positive, all within eight game days.",
        Start = FoodFixture.EmptyChannels("MealSurvivalPack", 0),
        RequiredOps = [FoodStarving.PrepareOp, FoodStarving.ObserveOp],
        Keep = ["Food"],
        NoCheckpoint = true,
        Service = true,
        Budget = TimeSpan.FromMinutes(20),
        Crew = new Crew { Size = 3 },
        Stall = TimeSpan.FromMinutes(2),
        Reason = "up to eight native days of a bow craft, hunts, butchering and recovery from malnutrition, like food/fishing's fifteen; the start is already the starving fixture",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        await FoodStarving.PrepareAsync(s, new JsonObject { ["foodLevel"] = Hunger }, cache: false, cancellationToken).ConfigureAwait(false);
        double peak = 0;
        bool chained = false, armed = false;
        var samples = new JsonArray();
        var missing = new List<string>();
        await FoodStarving.ServeAsync(
            s,
            null,
            Wait.TicksPerDay,
            Days,
            async r =>
            {
                var o = r.Observed;
                peak = Math.Max(peak, o["malnutrition"].AsNumber());
                foreach (var c in o["colonists"].Rows())
                {
                    // Every weapon was destroyed at the start: a ranged one in hand was crafted.
                    if (c["ranged"].AsBool() == true)
                    {
                        armed = true;
                    }
                }

                foreach (var k in o["killJobs"].Rows())
                {
                    if (k["targetLive"].AsBool() == true && k["jobDef"].AsString() == "Hunt" && k["targetDesignated"].AsBool() == true)
                    {
                        chained = true;
                    }
                }

                var butchered = 0.0;
                try
                {
                    var ledger = await FoodLedger.ReadAsync(s, r.Prefix + "-ledger", cancellationToken).ConfigureAwait(false);
                    butchered = ledger.Raw["butchersTotal"].AsNumber();
                }
                catch (AcceptanceException e)
                {
                    report[r.Prefix + "_ledger_error"] = e.Message;
                }

                var runway = r.Colony["foodRunwayDays"].AsNumber();
                var sample = new JsonObject
                {
                    ["round"] = r.N,
                    ["clockParked"] = r.Parked,
                    ["malnutrition"] = o["malnutrition"].Copy(),
                    ["peakMalnutrition"] = peak,
                    ["bowWielded"] = armed,
                    ["wildKills"] = o["wildKills"].Copy(),
                    ["huntChain"] = chained,
                    ["butchers"] = butchered,
                    ["meatEaten"] = o["meatEaten"].Copy(),
                    ["foodRunwayDays"] = r.Colony["foodRunwayDays"].Copy(),
                    ["pendingHunts"] = o["pendingHunts"].Copy(),
                    ["killJobs"] = o["killJobs"].Copy(),
                };
                samples.Add(sample);
                report["samples"] = samples;
                report["latest"] = sample;
                missing.Clear();
                foreach (var (name, ok) in new (string, bool)[]
                {
                    ("a bow was crafted and wielded", armed),
                    ("prey was killed", o["wildKills"].AsNumber() > 0),
                    ("the killer's next job was Hunt on live designated prey", chained),
                    ("meat was butchered", butchered > 0),
                    ("meat was eaten", o["meatEaten"].AsNumber() > 0),
                    ($"malnutrition is at most {RecoveryMalnutrition}", o["malnutrition"].AsNumber() <= RecoveryMalnutrition),
                    ("the food runway is positive", runway > 0),
                })
                {
                    if (!ok)
                    {
                        missing.Add(name);
                    }
                }

                return missing.Count == 0;
            },
            cancellationToken).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            throw new AcceptanceException($"the starving colony did not recover within {Days} days; unmet: [{string.Join(' ', missing)}]");
        }
    }
}
