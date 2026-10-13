using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>food/starving-tribal</c> (Go <c>starving.go</c>) and the serve loop <c>food/starving-tribal-recovery</c> shares.
/// </summary>
internal static class FoodStarving
{
    internal const string PrepareOp = "test/food_starving_prepare";
    internal const string ObserveOp = "test/food_starving_observe";

    // The game time between samples (half a day); StarvingRounds samples cover three days, the span the epic asks about.
    private const int StarvingRoundTicks = 30000;
    private const int StarvingRounds = 6;

    internal static AcceptanceCase Case() => new()
    {
        Name = "food/starving-tribal",
        Scope = "Diagnosis (#2141), asserts only that the report was produced: a starving tribal colony (tribal8, no food, no weapons, no butcher bill, six wild deer) is sampled every half day for three days with the live foodPlan portfolio (kind, id, decision, reason, terms, DeliveredPerDay, gap, runway), "
            + "which hunt blockers hold (butcher bill, Cooking worker, ordinary ranged weapon, three gunners, pending-hunt cap), pawn weapons and work state, wildlife counts and the food channel census. "
            + "A Go snapshot test over recorded colony facts cannot cover it: the weapon, work and wildlife state only exist on a live map; the recorded facts it yields become the replay snapshot.",
        Start = FoodFixture.EmptyChannels("MealSurvivalPack", 0),
        RequiredOps = [PrepareOp, ObserveOp],
        Keep = ["Food"],
        Service = true,
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Stall = TimeSpan.FromMinutes(2),
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        await PrepareAsync(s, null, cache: true, cancellationToken).ConfigureAwait(false);
        var samples = new JsonArray();
        await ServeAsync(
            s,
            ["acquisition", "field", "work", "bill", "research"],
            StarvingRoundTicks,
            StarvingRounds,
            async r =>
            {
                var sample = new JsonObject
                {
                    ["round"] = r.N,
                    ["clockParked"] = r.Parked,
                    ["native"] = r.Observed.DeepClone(),
                    ["foodPlan"] = r.Colony["foodPlan"].Copy(),
                    ["blockers"] = HuntBlockers(r.Observed),
                };
                try
                {
                    var facts = await NativeReads.ColonyFactsAsync(s, r.Prefix + "-facts", planning: true, cancellationToken).ConfigureAwait(false);
                    sample["foodChannels"] = facts["foodChannels"].Copy();
                    sample["foodNutrition"] = facts["foodNutrition"].Copy();
                }
                catch (AcceptanceException tolerated)
                {
                    // The census is evidence only: a read that fails leaves the sample without it.
                    Tolerated.Note("food census", tolerated);
                }

                var plan = r.Colony["foodPlan"].AsMap();
                var huntRows = PortfolioRows(plan, "Hunt");
                sample["huntRows"] = huntRows;
                sample["openHuntRow"] = OpenRow(huntRows);
                samples.Add(sample);
                report["samples"] = samples;
                report["latest"] = sample;
                return false;
            },
            cancellationToken).ConfigureAwait(false);
        if (samples.Count != StarvingRounds)
        {
            throw new AcceptanceException($"starving report incomplete: {samples.Count} of {StarvingRounds} samples");
        }
    }

    /// <summary>
    /// Makes the fixture colony a starving tribal one (the prepare op's args name the animals and the hunger) and
    /// records it in the report. <paramref name="cache"/> keeps the result in the checkpoint so a resumed run skips the
    /// prep; a case that reads the op's native counters prepares every run instead, because the counters' hooks live in
    /// the game process, not in the save.
    /// </summary>
    internal static async Task PrepareAsync(ISession s, JsonObject? args, bool cache, CancellationToken cancellationToken)
    {
        JsonObject? prepared = null;
        if (cache)
        {
            prepared = NativeReads.RestoredState(s, "fixture").AsMap();
        }

        if (prepared is null)
        {
            prepared = await s.Harness.CallAsync("starving-prepare", PrepareOp, args, cancellationToken).ConfigureAwait(false);
            if (cache)
            {
                Checkpoints.SetState("fixture", prepared.DeepClone());
            }
        }

        if (prepared["success"].AsBool() != true || prepared["animals"].AsSlice().Count == 0)
        {
            throw new AcceptanceException($"starving fixture refused: {prepared.ToJson()}");
        }

        s.Report["fixture"] = prepared.DeepClone();
    }

    /// <summary>
    /// One sample of the serve loop: the round's number, whether the clock parked before its game-time budget, the live
    /// colony read and the native observe op's reply taken after the service stopped.
    /// </summary>
    internal sealed record Round(Harness H, int N, string Prefix, bool Parked, JsonObject Colony, JsonObject Observed);

    /// <summary>
    /// Runs the controller in rounds of <paramref name="roundTicks"/> game time (at most <paramref name="maxRounds"/>):
    /// each round launches serve, waits for the budget or a parked clock (no_work freezes the tick; the parked tick is
    /// itself a finding), stops it, pauses the game and hands the live colony read and the native observation to
    /// <paramref name="each"/>, which returns true once the case has seen enough to stop.
    /// </summary>
    internal static async Task ServeAsync(
        ISession s,
        IReadOnlyList<string>? families,
        ulong roundTicks,
        int maxRounds,
        Func<Round, Task<bool>> each,
        CancellationToken cancellationToken)
    {
        for (var round = 1; round <= maxRounds; round++)
        {
            var prefix = string.Create(CultureInfo.InvariantCulture, $"starving-{round}");
            JsonObject colony;
            var parked = false;
            try
            {
                var run = await BrainRun.StartAsync(s, families, prefix, keepPrefix: prefix, cancellationToken).ConfigureAwait(false);
                try
                {
                    ulong start = 0, lastTick = 0;
                    var parkedSince = Stopwatch.StartNew();
                    var wait = new Wait { Stall = Wait.StallBudget(), Terminal = run.Service.Exited };
                    await wait.WaitProgressAsync(
                        async ct =>
                        {
                            var tick = await run.TickAsync(ct).ConfigureAwait(false) ?? 0;
                            if (start == 0)
                            {
                                start = tick;
                            }

                            if (tick != lastTick)
                            {
                                lastTick = tick;
                                parkedSince.Restart();
                            }

                            // The Go loop also required a persisted review (Revision > 0); a live tick is the in-mod equivalent.
                            parked = tick > 0 && parkedSince.Elapsed > TimeSpan.FromSeconds(20);
                            return (Wait.Signature(tick), tick >= start + roundTicks || parked);
                        },
                        cancellationToken).ConfigureAwait(false);
                    colony = await run.GetAsync("/api/player/colony", cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await run.StopAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{prefix}: {e.Message}", e);
            }

            var h = s.Harness;
            await FoodFixture.PauseForProbeAsync(h, prefix + "-probe", cancellationToken).ConfigureAwait(false);
            var observed = await h.CallAsync(prefix + "-observe", ObserveOp, null, cancellationToken).ConfigureAwait(false);
            if (await each(new Round(h, round, prefix, parked, colony, observed)).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>The foodPlan portfolio rows of one channel kind.</summary>
    internal static JsonArray PortfolioRows(JsonObject? plan, string kind)
    {
        var rows = new JsonArray();
        foreach (var row in plan?["portfolio"].Rows() ?? [])
        {
            if (row["kind"].AsString() == kind)
            {
                rows.Add(row.DeepClone());
            }
        }

        return rows;
    }

    internal static bool OpenRow(JsonArray rows) => rows.Rows().Any(row => row["decision"].AsString() == "Open");

    /// <summary>
    /// Names which hunt gates hold in one observe read: the native Eligible rule's inputs (butcher bill, Cooking worker,
    /// ordinary ranged weapon, Hunting enabled), the squad's three-gunner floor and the two-hunt pending cap. Route
    /// safety shows in each animal's own reason.
    /// </summary>
    private static JsonObject HuntBlockers(JsonObject observed)
    {
        int gunners = 0, hunters = 0, cooks = 0;
        foreach (var c in observed["colonists"].Rows())
        {
            if (c["hunting"].AsNumber() > 0)
            {
                hunters++;
            }

            if (c["cooking"].AsNumber() > 0)
            {
                cooks++;
            }

            if (c["ranged"].AsBool() == true)
            {
                gunners++;
            }
        }

        var butcher = observed["bills"].Rows().Count(b => b["butcher"].AsBool() == true);
        return new JsonObject
        {
            ["noButcherBill"] = butcher == 0,
            ["noCookingWorker"] = cooks == 0,
            ["noHuntingWorker"] = hunters == 0,
            ["noRangedWeapon"] = gunners == 0,
            ["underSquadFloor"] = gunners < PolicyConstants.SquadHuntMinGunners,
            ["pendingHuntCapHit"] = observed["pendingHunts"].AsNumber() >= 2,
            ["rangedPawns"] = gunners,
            ["squadHuntMinGunner"] = PolicyConstants.SquadHuntMinGunners,
        };
    }
}
