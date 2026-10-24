using System.Globalization;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>food/ledger</c>: the colony facts' delivery ledger counts native fish catches, rice harvests and milk against the
/// fixture's independent tally, and its counters never fall within a load token. <see cref="ReadAsync"/> is the ledger read
/// the food cases share.
/// </summary>
internal static class FoodLedger
{
    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Prepare = ServedOps.Inject("test/ledger_prepare");
    private static readonly FixtureOp<NoArgs, Fx.LedgerTally> Observe = ServedOps.Read<Fx.LedgerTally>("test/ledger_observe");

    internal static TypedCase Case() => new()
    {
        Name = "food/ledger",
        Scope = "On an Odyssey coast (150-tile TropicalRainforest debug start on 30% planet coverage) test/ledger_prepare initializes the native fish population and stages a finite fishing zone, fishers, a mature rice field "
            + "and a full-milk cow; observations_read_colony_facts' delivery ledger then counts FISH and CROP RawRice units within the "
            + "fixture's own Notify_Fished and successfully placed harvest-stack tallies taken before and after the read, counts ANIMAL_PRODUCT Milk, and a "
            + "second read keeps the load token with no counter lower.",
        Ticks = 10000,
        Tier = CaseTier.Nightly,
        // Five percent coverage only samples mid-latitudes; 30% reaches the tropical rainfall band.
        // Keep the biome: the staged ocean must support the fixture's native fish population.
        Start = new DebugStart { Size = new DebugStartSize { MapSize = 150, PlanetCoverage = 0.3, Biomes = "TropicalRainforest", Seed = "fishing-426" } },
        Ops = [Prepare.Tool, Observe.Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("prepare", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("ledger-deliveries", s.TicksLeft, async ct =>
        {
            var tally = await s.FixtureAsync("ledger-progress", Observe, NoArgs.Value, ct).ConfigureAwait(false);
            return (Wait.Signature(tally.Catches, tally.RiceYield, tally.Gathers), tally is { Catches: > 0, RiceYield: > 0, Gathers: > 0 });
        }, cancellationToken).ConfigureAwait(false);

        var before = await s.FixtureAsync("tally-before", Observe, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var ledger = await ReadAsync(s, "ledger-facts", cancellationToken).ConfigureAwait(false);
        var after = await s.FixtureAsync("tally-after", Observe, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("tally_before", ServedOps.Json(before));
        s.Record("tally_after", ServedOps.Json(after));
        s.RecordWire("ledger", ledger);
        if (ledger.LoadToken.Length == 0)
        {
            throw new AcceptanceException("the ledger has no load token");
        }

        // The ledger may count a delivery between the two tallies; it is never outside them.
        foreach (var (name, kind, def, low, high) in new[]
        {
            ("fish", Observations.DeliveryKind.Fish, "", before.Catches, after.Catches),
            ("rice", Observations.DeliveryKind.Crop, "RawRice", before.RiceYield, after.RiceYield),
        })
        {
            var units = Units(ledger, kind, def);
            if (units <= 0 || units < low || units > high)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{name} ledger units {units} are outside the independent tally [{low}, {high}]"));
            }
        }

        if (Units(ledger, Observations.DeliveryKind.AnimalProduct, "Milk") <= 0)
        {
            throw new AcceptanceException("no milk counted for a gathered cow");
        }

        var second = await ReadAsync(s, "ledger-facts-again", cancellationToken).ConfigureAwait(false);
        if (second.LoadToken != ledger.LoadToken)
        {
            throw new AcceptanceException($"load token changed without a load: \"{ledger.LoadToken}\" -> \"{second.LoadToken}\"");
        }

        var now = second.Counts.ToLookup(r => (r.Kind, r.Source, r.Def), r => r.Units);
        if (ledger.Counts.FirstOrDefault(r => now[(r.Kind, r.Source, r.Def)].Sum() < r.Units) is { } fell)
        {
            throw new AcceptanceException($"counter {fell.Kind}/{fell.Source}|{fell.Def} decreased from {fell.Units}");
        }
    }

    private static long Units(Observations.DeliveryLedger ledger, Observations.DeliveryKind kind, string def) =>
        ledger.Counts.Where(r => r.Kind == kind && (def.Length == 0 || r.Def == def)).Sum(r => r.Units);

    /// <summary>The colony facts' delivery ledger.</summary>
    internal static async Task<Observations.DeliveryLedger> ReadAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        return TypedReads.Observed(label, NativeOps.ReadColonyFacts, reply, reply.Outcome?.Observed).DeliveryLedger;
    }
}
