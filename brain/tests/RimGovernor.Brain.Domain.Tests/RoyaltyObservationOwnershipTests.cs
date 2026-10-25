using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Policy;
using Verse;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RoyaltyObservationOwnershipTests
{
    [Fact]
    public void KnownPsycastKeepsNativeCooldownAndCatalogDefinition()
    {
        var observed = new O.PawnPsycast { DefName = "Stun", CooldownRemainingTicks = 120 };
        var definition = new Psycast { Def = "Stun", PsyfocusCost = .1, Entropy = 2, Target = PsycastTarget.Pawn, CooldownTicks = 200 };
        var pawn = PawnFixtures.Row("caster") with
        {
            Royalty = new O.PawnRoyaltyState.Active(new O.PawnRoyalty { Holdings = [], Psycasts = [observed], Psylink = null }),
        };
        var snapshot = new O.PawnSnapshot { Context = ColonyFactsCoreTests.Snapshot().Context, Pawns = [pawn] };

        var facts = RoyaltyFactsRules.PawnRoyaltyFacts(snapshot, _ => definition);
        var known = Assert.Single(facts.Psycasts[new PawnId("caster")]);

        Assert.Same(observed, known.Observed);
        Assert.Same(definition, known.Psycast);
        Assert.Equal(120, known.CooldownRemaining);
        Assert.Equal(0, (known with { Observed = observed with { CooldownRemainingTicks = 0 } }).CooldownRemaining);
    }

    [Fact]
    public void NeuroformerKeepsNativeStockAndDerivesTradeabilityFromItsDefinition()
    {
        var stock = new O.NeuroformerStock { DefName = "PsychicAmplifier", Held = 3, Craftable = false };
        var catalog = new DefinitionCatalog(
            [new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" },
                new ThingDef { defName = stock.DefName, tradeability = RimWorld.Tradeability.Sellable }],
            DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10));
        var colony = new O.RoyaltyColonyFacts { Neuroformers = [stock], Ceremonies = [], Thrones = [] };

        var joined = catalog.WithNeuroformerDefs(colony)[stock.DefName];

        Assert.Same(stock, joined.Observed);
        Assert.Equal(3, joined.Held);
        Assert.False(joined.Craftable);
        Assert.True(joined.Tradeable);
        var changed = joined with { Observed = stock with { Held = 0, Craftable = true } };
        Assert.Equal(0, changed.Held);
        Assert.True(changed.Craftable);
    }
}
