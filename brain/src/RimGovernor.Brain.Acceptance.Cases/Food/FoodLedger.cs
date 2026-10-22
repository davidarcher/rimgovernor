using System.Globalization;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>food/ledger</c>: the colony facts' delivery ledger counts native fish catches, rice harvests and milk against the
/// fixture's independent tally, and its counters never fall within an epoch. <see cref="ReadAsync"/> is the ledger read
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
            + "fixture's own Notify_Fished and harvest tallies taken before and after the read, counts ANIMAL_PRODUCT Milk, and a "
            + "second read keeps the epoch with no counter lower.",
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
        if (string.IsNullOrEmpty(ledger.Epoch))
        {
            throw new AcceptanceException("the ledger has no epoch");
        }

        // The ledger may count a delivery between the two tallies; it is never outside them.
        foreach (var (name, kind, def, low, high) in new[]
        {
            ("fish", Observations.DeliverySourceKind.Fish, "", before.Catches, after.Catches),
            ("rice", Observations.DeliverySourceKind.Crop, "RawRice", before.RiceYield, after.RiceYield),
        })
        {
            var units = Units(ledger, kind, def);
            if (units <= 0 || units < low || units > high)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{name} ledger units {units} are outside the independent tally [{low}, {high}]"));
            }
        }

        if (Units(ledger, Observations.DeliverySourceKind.AnimalProduct, "Milk") <= 0)
        {
            throw new AcceptanceException("no milk counted for a gathered cow");
        }

        var second = await ReadAsync(s, "ledger-facts-again", cancellationToken).ConfigureAwait(false);
        if (second.Epoch != ledger.Epoch)
        {
            throw new AcceptanceException($"epoch changed without a load: \"{ledger.Epoch}\" -> \"{second.Epoch}\"");
        }

        var now = second.Rows.ToLookup(r => (r.SourceKind, r.SourceId, r.DefName), r => r.Units ?? 0);
        if (ledger.Rows.FirstOrDefault(r => now[(r.SourceKind, r.SourceId, r.DefName)].Sum() < (r.Units ?? 0)) is { } fell)
        {
            throw new AcceptanceException($"counter {fell.SourceKind}/{fell.SourceId}|{fell.DefName} decreased from {fell.Units}");
        }
    }

    private static long Units(Observations.DeliveryLedgerFacts ledger, Observations.DeliverySourceKind kind, string def) =>
        ledger.Rows.Where(r => r.SourceKind == kind && (def.Length == 0 || r.DefName == def)).Sum(r => r.Units ?? 0);

    /// <summary>The colony facts' observed delivery ledger.</summary>
    internal static async Task<Observations.DeliveryLedgerFacts> ReadAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var facts = TypedReads.Observed(label, NativeOps.ReadColonyFacts, reply, reply.Outcome?.Observed);
        return facts.DeliveryLedger?.Observed ?? throw new AcceptanceException($"{label}: the delivery ledger is not observed: {facts.DeliveryLedger}");
    }
}
