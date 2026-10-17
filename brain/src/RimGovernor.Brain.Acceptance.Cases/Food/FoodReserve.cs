using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>food/reserve</c> (Go <c>reserve.go</c>).</summary>
internal static class FoodReserve
{
    private const string PrepareOp = "test/food_reserve_prepare";
    private const string ProbeOp = "test/food_reserve_probe";

    // The share of the default reserve target the fixture seeds as unforbidden pemmican in the food room (0.9: the bill
    // has less to cook, so the fill finishes in fewer rounds): the stock stays short of the target, so the goal must both
    // hold it and refill the rest with a preserve bill.
    private const double SeedShare = 0.9;

    // The reserve the service runs with at every stage.
    private const double ReserveDays = PolicyFunctions.DefaultFoodReserveDays;

    // The game time one service round runs before the case stops it to read native state: the bill, the holds and the
    // meals all move within a quarter day.
    private const ulong RoundTicks = 15000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "food/reserve",
        Tier = CaseTier.Nightly,
        Scope = "The food-stores/storage Need holds unforbidden pemmican and fills the rest with a target-count pemmican bill that stops at the reserve while simple meals feed the colony (#428); deleting every other food releases the reserve and the colonists eat it.",
        Start = FoodFixture.EmptyChannels("MealSimple", 200),
        RequiredOps = [PrepareOp, ProbeOp],
        Keep = ["Food"],
        Service = true,
        Budget = TimeSpan.FromMinutes(12),
        Crew = new Crew { Size = 3 },
        Stall = TimeSpan.FromMinutes(2),
        Run = RunAsync,
    };

    // The pemmican count the reserve asks for: ReserveDays of 1.6 nutrition per colonist at 0.05 per unit.
    private static int TargetUnits(int colonists) => (int)Math.Ceiling(ReserveDays * colonists * 1.6 / 0.05);

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;

        // A resume from the ring reloads a mid-run save that already holds the food room and the seeded runway:
        // preparing again would stack a second runway on it and push the upkeep items census over its bound, so the
        // resumed run reads the fresh run's result.
        var prepared = NativeReads.RestoredState(s, "fixture").AsMap();
        if (prepared is null)
        {
            prepared = await h.CallAsync("reserve-prepare", PrepareOp, new JsonObject { ["reserveDays"] = ReserveDays, ["seedShare"] = SeedShare }, cancellationToken).ConfigureAwait(false);
            Checkpoints.SetState("fixture", prepared.DeepClone());
        }

        var seeded = prepared["pemmican"].Strict() ?? 0;
        var colonists = prepared["colonists"].Strict() ?? 0;
        var expected = TargetUnits((int)colonists);
        if (colonists < 1 || seeded < 1 || (int)seeded >= expected)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"fixture must seed pemmican short of the {expected}-unit reserve target: {prepared.ToJson()}"));
        }

        report["expected_target_units"] = expected;
        report["fixture"] = prepared.DeepClone();

        // round runs the service for RoundTicks of game time (and until the clock parks), stops it and reads the native
        // state through the probe. The durable review persists only the pending reserve identities, which the player
        // API does not show, so the native probe is the assertion and the attempt loops below absorb a round that
        // stopped before a hold or release finished.
        async Task<JsonObject> Round(string prefix, JsonObject? args)
        {
            try
            {
                // The cooking family stays out: its meal bill sits above the reserve bill on the single stove and, with
                // meals eaten as fast as they are cooked, never lets the cook reach the pemmican.
                var run = await BrainRun.StartAsync(s, ["bill", "food-storage-upkeep"], prefix, keepPrefix: prefix, cancellationToken).ConfigureAwait(false);
                try
                {
                    // A round ends once either the round's game time elapsed or the clock parked (no_work: the planners
                    // have nothing left, so the tick stops moving); the review keeps revising at a parked tick, so the
                    // tick alone is the stall signature.
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

                            var parked = tick > 0 && parkedSince.Elapsed > TimeSpan.FromSeconds(20);
                            return (Wait.Signature(tick), tick >= start + RoundTicks || parked);
                        },
                        cancellationToken).ConfigureAwait(false);
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

            await FoodFixture.PauseForProbeAsync(h, prefix + "-probe", cancellationToken).ConfigureAwait(false);
            var probe = await h.CallAsync(prefix + "-probe", ProbeOp, args, cancellationToken).ConfigureAwait(false);
            report[prefix] = probe.DeepClone();
            return probe;
        }

        // Fill: the seeded pemmican is held, the bill tops the stock up to its target and stacks are held until the
        // target is covered; the bill then pauses. Whole stacks are held, so the last batch may leave a free surplus.
        // The held count never falls: no colonist eats the reserve.
        JsonObject probe;
        var lastHeld = 0;
        for (var attempt = 1; ; attempt++)
        {
            probe = await Round(string.Create(CultureInfo.InvariantCulture, $"fill-{attempt}"), null).ConfigureAwait(false);
            var (held, free) = PemmicanUnits(probe);
            if (held < lastHeld)
            {
                throw new AcceptanceException($"held pemmican fell from {lastHeld} to {held} units while other food existed: {probe.ToJson()}");
            }

            lastHeld = held;
            var bill = ReserveBill(probe);
            if (bill is not null)
            {
                var target = bill["target"].Strict() ?? 0;
                var paused = bill["paused"].AsBool() == true;
                if (bill["repeat"].AsString() == "TargetCount" && paused && held >= (int)target && held >= (int)seeded && target > seeded)
                {
                    break;
                }
            }

            if (attempt == 8)
            {
                var detail = bill is null ? $"native Make_Pemmican(Bulk) bill missing: {probe["bills"].ToJson()}" : "<nil>";
                throw new AcceptanceException($"reserve never filled and held: {held} held, {free} free, bill {bill.ToJson()}: {detail}");
            }
        }

        if ((probe["otherFood"].Strict() ?? 0) <= 0)
        {
            throw new AcceptanceException($"simple meals vanished before the drain: {probe.ToJson()}");
        }

        // Drain: every other food goes and the colonists are hungry. The runway falls under the seasonal minimum, the
        // review releases every held stack and the colonists eat it.
        JsonObject? args = new JsonObject { ["drain"] = true };
        for (var attempt = 1; ; attempt++)
        {
            probe = await Round(string.Create(CultureInfo.InvariantCulture, $"release-{attempt}"), args).ConfigureAwait(false);
            args = null;
            var (held, _) = PemmicanUnits(probe);

            // Released: eating needs game time the parked service clock no longer gives, so the harness ticks the game
            // itself before re-reading.
            for (var tick = 0; held == 0 && tick < 4; tick++)
            {
                if ((probe["reserveEaten"].Strict() ?? 0) > 0)
                {
                    return;
                }

                _ = await s.AdvanceAsync(2500, null, cancellationToken).ConfigureAwait(false);
                probe = await h.CallAsync(string.Create(CultureInfo.InvariantCulture, $"release-{attempt}-eat-{tick}"), ProbeOp, null, cancellationToken).ConfigureAwait(false);
                report[string.Create(CultureInfo.InvariantCulture, $"release-{attempt}-eat")] = probe.DeepClone();
            }

            if (held == 0 && (probe["reserveEaten"].Strict() ?? 0) > 0)
            {
                return;
            }

            if (attempt == 4)
            {
                throw new AcceptanceException($"reserve not released and eaten after the drain: {held} held: {probe.ToJson()}");
            }
        }
    }

    // Sums the probe's pemmican stacks into forbidden (held) and unforbidden units.
    private static (int Held, int Free) PemmicanUnits(JsonObject probe)
    {
        if (probe["pemmican"] is not JsonArray rows)
        {
            throw new AcceptanceException($"pemmican stacks unobserved: {probe.ToJson()}");
        }

        int held = 0, free = 0;
        foreach (var row in rows.Rows())
        {
            var units = (int)(row["units"].Strict() ?? 0);
            if (row["forbidden"].AsBool() == true)
            {
                held += units;
            }
            else
            {
                free += units;
            }
        }

        return (held, free);
    }

    private static JsonObject? ReserveBill(JsonObject probe) =>
        probe["bills"].Rows().FirstOrDefault(row => row["recipe"].AsString() is "Make_PemmicanBulk" or "Make_Pemmican");
}
