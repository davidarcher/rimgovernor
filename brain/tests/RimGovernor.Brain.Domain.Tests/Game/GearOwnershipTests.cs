using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;
using D = RimGovernor.Contracts.Wire.Defs;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class GearOwnershipTests
{
    [Fact]
    public void CandidateValuationRetainsNativeEvidenceWhenModelOverridesGain()
    {
        var observed = new O.GearCandidate { Item = GearFixtures.Item("shirt", new O.ItemQuality.Qualityless()), Gain = .2 };
        var native = new RimGovernor.Brain.Policy.GearCandidate(observed, new("Shirt"));
        var modeled = native with { Valuation = new GearCandidateValuation.Modeled(.7) };

        Assert.Equal(.2, native.Gain);
        Assert.Equal(.7, modeled.Gain);
        Assert.Same(observed, modeled.Observed);
        Assert.Equal(.2, modeled.Observed.Gain);
    }

    private static DefinitionCatalog Catalog() => new(
        [new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }],
        DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10));

    [Fact]
    public void ProductionGearUsesTheCanonicalPawnBodySkillsDraftAndIdeology()
    {
        var catalog = Catalog();
        var pawn = PawnFixtures.Row("pawn");
        var skill = new O.Skill { DefName = "Crafting", Level = 12, StoredLevel = 12, Passion = O.Passion.Minor };
        pawn = pawn with
        {
            Drafted = true,
            Biography = pawn.Biography with { Skills = [skill], DisabledWorkTags = [nameof(WorkTags.Violent)] },
            Settings = pawn.Settings with
            {
                Body = pawn.Settings.Body with { ComfortableMinC = -17, ComfortableMaxC = 41 },
                PolicyInputs = pawn.Settings.PolicyInputs with { Ideo = new O.PawnIdeo.Member("ideo", ["Nudism_Preferred"], null, 1) },
            },
        };
        var observed = GearFixtures.Loadout("pawn") with { ApparelPolicy = PoliciesTests.EmptyOutfit };
        var state = ColonyGearReads.ApparelPolicyFacts(observed, catalog, pawn)!;
        var model = ColonyGearReads.GearLoadoutModelFacts(
            new GearDefinitions { Catalog = catalog, Finished = new HashSet<string>() },
            new Tables(default, default, default, []), 20, observed, state,
            ColonyModelFixtures.NoThreat, new StuffArrival(new Dictionary<Resource, long>(), []));
        var input = Assert.IsType<GearModel.Modeled>(model).Input;

        Assert.Same(observed, input.Observed);
        Assert.Same(pawn.Settings.Body, input.Body);
        Assert.Same(pawn, input.Role.Pawn);
        Assert.Same(skill, Assert.Single(input.Role.Skills));
        Assert.Same(pawn.Settings.PolicyInputs.Ideo, input.Role.Ideology);
        Assert.True(input.Role.DraftedSquad);
        Assert.True(input.Role.IncapableOfViolence);
        Assert.Equal(-17, input.ComfortableMin);
        Assert.Equal(41, input.ComfortableMax);
    }

    [Fact]
    public void ItemAndCatalogOriginsKeepTheirActualSourcesDistinct()
    {
        var observed = GearFixtures.Option("shirt", "Apparel_Shirt", "Cloth", GearSource.Stored) with
        {
            Quality = new O.ItemQuality.Qualityless(), Condition = .65, Tainted = true,
        };
        var item = new GearOption { Origin = new GearOptionOrigin.Observed(observed) };
        var bill = new O.GearLockedBill { DefName = "Apparel_Shirt", Craftable = true, Crafters = [new(12, false, 0)] };
        var product = new GearOption { Origin = new GearOptionOrigin.Catalog(bill, "Cloth", false) };

        Assert.Same(observed, Assert.IsType<GearOptionOrigin.Observed>(item.Origin).Value);
        Assert.True(item.Qualityless);
        Assert.True(item.Tainted);
        Assert.Equal(.65, item.Condition);
        Assert.Same(bill, Assert.IsType<GearOptionOrigin.Catalog>(product.Origin).Bill);
        Assert.Equal(GearSource.Bill, product.Source);
        Assert.Equal(1, product.Condition);
        Assert.False(product.Tainted);
        Assert.False(product.Locked);
    }

    [Fact]
    public void AllocatingStoredApparelPreservesTheObservedStockCount()
    {
        var native = new O.GearStock
        {
            DefName = "Apparel_Shirt", Stuff = "Cloth", Quality = new O.ItemQuality.Graded(D.QualityCategory.Normal), HpBand = 9, Count = 2,
        };
        var wanted = new GearOption { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("shirt", native.DefName, native.Stuff, GearSource.Stored)) };
        var planned = new GearLoadout { Role = GearRole.Worker, Gaps = [new GearGap { Wanted = wanted, Source = GearSource.Stored, Floor = true }] };

        var balance = Assert.Single(GearStockRules.UnassignedGearStock([native], [planned]));

        Assert.Same(native, balance.Observed);
        Assert.Equal(2, native.Count);
        Assert.Equal(1, balance.Count);
        Assert.Equal(1, balance.Allocated);
    }

    [Fact]
    public void GoalRequirementsExtendThePolicyWithoutEditingItsNativeRequirements()
    {
        var observed = PoliciesTests.EmptyOutfit with { RequiredDefs = ["native"] };
        var gear = GearFixtures.Loadout("pawn") with { ApparelPolicy = observed };
        var state = new RimGovernor.Brain.Policy.ApparelPolicyState
        {
            Observed = observed,
            Role = new GearRoleInput { Observed = gear, Pawn = PawnFixtures.Row("pawn") },
            GoalRequired = ["goal"],
        };

        Assert.Equal(["native", "goal"], state.Required);
        Assert.Equal(["native"], state.Observed.RequiredDefs);
    }
}
