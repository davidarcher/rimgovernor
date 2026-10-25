using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The royalty read is a named state (#3888): Inactive without the expansion (nothing is read and nothing is owed),
/// Active with the facts; the ability and title rows are plain values, a permit's royal aid and minimum title are
/// relations (null for a passive permit and for none).
/// </summary>
public sealed class RoyaltyStateTests
{
    private static readonly DefinitionCatalog Catalog = new([new Verse.ThingDef { defName = "Silver" }, new Verse.TerrainDef { defName = "Soil" }], DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));

    private static RoyalRung Rung(string title, int seniority, int favor) => new()
    {
        Title = title,
        Seniority = seniority,
        FavorNeeded = favor,
        Throne = new ThroneRequirements(),
        BedroomMinImpressiveness = 0,
        BedroomThings = [],
    };

    private static RoyalPermit Permit(string name, RoyalAid? aid) => new()
    {
        Name = name,
        MinTitle = null,
        PermitPoints = 1,
        Aid = aid,
        Worker = PermitChoiceRules.PermitWorkerCallAid,
    };

    private static PawnRoyalHolding Holding(int favor, params string[] permits) => new()
    {
        FactionDef = "Empire",
        Title = "Knight",
        Favor = favor,
        PermitPoints = 0,
        Permits = [.. permits],
        PermitCooldowns = [.. permits.Select(p => new PermitCooldown { Permit = p, CooldownRemainingTicks = 0 })],
    };

    [Fact]
    public void AnInactiveSectionIsTheInactiveStateAndReadsNoPawnRows()
    {
        // No pawn rows: the inactive section reads nothing, so it never asks for them.
        Assert.IsType<RoyaltyState.Inactive>(ColonyPlanningReads.RoyaltyOf(RoyaltySectionTests.NoRoyalty, null, Catalog));
        // The rounds facts hold the Inactive state before the read.
        Assert.IsType<RoyaltyState.Inactive>(new RoundsFacts().Royalty);
    }

    [Fact]
    public void APermitIsReadyOnlyWithRoyalAidItsHolderCanAfford()
    {
        var facts = new RoyaltyFacts
        {
            Permits = new Dictionary<string, RoyalPermit>
            {
                ["CallAid"] = Permit("CallAid", new RoyalAid(4)),
                ["TradeSettlement"] = Permit("TradeSettlement", null),
            },
        };
        Assert.True(CombatPermitRules.PermitReady(facts, Holding(4, "CallAid"), "CallAid"));
        Assert.False(CombatPermitRules.PermitReady(facts, Holding(3, "CallAid"), "CallAid"));
        // A passive permit calls no aid, so it is never ready, whatever the favor.
        Assert.False(CombatPermitRules.PermitReady(facts, Holding(100, "TradeSettlement"), "TradeSettlement"));
        // A permit the catalog does not list is not ready.
        Assert.False(CombatPermitRules.PermitReady(facts, Holding(100, "Unlisted"), "Unlisted"));
    }

    [Fact]
    public void TheNextClaimIsTheRungAboveTheHeldTitleAndNoneAtTheTop()
    {
        EquatableArray<RoyalRung> ladder = [Rung("Freeholder", 0, 1), Rung("Knight", 100, 8)];
        Assert.Equal("Freeholder", TitleClaimRules.NextClaim(ladder, null)!.Title);
        Assert.Equal("Knight", TitleClaimRules.NextClaim(ladder, "Freeholder")!.Title);
        Assert.Null(TitleClaimRules.NextClaim(ladder, "Knight"));
        Assert.Null(TitleClaimRules.NextClaim(ladder, "Count"));
    }
}
