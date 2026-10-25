using D = RimGovernor.Contracts.Wire.Defs;
using O = RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3540: ArmorUtility.GetPostArmorDamage as an expectation, per pawn outfit and weapon bin.
public sealed class ArmorResponseTests
{
    private static readonly BodyCoverage Torso = new()
    {
        Shares = [new CoverageShare { Groups = ["Torso"], Probability = 1 }],
        Layers = ["OnSkin", "Middle", "Shell"],
    };

    private static GearOption Garment(double sharp, double blunt = 0, string layer = "Shell", string group = "Torso") =>
        new() { Origin = new GearOptionOrigin.Observed(GearFixtures.Option(layer + sharp, "", "", GearSource.Worn)), Sharp = sharp, Blunt = blunt, Layers = [layer], Groups = [group] };

    private static WeaponBin Sharp(double damage, double ap) => new() { Damage = damage, ArmorPenetration = ap, Category = ArmorCategory.Sharp, Weight = 1 };

    private static ArmorResponse Of(GearOption[] outfit, BodyCoverage coverage, WeaponBin bin) => ArmorResponseRules.Of(outfit, new NaturalArmor(), coverage, bin);

    [Fact]
    public void ADusterDeflectsAndHalvesAHitByHalfItsEffectiveArmorEach()
    {
        var response = Of([Garment(0.5)], Torso, Sharp(10, 0.15));

        Assert.Equal(0.175, response.DeflectChance, 9);
        Assert.Equal((10 * 0.65) + (5 * 0.175), response.ExpectedDamage, 9);
        Assert.Equal(0.65, response.BleedChance, 9);
    }

    [Fact]
    public void ADevilstrandDusterBeatsClothOnExpectedDamageForEverySharpBin()
    {
        foreach (var bin in new[] { Sharp(5, 0), Sharp(10, 0.15), Sharp(18, 0.1), Sharp(30, 0.25) })
        {
            var devilstrand = Of([Garment(0.8)], Torso, bin);
            var cloth = Of([Garment(0.3)], Torso, bin);

            Assert.True(devilstrand.ExpectedDamage < cloth.ExpectedDamage, $"damage {bin.Damage} ap {bin.ArmorPenetration}");
            Assert.True(devilstrand.BleedChance < cloth.BleedChance);
        }
    }

    [Fact]
    public void ABluntBinIgnoresTheSharpStatAndABluntHitNeverBleeds()
    {
        var bin = new WeaponBin { Damage = 10, ArmorPenetration = 0, Category = ArmorCategory.Blunt, Weight = 1 };

        Assert.Equal(10, Of([Garment(0.9)], Torso, bin).ExpectedDamage, 9);
        Assert.Equal(0, Of([Garment(0.9)], Torso, bin).BleedChance);
        Assert.Equal(10 * 0.8 + (5 * 0.1), Of([Garment(0, 0.2)], Torso, bin).ExpectedDamage, 9);
    }

    [Fact]
    public void AHalvedSharpHitIsBluntForTheLayersUnderItAndStopsBleeding()
    {
        // Shell sharp 0.4 over a skin layer with blunt 1: the shell halves 0.2 of hits, deflects 0.2; the skin layer then rates them as blunt.
        var outfit = new[] { Garment(0.4, layer: "Shell"), Garment(0, 1, layer: "OnSkin") };
        var response = Of(outfit, Torso, Sharp(10, 0));

        // Sharp through the shell (0.6) meets the skin layer's sharp 0: unhalved and bleeding.
        Assert.Equal(0.6, response.BleedChance, 9);
        // 0.6 x 10, plus the 0.2 halved to 5 that the blunt-1 skin deflects half of and halves half of: 0.2 x 0.5 x 2.5.
        Assert.Equal(6 + (0.2 * 0.5 * 2.5), response.ExpectedDamage, 9);
    }

    [Fact]
    public void AnUncoveredPartTakesFullDamageAndGarmentsOnOtherGroupsDoNotCountOnIt()
    {
        var coverage = new BodyCoverage
        {
            Shares = [new CoverageShare { Groups = ["Torso"], Probability = 0.5 }, new CoverageShare { Groups = ["Legs"], Probability = 0.3 }],
            Layers = Torso.Layers,
        };

        var response = Of([Garment(0.5, group: "Torso")], coverage, Sharp(10, 0));

        // Torso 0.5: 10 x 0.5 + 5 x 0.25; legs 0.3 and the 0.2 uncovered remainder: 10.
        Assert.Equal((0.5 * ((10 * 0.5) + (5 * 0.25))) + (0.5 * 10), response.ExpectedDamage, 9);
        Assert.Equal(0.2, coverage.Uncovered, 9);
        Assert.Equal(10, Of([], Torso, Sharp(10, 0)).ExpectedDamage, 9);
    }

    [Fact]
    public void PenetrationAboveTheArmorPassesTheHitAndQualityAndConditionScaleTheRating()
    {
        Assert.Equal(10, Of([Garment(0.5)], Torso, Sharp(10, 0.5)).ExpectedDamage, 9);
        var worn = Garment(0.5) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Garment(0.5)).Origin).Value) with { Quality = new O.ItemQuality.Graded(D.QualityCategory.Legendary), Condition = 0.5 }) };

        // 0.5 x 1.8 x 0.5 = 0.45.
        Assert.Equal(0.225, Of([worn], Torso, Sharp(10, 0)).DeflectChance, 9);
    }

    [Fact]
    public void NaturalArmorIsTheInnermostLayerAndHeatBinsReadTheHeatArmor()
    {
        var hide = new NaturalArmor { Sharp = 0.2 };
        var heat = new WeaponBin { Damage = 10, Category = ArmorCategory.Heat, Weight = 1 };
        var shirt = Garment(0) with { ArmorHeat = 0.4 };

        Assert.Equal(0.1, ArmorResponseRules.Of([], hide, Torso, Sharp(10, 0)).DeflectChance, 9);
        Assert.Equal(0.2, ArmorResponseRules.Of([shirt], new NaturalArmor(), Torso, heat).DeflectChance, 9);
    }

    [Fact]
    public void AnOutfitResponseIsOneArrayWithAnEntryPerBin()
    {
        WeaponBin[] bins = [Sharp(10, 0), Sharp(5, 0.2)];
        var responses = ArmorResponseRules.OfOutfit([Garment(0.5)], new NaturalArmor(), Torso, bins);

        Assert.Equal(2, responses.Length);
        Assert.Equal(Of([Garment(0.5)], Torso, bins[1]), responses[1]);
    }
}
