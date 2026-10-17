using System.Globalization;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>food/reserve</c>: the served food-stores/storage Need holds the seeded pemmican and tops it up with a target-count
/// pemmican bill that pauses at the reserve while simple meals feed the colony (#428); once every other food is gone the
/// reserve is released and the colonists eat it.
/// </summary>
internal static class FoodReserve
{
    // The share of the reserve target seeded as unforbidden pemmican: short of the target, so the Need must both hold the
    // stock and fill the rest with a bill, with little left to cook.
    private const double SeedShare = 0.9;
    private const double ReserveDays = PolicyFunctions.DefaultFoodReserveDays;
    private const int FillTicks = 35000;

    private static readonly FixtureOp<NoArgs, Fx.ReservePrepared> Prepare = new(
        "test/food_reserve_prepare",
        _ => ServedOps.Args(("reserveDays", ReserveDays), ("seedShare", SeedShare)),
        ServedOps.Reply<Fx.ReservePrepared>("test/food_reserve_prepare"));

    private static readonly FixtureOp<bool, Fx.ReserveProbe> Probe = new(
        "test/food_reserve_probe",
        drain => ServedOps.Args(("drain", drain)),
        ServedOps.Reply<Fx.ReserveProbe>("test/food_reserve_probe"));

    internal static TypedCase Case() => new()
    {
        Name = "food/reserve",
        Scope = "On the stripped tribal baseline with 200 simple meals and a stove room seeded with pemmican short of the reserve "
            + "target, the served bill and food-storage-upkeep families hold the pemmican (the held count never falls) and fill it "
            + "with a TargetCount Make_Pemmican bill that pauses once the held units reach its target; after test/food_reserve_probe "
            + "destroys every other food, the served Need releases every held stack and the colonists eat pemmican (#428).",
        Ticks = TypedCase.TickCap,
        Tier = CaseTier.Nightly,
        Start = FoodOps.Baseline,
        Ops = [FoodOps.EmptyChannels.Tool, Prepare.Tool, Probe.Tool],
        Keep = ["Food"],

        // The cooking family stays out: its meal bill sits above the reserve bill on the single stove and, with meals eaten
        // as fast as they are cooked, never lets the cook reach the pemmican.
        Serve = new ServeSpec { Families = ["bill", "food-storage-upkeep"], Prefix = "food-reserve" },
        Run = RunAsync,
    };

    // The pemmican count the reserve asks for: ReserveDays of 1.6 nutrition per colonist at 0.05 per unit.
    private static int TargetUnits(int colonists) => (int)Math.Ceiling(ReserveDays * colonists * 1.6 / 0.05);

    private static int Held(Fx.ReserveProbe probe) => probe.Pemmican.Where(p => p.Forbidden).Sum(p => p.Units);

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("empty-channels", FoodOps.EmptyChannels, new FoodStock("MealSimple", 200), cancellationToken).ConfigureAwait(false);
        var prepared = await s.FixtureAsync("reserve-prepare", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var expected = TargetUnits(prepared.Colonists);
        s.Record("expected_target_units", expected);
        if (!prepared.Success || prepared.Colonists < 1 || prepared.Pemmican < 1 || prepared.Pemmican >= expected)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the fixture must seed pemmican short of the {expected}-unit target: {prepared}"));
        }

        // Fill: the seeded stock is held, the bill tops it up and pauses at its target; no colonist eats the reserve.
        var probe = new Fx.ReserveProbe();
        var lastHeld = 0;
        _ = await s.ServeUntilAsync("fill", FillTicks, async ct =>
        {
            probe = await s.FixtureAsync("fill-probe", Probe, false, ct).ConfigureAwait(false);
            var held = Held(probe);
            if (held < lastHeld)
            {
                throw new AcceptanceException($"held pemmican fell from {lastHeld} to {held} units while other food existed");
            }

            lastHeld = held;
            var bill = probe.Bills.FirstOrDefault(b => b.Recipe is "Make_PemmicanBulk" or "Make_Pemmican");
            var filled = bill is { Repeat: "TargetCount", Paused: true } && held >= bill.Target && held >= prepared.Pemmican && bill.Target > prepared.Pemmican;
            return (Wait.Signature(held, bill?.Target, bill?.Paused, probe.OtherFood), filled);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("filled", ServedOps.Json(probe));
        if (probe.OtherFood <= 0)
        {
            throw new AcceptanceException("simple meals vanished before the drain");
        }

        // Drain: every other food goes and the colonists are hungry; the Need releases the held stacks.
        probe = await s.FixtureAsync("drain", Probe, true, cancellationToken).ConfigureAwait(false);
        _ = await s.ServeUntilAsync("release", s.TicksLeft, async ct =>
        {
            probe = await s.FixtureAsync("release-probe", Probe, false, ct).ConfigureAwait(false);
            return (Wait.Signature(Held(probe), probe.ReserveEaten), Held(probe) == 0);
        }, cancellationToken).ConfigureAwait(false);

        // Released: eating takes game time the parked service clock no longer gives.
        _ = await s.RunUntilAsync("eaten", s.TicksLeft, async ct =>
        {
            probe = await s.FixtureAsync("eat-probe", Probe, false, ct).ConfigureAwait(false);
            return (Wait.Signature(probe.ReserveEaten), probe.ReserveEaten > 0);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("eaten", ServedOps.Json(probe));
    }
}
