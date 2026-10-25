using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;
using Gender = RimGovernor.Brain.GameFacts.Defs.Gender;

namespace RimGovernor.Brain.Domain.Tests;

// #3816: a delivered garment is credited, in the Investment term, the apparel mood it changes for its wearer a day from its
// delivery to tier 2's horizon: the wellbeing of the thought delta plus the break days the shifted mood saves.
public sealed class ApparelCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;

    private static NeedThoughtStage Stage(string thought, string role, int stage, double offset)
        => new(thought, role, stage, offset, NeedsFixtures.Lasting(Day, false));

    private static GearOption Shirt(double condition) => new() { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("shirt" + condition, "Apparel_BasicShirt", "Cloth", GearSource.Worn) with { Condition = condition }), Slot = GearSlot.SkinTorso, Layers = ["OnSkin"], Groups = ["Torso", "Arms"], CareIfDamaged = true, CountsAsClothingForNudity = true };

    private static NeedsForecastPawn Pawn(NeedApparel? apparel, params NeedThoughtStage[] stages) => ForecastValueTests.Pawn() with
    {
        ThoughtStages = EquatableArray.From(stages), Apparel = apparel,
    };

    private static readonly NeedApparel Warm = NeedsFixtures.Apparel(Gender.Male, true, true);

    private static readonly NeedThoughtStage[] Tattered =
        [Stage("ApparelDamaged", ForecastApparel.Damaged, 0, -3), Stage("ApparelDamaged", ForecastApparel.Damaged, 1, -5)];

    private static ForecastCoupledContext Context(NeedsForecastPawn pawn, params GearOption[] worn) => ForecastValueTests.Context(pawn,
        outfits: new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = EquatableArray.From(worn) }) with
    { InvestmentHorizonDays = Horizon };

    private static ForecastCommitment Craft(GearOption garment) => new("craft", default)
    { ActivityCompletions = ["deliver"], OutfitChanges = [new("pawn", [garment]) { Overlay = true }] };

    /// <summary>A day in the mildest break tier unshifted, and none once shifted by <paramref name="shift"/>; the garment delivered at <paramref name="at"/>.</summary>
    private static ForecastCoupledOutcome Delivering(double at, double shift) =>
        new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult>
        {
            ["pawn"] = new([], new ForecastTerm.Unassessed("not_projected"), new ForecastTerm.Unassessed("not_projected"), new(new("pawn", "", new ForecastCapacityWindows.Unread(), 0), []), [])
            {
                Mood = MoodResults.Flat(0d, [Day, 0, 0], [new(shift, [0, 0, 0])]),
            },
        }, [])
        { OutfitTicks = [new("craft", at)] };

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
        GearOption Tainted(int i) => Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { Id = "t" + i, Tainted = true }), Slot = GearSlot.Unset, Groups = [], Layers = [] };
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
        var uniform = Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { DefName = "Apparel_Uniform" }), Slot = GearSlot.SkinLegs, Groups = ["Legs"] };
        Assert.Equal(0, ForecastApparel.MoodDelta(Pawn(Warm with { KindRequired = ["Apparel_Uniform"] }, covered), [], [uniform]));
    }

    [Fact]
    public void AnUncoveredGroinPreceptAndNakednessAreRelievedByPants()
    {
        var pants = Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { DefName = "Apparel_Pants" }), Slot = GearSlot.SkinLegs, Groups = ["Legs"] };
        var stages = new[] { Stage("GroinUncovered", ForecastApparel.GroinUncovered, 0, -4), Stage("Naked", ForecastApparel.Naked, 0, -6) };
        Assert.Equal(10, ForecastApparel.MoodDelta(Pawn(Warm, stages), [], [pants]));
        // A legless pawn counts as wearing pants: only the precept's thought is lifted.
        Assert.Equal(4, ForecastApparel.MoodDelta(Pawn(Warm with { Legs = false }, stages), [], [pants]));
    }

    [Fact]
    public void AFemalesShirtAndPantsShareTheNakednessTheyEndTogetherAndTheLaterDeliveryIsCreditedIt()
    {
        // PsychologicallyNude: a female pawn is nude without a shirt or without pants, so neither alone ends it.
        var pants = Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { Id = "pants", DefName = "Apparel_Pants" }), Slot = GearSlot.SkinLegs, Groups = ["Legs"] };
        var pawn = Pawn(NeedsFixtures.Apparel(Gender.Female, true, true), Stage("Naked", ForecastApparel.Naked, 0, -6));
        Assert.Equal(0, ForecastApparel.MoodDelta(pawn, [], [Shirt(1)]));
        Assert.Equal(0, ForecastApparel.MoodDelta(pawn, [], [pants]));
        var context = Context(pawn);
        var shirt = Craft(Shirt(1)) with { Id = "shirt" };
        var legs = Craft(pants) with { Id = "pants" };
        Assert.Equal([0.06], ForecastApparel.BreakShifts(context.Needs, context.Outfits, [shirt, legs])["pawn"]);
        var outcome = Delivering(0, 0.06) with { OutfitTicks = [new("shirt", Day), new("pants", 2 * Day)] };
        var row = Assert.Single(ForecastValue.Of(outcome, context, Day, null, [shirt, legs]).InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
        var perDay = context.Value.WellbeingWeight * 6 / 100 + context.MoodBreaks.DaysLost([Day, 0, 0])!.Value;
        Assert.Equal((ForecastApparel.CreditKey("pawn", "Apparel_Pants"), Horizon - 2), (row.Key, row.Units));
        Assert.Equal(-perDay * (Horizon - 2), row.Value, 12);
    }

    // #3836: a title's or role's unmet apparel requirement thought, active while no worn garment's def meets one of the pawn's
    // active requirements (ApparelRequirement.IsMet).
    private static readonly NeedThoughtStage[] RequirementThoughts =
        [Stage("TitleApparelRequirementNotMet", ForecastApparel.TitleApparel, 0, -4), Stage("IdeoRoleApparelRequirementNotMet", ForecastApparel.RoleApparel, 0, -4)];

    private static GearOption Crown() => Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { Id = "crown", DefName = "Apparel_Crown" }), Slot = GearSlot.Headgear, Layers = ["Overhead"], Groups = ["UpperHead"] };

    [Fact]
    public void ATitledPawnMissingTheirRequiredApparelGainsTheRequirementThoughtsMoodWhenItIsDelivered()
    {
        var titled = Warm with { Requirements = [new(ForecastApparel.TitleApparel, ["Apparel_Crown", "Apparel_Coronet"])] };
        var context = Context(Pawn(titled, RequirementThoughts), Shirt(1));
        Assert.Equal(4, ForecastApparel.MoodDelta(context.Needs.Pawns[0], context.Outfits["pawn"], [Crown()]));
        Assert.Equal([0.04], ForecastApparel.BreakShifts(context.Needs, context.Outfits, [Craft(Crown())])["pawn"]);
        var row = Assert.Single(Value(context, Crown(), shift: 0.04).InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
        var perDay = context.Value.WellbeingWeight * 4 / 100 + context.MoodBreaks.DaysLost([Day, 0, 0])!.Value;
        Assert.Equal((ForecastApparel.CreditKey("pawn", "Apparel_Crown"), Horizon - 0.5), (row.Key, row.Units));
        Assert.Equal(-perDay * (Horizon - 0.5), row.Value, 12);
        // A requirement already met by a worn garment raises no thought to lift.
        Assert.Equal(0, ForecastApparel.MoodDelta(context.Needs.Pawns[0], [Crown() with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Crown()).Origin).Value) with { Id = "worn-crown" }) }], [Crown()]));
    }

    [Fact]
    public void ARoleHoldersRequiredApparelLiftsOnlyTheRoleThought()
    {
        var holder = Warm with { Requirements = [new(ForecastApparel.RoleApparel, ["Apparel_Crown"]), new(ForecastApparel.RoleApparel, ["Apparel_Robe"])] };
        // Both role requirements must be met for Thought_IdeoRoleApparelRequirementNotMet to lift; the title thought has none.
        Assert.Equal(0, ForecastApparel.MoodDelta(Pawn(holder, RequirementThoughts), [], [Crown()]));
        var robe = Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { Id = "robe", DefName = "Apparel_Robe" }), Slot = GearSlot.Outer, Layers = ["Shell"], Groups = ["Torso"] };
        Assert.Equal(4, ForecastApparel.MoodDelta(Pawn(holder, RequirementThoughts), [Crown()], [robe]));
    }

    [Fact]
    public void APawnWithNoRequirementGainsNothingFromTheGarment()
    {
        // No title and no role (or neither DLC): the native read lists no requirement, a named state worth zero.
        var context = Context(Pawn(Warm, RequirementThoughts), Shirt(1));
        Assert.Equal(0, ForecastApparel.MoodDelta(context.Needs.Pawns[0], context.Outfits["pawn"], [Crown()]));
        Assert.DoesNotContain(Value(context, Crown()).InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
        Assert.DoesNotContain(Value(context, Crown()).Findings, f => f.StartsWith("apparel", StringComparison.Ordinal));
    }

    // #3841: a title's minimum apparel quality thought, active while a worn garment with a quality is below the minimum of a
    // title in effect (ThoughtWorker_RoyalTitleApparelMinQualityNotMet).
    private static readonly NeedThoughtStage[] QualityThought = [Stage("TitleApparelMinQualityNotMet", ForecastApparel.TitleApparelQuality, 0, -4)];

    private static readonly NeedApparel Knight = Warm with { MinimumQuality = RimGovernor.Brain.GameFacts.Defs.QualityCategory.Normal };

    [Fact]
    public void ATitledPawnInBelowMinimumApparelGainsTheMinimumQualityThoughtsMoodWhenAGarmentAtTheMinimumIsDelivered()
    {
        var context = Context(Pawn(Knight, QualityThought), Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Poor) }) });
        Assert.Equal(4, ForecastApparel.MoodDelta(context.Needs.Pawns[0], context.Outfits["pawn"], [Shirt(1)]));
        Assert.True(ForecastApparel.ChangesThoughts(context.Outfits["pawn"], [Shirt(1)]));
        Assert.Equal([0.04], ForecastApparel.BreakShifts(context.Needs, context.Outfits, [Craft(Shirt(1))])["pawn"]);
        var row = Assert.Single(Value(context, Shirt(1), shift: 0.04).InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
        var perDay = context.Value.WellbeingWeight * 4 / 100 + context.MoodBreaks.DaysLost([Day, 0, 0])!.Value;
        Assert.Equal((ForecastApparel.CreditKey("pawn", "Apparel_BasicShirt"), Horizon - 0.5), (row.Key, row.Units));
        Assert.Equal(-perDay * (Horizon - 0.5), row.Value, 12);
        // Without a title in effect the minimum is Awful, which no garment is below.
        Assert.Equal(0, ForecastApparel.MoodDelta(Pawn(Warm, QualityThought), [Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Awful) }) }], [Shirt(1)]));
    }

    [Fact]
    public void AQualitylessGarmentChangesNothing()
    {
        // TryGetQuality fails for a def without CompQuality: the worker skips it, so it neither raises nor lifts the thought.
        var crown = Crown() with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Crown()).Origin).Value) with { Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Qualityless() }) };
        var pawn = Pawn(Knight, QualityThought);
        Assert.Equal(0, ForecastApparel.MoodOf(pawn, [crown]));
        Assert.Equal(0, ForecastApparel.MoodDelta(pawn, [Shirt(1)], [crown]));
        Assert.Equal(0, ForecastApparel.MoodDelta(pawn, [Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Poor) }) }, Crown() with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Crown()).Origin).Value) with { Id = "old", Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Qualityless() }) }], [crown]));
        Assert.Equal(-4, ForecastApparel.MoodOf(pawn, [Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Poor) }) }, crown]));
    }

    // #3850: a crafted garment comes out at its crafter's quality odds (QualityUtility.GenerateQualityCreatedByPawn).
    private static GearOption Crafted(int skill) => Shirt(1) with { QualityOdds = EquatableArray.From(CraftedQualityRules.Odds(new(skill, false, 0))) };

    private static readonly NeedApparel Count = Warm with { MinimumQuality = RimGovernor.Brain.GameFacts.Defs.QualityCategory.Excellent };

    [Fact]
    public void ATitleMinimumOfExcellentIsLiftedOnlyWithTheChanceTheCrafterReachesIt()
    {
        var bill = Crafted(15);
        var reach = bill.QualityOdds[4] + bill.QualityOdds[5] + bill.QualityOdds[6];
        Assert.InRange(reach, 0.05, 0.95);
        // A Good shirt is below Excellent: the thought holds unless the crafted one reaches it.
        var context = Context(Pawn(Count, QualityThought), Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Good) }) });
        var pawn = context.Needs.Pawns[0];
        var outcomes = ForecastApparel.MoodOutcomes(pawn, [bill])!;
        Assert.Equal([-4.0, 0.0], outcomes.Select(o => o.Mood));
        Assert.Equal(1 - reach, outcomes[0].Chance, 12);
        Assert.Equal(reach, outcomes[1].Chance, 12);
        Assert.Equal(4 * reach, ForecastApparel.MoodDelta(pawn, context.Outfits["pawn"], [bill])!.Value, 12);
        Assert.True(ForecastApparel.ChangesThoughts(context.Outfits["pawn"], [bill]));
        // The outcome that keeps the thought shifts nothing; the one that lifts it is the 0.04 shift.
        Assert.Equal([0.04], ForecastApparel.BreakShifts(context.Needs, context.Outfits, [Craft(bill)])["pawn"]);
        var row = Assert.Single(Value(context, bill, shift: 0.04).InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
        var perDay = context.Value.WellbeingWeight * 4 / 100 + context.MoodBreaks.DaysLost([Day, 0, 0])!.Value;
        Assert.Equal(-reach * perDay * (Horizon - 0.5), row.Value, 12);
    }

    [Fact]
    public void ASkillThreeCraftersBillIsWorthLessThanASkillFifteenCrafters()
    {
        var context = Context(Pawn(Count, QualityThought), Shirt(1) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Shirt(1)).Origin).Value) with { Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Good) }) });
        double Credit(GearOption bill) =>
            Assert.Single(Value(context, bill, shift: 0.04).InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal)).Value;
        // Credits are gains, negative: the skilled crafter's shirt lifts the thought more often.
        Assert.True(Credit(Crafted(15)) < Credit(Crafted(3)));
        // Its stats are expected over the odds too: more armor and insulation.
        var (armor3, insulation3) = GearLoadoutRules.GearQualityMultipliers(Crafted(3));
        var (armor15, insulation15) = GearLoadoutRules.GearQualityMultipliers(Crafted(15));
        Assert.True(armor15 > armor3 && insulation15 > insulation3);
        Assert.True(armor3 < 1 && armor15 > 1);
    }

    [Fact]
    public void APawnWithoutAnApparelTrackerIsCreditedNothing()
    {
        // #3835: no apparel facts is a pawn with no apparel tracker (NativeNeedsForecast reads them whenever it has one): a named
        // state with no apparel thought, worth zero.
        var value = Value(Context(Pawn(null, Tattered), Shirt(0.15)), Shirt(1));
        Assert.DoesNotContain(value.InvestmentContributions, r => r.Key.StartsWith("apparel/", StringComparison.Ordinal));
        Assert.DoesNotContain(value.Findings, f => f.StartsWith("apparel", StringComparison.Ordinal));
    }

    [Fact]
    public void AWearerWhoseOutfitIsUnreadThrowsARequiredFact()
    {
        var context = Context(Pawn(Warm, Tattered), Shirt(0.15));
        context = context with { Outfits = new Dictionary<string, EquatableArray<GearOption>>() };
        Assert.Throws<RequiredFactException>(() => Value(context, Shirt(1)));
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
