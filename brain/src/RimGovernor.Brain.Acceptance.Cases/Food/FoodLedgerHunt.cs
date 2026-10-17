using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>food/ledger-hunt</c>: the delivery ledger records a native hunt kill and the butchering of its corpse, linked by
/// corpse id, with the butcher record's meat and leather equal to the fixture's independent tally; a re-placed corpse is
/// not recounted.
/// </summary>
internal static class FoodLedgerHunt
{
    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Prepare = ServedOps.Inject("test/ledger_hunt_prepare");
    private static readonly FixtureOp<NoArgs, Fx.HuntButchered> Butcher = new("test/ledger_hunt_butcher", _ => [], ServedOps.Reply<Fx.HuntButchered>("test/ledger_hunt_butcher"));
    private static readonly FixtureOp<NoArgs, Fx.HuntTally> Observe = ServedOps.Read<Fx.HuntTally>("test/ledger_hunt_observe");

    internal static TypedCase Case() => new()
    {
        Name = "food/ledger-hunt",
        Scope = "On the lab test/ledger_hunt_prepare stages a ranger with a bow, a designated wild deer and a butcher table: after the "
            + "native kill the colony facts' delivery ledger holds one Deer kill record with body size and potential nutrition and "
            + "no butcher; after the corpse is re-placed twice and butchered it still holds one kill and one butcher record on the "
            + "same corpse id whose meat and leather equal the fixture's tally and whose nutrition is within the kill's potential.",
        Ticks = 10000,
        Tier = CaseTier.Nightly,
        Ops = [Prepare.Tool, Butcher.Tool, Observe.Tool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("prepare", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("ledger-hunt-kill", s.TicksLeft / 2, async ct =>
        {
            var tally = await s.FixtureAsync("kill-progress", Observe, NoArgs.Value, ct).ConfigureAwait(false);
            return (Wait.Signature(tally.Tick), tally.Kills > 0);
        }, cancellationToken).ConfigureAwait(false);

        var killed = await FoodLedger.ReadAsync(s, "ledger-killed", cancellationToken).ConfigureAwait(false);
        s.RecordWire("ledger_killed", killed);
        if (killed.Kills.ToList() is not [{ CorpseId: { Length: > 0 } corpse, Race: "Deer", PotentialNutrition: > 0, BodySize: > 0 } kill] || killed.Butchers.Count != 0)
        {
            throw new AcceptanceException($"want one complete Deer kill record and no butcher before the bill, got {killed.Kills.Count} kills, {killed.Butchers.Count} butchers");
        }

        var hauled = await s.FixtureAsync("butcher", Butcher, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        if (!hauled.Success || hauled.Corpse != corpse)
        {
            throw new AcceptanceException($"fixture corpse {hauled.Corpse} is not the counted kill {corpse}");
        }

        var done = await s.RunUntilAsync("ledger-hunt-butcher", s.TicksLeft, async ct =>
        {
            var tally = await s.FixtureAsync("butcher-progress", Observe, NoArgs.Value, ct).ConfigureAwait(false);
            return (Wait.Signature(tally.Tick), tally.ButcherMeat > 0);
        }, cancellationToken).ConfigureAwait(false);
        var tally = await s.FixtureAsync("tally-done", Observe, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var ledger = await FoodLedger.ReadAsync(s, "ledger-butchered", cancellationToken).ConfigureAwait(false);
        s.Record("tally", ServedOps.Json(tally));
        s.RecordWire("ledger", ledger);
        if (ledger.Kills.Count != 1 || tally.Kills != 1)
        {
            throw new AcceptanceException($"a re-placed corpse was recounted: ledger kills {ledger.Kills.Count}, fixture corpses {tally.Kills}");
        }

        if (ledger.Butchers.ToList() is not [var butcher] || butcher.CorpseId != corpse)
        {
            throw new AcceptanceException($"want one butcher record on corpse {corpse}, got {string.Join("; ", ledger.Butchers)}");
        }

        if (butcher.MeatUnits is not > 0 || butcher.MeatUnits != tally.ButcherMeat || butcher.LeatherUnits is not > 0 || butcher.LeatherUnits != tally.ButcherLeather)
        {
            throw new AcceptanceException($"butcher meat {butcher.MeatUnits} and leather {butcher.LeatherUnits} differ from the produced {tally.ButcherMeat} and {tally.ButcherLeather}");
        }

        if (butcher.MeatNutrition is not { } nutrition || !(nutrition > 0) || nutrition > kill.PotentialNutrition * 1.01)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"butchered nutrition {butcher.MeatNutrition} is not within the kill's potential {kill.PotentialNutrition}"));
        }

        s.Record("butcher_ticks", done);
    }
}
