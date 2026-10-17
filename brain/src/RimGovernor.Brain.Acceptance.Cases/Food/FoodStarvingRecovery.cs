using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>food/starving-tribal-recovery</c> (#2173): a starving tribal colony with no food, no weapons and no butcher bill, under
/// the autonomous brain, crafts a bow a colonist wields, kills designated wild prey, chains the hunt to the next live prey
/// instead of hauling the corpse, butchers the meat and eats it. The old case also waited out malnutrition recovery, days of
/// vanilla physics past the cap; the chain to eaten meat is the claim.
/// </summary>
internal static class FoodStarvingRecovery
{
    // Low enough that the colony is starving from the first tick.
    private const double Hunger = 0.05;

    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Starve = new(
        "test/food_starving_prepare",
        _ => ServedOps.Args(("foodLevel", Hunger)),
        ServedOps.Done("test/food_starving_prepare"));

    private static readonly FixtureOp<NoArgs, Fx.StarvingObserved> Observe = ServedOps.Read<Fx.StarvingObserved>("test/food_starving_observe");

    internal static TypedCase Case() => new()
    {
        Name = "food/starving-tribal-recovery",
        Scope = "On the stripped tribal baseline (no food, every weapon destroyed, no butcher bill, six wild deer) at food level 0.05, "
            + "the served autonomous brain gets a crafted ranged weapon into a colonist's hands, a colonist kills wild prey and its "
            + "job two ticks later is Hunt on the next live designated prey, the delivery ledger records a butcher, and colonists "
            + "eat meat (the fixture's ingest counter), within one game day.",
        Ticks = TypedCase.TickCap,
        Tier = CaseTier.Nightly,
        Start = FoodOps.Baseline,
        Ops = [FoodOps.EmptyChannels.Tool, Starve.Tool, Observe.Tool],
        Keep = ["Food"],
        Serve = new ServeSpec { Prefix = "starving-recovery" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("empty-channels", FoodOps.EmptyChannels, new FoodStock("MealSurvivalPack", 0), cancellationToken).ConfigureAwait(false);
        _ = await s.FixtureAsync("starve", Starve, NoArgs.Value, cancellationToken).ConfigureAwait(false);

        bool armed = false, chained = false;
        var butchers = 0;
        var observed = new Fx.StarvingObserved();
        _ = await s.ServeUntilAsync("recover", s.TicksLeft, async ct =>
        {
            observed = await s.FixtureAsync("observe", Observe, NoArgs.Value, ct).ConfigureAwait(false);

            // Every weapon was destroyed at the start: a ranged one in hand was crafted.
            armed |= observed.Colonists.Any(c => c.Ranged);
            chained |= observed.KillJobs.Any(k => k is { TargetLive: true, JobDef: "Hunt", TargetDesignated: true });
            butchers = (await FoodLedger.ReadAsync(s, "ledger", ct).ConfigureAwait(false)).Butchers.Count;
            var done = armed && observed.WildKills > 0 && chained && butchers > 0 && observed.MeatEaten > 0;

            // The colonists' jobs move while the served clock runs, so only a parked game stalls the wait.
            return (Wait.Signature(armed, observed.WildKills, chained, butchers, observed.MeatEaten, string.Join(",", observed.Colonists.Select(c => c.Job))), done);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("observed", ServedOps.Json(observed));
        s.Record("butchers", butchers);
    }
}
