using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Gender = RimGovernor.Contracts.Wire.Defs.Gender;

namespace RimGovernor.Brain.Domain.Tests;

// #3816: a delivered garment is credited, in the Investment term, the apparel mood it changes for its wearer a day from its
// delivery to tier 2's horizon: the wellbeing of the thought delta plus the break days the shifted mood saves.
public sealed class ApparelCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;

    private static NeedThoughtStage Stage(string thought, string role, int stage, double offset)
        => new(thought, role, stage, offset, Known(Day), false);

    private static GearOption Shirt(double condition) => new()
    {
        ID = "shirt" + condition, Definition = new("Apparel_BasicShirt"), Stuff = new("Cloth"), Slot = GearSlot.SkinTorso,
        Quality = 2, Condition = condition, Layers = ["OnSkin"], Groups = ["Torso", "Arms"],
        CareIfDamaged = true, CountsAsClothingForNudity = true,
    };

    private static NeedsForecastPawn Pawn(NeedApparel? apparel, params NeedThoughtStage[] stages) => ForecastValueTests.Pawn() with
    {
        ThoughtStages = EquatableArray.From(stages), Apparel = apparel,
    };

    private static readonly NeedApparel Warm = new(Gender.Male, true, true);

    private static readonly NeedThoughtStage[] Tattered =
        [Stage("ApparelDamaged", ForecastApparel.Damaged, 0, -3), Stage("ApparelDamaged", ForecastApparel.Damaged, 1, -5)];

    private static ForecastCoupledContext Context(NeedsForecastPawn pawn, params GearOption[] worn) => ForecastValueTests.Context(pawn,
        outfits: new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = EquatableArray.From(worn) }) with
    { InvestmentHorizonDays = Horizon };

    private static ForecastCommitment Craft(GearOption garment) => new("craft", default)
    { ActivityCompletions = ["deliver"], OutfitChanges = [new("pawn", [garment]) { Overlay = true }] };

    /// <summary>A day in the mildest break tier unshifted, and none once shifted by <paramref name="shift"/>; the garment delivered at <paramref name="at"/>.</summary>
    private static ForecastCoupledOutcome Delivering(double at, double shift) =>
        new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>
        {
            ["pawn"] = new([], default, default, new(new("pawn", "", default, default), []), [])
            {
                Mood = new([], default, default, default, default, default, [])
                {
                    PressureTicks = Known(0d), BreakRisk = Known(0d), BreakTierTicks = [Day, 0, 0],
                    ShiftedBreakTierTicks = [new(shift, [0, 0, 0])],
                },
            },
        }, [])
        { OutfitTicks = [new("craft", Known(at))] };

    private static ForecastValueResult Value(ForecastCoupledContext context, GearOption garment, double at = Day / 2, double shift = 0.05)
        => ForecastValue.Of(Delivering(at, shift), context, Day, null, [Craft(garment)]);

    [Fact]
    public void ATatteredShirtsReplacementIsWorthTheTatteredStageMoodItRemoves()
    {
        var context = Context(Pawn(Warm, Tattered), Shirt(0.15));
        Assert.Equal(5, ForecastApparel.MoodDelta(context.Needs.Pawns[0], context.Outfits["pawn"], [Shirt(1)]));
        var row = Assert.Single(Value(context, Shirt(1)).InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
        var perDay = context.Value.WellbeingWeight * 5 / 100 + context.MoodBreaks.DaysLost([Day, 0, 0])!.Value;
        Assert.Equal(("pawn", ForecastApparel.CreditKey("pawn", "Apparel_BasicShirt"), Horizon - 0.5), (row.Pawn, row.Key, row.Units));
        Assert.Equal(-perDay * (Horizon - 0.5), row.Value, 12);
    }

    [Fact]
    public void AWornShirtOnlyFrayedIsWorthTheFrayedStageAndAFreshOneNothing()
    {
        var pawn = Pawn(Warm, Tattered);
        Assert.Equal(3, ForecastApparel.MoodDelta(pawn, [Shirt(0.4)], [Shirt(1)]));
        Assert.Equal(0, ForecastApparel.MoodDelta(pawn, [Shirt(0.9)], [Shirt(1)]));
        Assert.False(ForecastApparel.ChangesThoughts([Shirt(0.9)], [Shirt(1)]));
        Assert.DoesNotContain(Value(Context(pawn, Shirt(0.9)), Shirt(1)).InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
    }

    [Fact]
    public void TaintedGarmentsPastTheLastStageAreItsOffset()
    {
        // DeadMansApparel: -5, -8, -11, then -14 for four or more (ThoughtState.StageIndexFor clamps).
        var pawn = Pawn(Warm, [.. new[] { -5, -8, -11, -14 }.Select((o, i) => Stage("DeadMansApparel", ForecastApparel.DeadMans, i, o))]);
        GearOption Tainted(int i) => Shirt(1) with { ID = "t" + i, Tainted = true, Slot = GearSlot.Unset, Groups = [], Layers = [] };
        Assert.Equal(-14, ForecastApparel.MoodOf(pawn, [.. Enumerable.Range(0, 5).Select(Tainted)]));
        Assert.Equal(-8, ForecastApparel.MoodOf(pawn, [Tainted(0), Tainted(1)]));
        Assert.Equal(0, ForecastApparel.MoodOf(pawn, []));
    }

    [Fact]
    public void ACoveringDisapprovedPreceptPricesTheCoverAsALossOnlyAtASafeAmbientTemperature()
    {
        // ThoughtWorker_Precept_AnyBodyPartCovered: a covered pawn at a safe temperature has the thought; a cold one does not.
        var covered = new[] { Stage("Covered", ForecastApparel.AnyCovered, 0, -3) };
        Assert.Equal(-3, ForecastApparel.MoodDelta(Pawn(Warm, covered), [], [Shirt(1)]));
        Assert.Equal(0, ForecastApparel.MoodDelta(Pawn(Warm with { SafeAmbient = false }, covered), [], [Shirt(1)]));
        // Kind-required apparel is no cover of the pawn's own (PawnUtility.HasClothingNotRequiredByKind).
        var uniform = Shirt(1) with { Definition = new("Apparel_Uniform"), Slot = GearSlot.SkinLegs, Groups = ["Legs"] };
        Assert.Equal(0, ForecastApparel.MoodDelta(Pawn(Warm with { KindRequired = ["Apparel_Uniform"] }, covered), [], [uniform]));
    }

    [Fact]
    public void AnUncoveredGroinPreceptAndNakednessAreRelievedByPants()
    {
        var pants = Shirt(1) with { Definition = new("Apparel_Pants"), Slot = GearSlot.SkinLegs, Groups = ["Legs"] };
        var stages = new[] { Stage("GroinUncovered", ForecastApparel.GroinUncovered, 0, -4), Stage("Naked", ForecastApparel.Naked, 0, -6) };
        Assert.Equal(10, ForecastApparel.MoodDelta(Pawn(Warm, stages), [], [pants]));
        // A legless pawn counts as wearing pants: only the precept's thought is lifted.
        Assert.Equal(4, ForecastApparel.MoodDelta(Pawn(Warm with { Legs = false }, stages), [], [pants]));
    }

    [Fact]
    public void AFemalesShirtAndPantsShareTheNakednessTheyEndTogetherAndTheLaterDeliveryIsCreditedIt()
    {
        // PsychologicallyNude: a female pawn is nude without a shirt or without pants, so neither alone ends it.
        var pants = Shirt(1) with { ID = "pants", Definition = new("Apparel_Pants"), Slot = GearSlot.SkinLegs, Groups = ["Legs"] };
        var pawn = Pawn(new(Gender.Female, true, true), Stage("Naked", ForecastApparel.Naked, 0, -6));
        Assert.Equal(0, ForecastApparel.MoodDelta(pawn, [], [Shirt(1)]));
        Assert.Equal(0, ForecastApparel.MoodDelta(pawn, [], [pants]));
        var context = Context(pawn);
        var shirt = Craft(Shirt(1)) with { Id = "shirt" };
        var legs = Craft(pants) with { Id = "pants" };
        Assert.Equal([0.06], ForecastApparel.BreakShifts(context.Needs, context.Outfits, [shirt, legs])["pawn"]);
        var outcome = Delivering(0, 0.06) with { OutfitTicks = [new("shirt", Known(Day)), new("pants", Known(2 * Day))] };
        var row = Assert.Single(ForecastValue.Of(outcome, context, Day, null, [shirt, legs]).InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
        var perDay = context.Value.WellbeingWeight * 6 / 100 + context.MoodBreaks.DaysLost([Day, 0, 0])!.Value;
        Assert.Equal((ForecastApparel.CreditKey("pawn", "Apparel_Pants"), Horizon - 2), (row.Key, row.Units));
        Assert.Equal(-perDay * (Horizon - 2), row.Value, 12);
    }

    [Fact]
    public void AWearerWithUnreadApparelFactsIsNamedAndNotCredited()
    {
        var value = Value(Context(Pawn(null, Tattered), Shirt(0.15)), Shirt(1));
        Assert.DoesNotContain(value.InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
        Assert.Contains(ForecastApparel.CreditUnassessed + "pawn", value.Findings);
    }

    [Fact]
    public void AFreshColonyWithoutAnInvestmentHorizonCreditsNothingAndDoesNotThrow()
    {
        var context = Context(Pawn(Warm, Tattered), Shirt(0.15)) with { InvestmentHorizonDays = 0 };
        Assert.DoesNotContain(Value(context, Shirt(1)).InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDeliveryAddsNothingToTheDevelopmentCount()
    {
        var consequences = ForecastCoupledSelection.CoupledConsequences(Delivering(0, 0.05), [Craft(Shirt(1))]);
        Assert.Equal(0, consequences.Development);
    }
}
