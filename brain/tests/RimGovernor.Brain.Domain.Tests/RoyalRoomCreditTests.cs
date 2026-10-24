using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3807 (epic #3650 P7): a planned suite or throne room is credited the title room thought its titled colonist has now
// (TitleBedroomRequirementsNotMet, TitleNoPersonalBedroom; TitleThroneroomRequirementsNotMet, TitleNoThroneRoom) through
// GainPerDay, once the room's predicted Impressiveness and size meet the title's requirement. A partial match gets nothing.
public sealed class RoyalRoomCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;
    private static readonly Cell Inside = new(1, 1);

    private static NeedThoughtStage Stage(string thought, string role, double offset) => new(thought, role, 0, offset, default, false);

    /// <summary>The title room thoughts' stage rows as the needs forecast reads them for a titled colonist.</summary>
    private static readonly NeedThoughtStage[] TitleStages =
    [
        Stage("TitleBedroomRequirementsNotMet", ForecastHousing.TitleBedroomUnmetRole, -6),
        Stage("TitleNoPersonalBedroom", ForecastHousing.TitleNoBedroomRole, -6),
        Stage("TitleThroneroomRequirementsNotMet", ForecastHousing.TitleThroneUnmetRole, -8),
        Stage("TitleNoThroneRoom", ForecastHousing.TitleNoThroneRole, -8),
    ];

    private static NeedsForecastPawn Knight() => ForecastValueTests.Pawn() with { ThoughtStages = EquatableArray.From(TitleStages) };

    /// <summary>A knight's bedroom requirement: 24 cells at impressiveness 40.</summary>
    private static readonly RoyalRoomNeed KnightBedroom = new("pawn", 24, 40);

    private static ForecastCoupledContext Context(PlannedRole role, double impressiveness, int cells, RoyalRoomNeed? need, params MoodThought[] observed)
        => Context(role, impressiveness, cells, need, Known(EquatableArray.From(observed)));

    private static ForecastCoupledContext Context(PlannedRole role, double impressiveness, int cells, RoyalRoomNeed? need, Fact<EquatableArray<MoodThought>> observed)
        => ForecastValueTests.Context(Knight()) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = role },
            Closures = [new(1, "WoodLog", [Inside], ["planned/room1/wall/WoodLog"], [],
                new("1", "WoodLog", "Bedroom", impressiveness, Known(3), cells, 0, false))],
            RoyalRooms = need is null ? new Dictionary<int, RoyalRoomNeed>() : new Dictionary<int, RoyalRoomNeed> { [1] = need },
            InvestmentHorizonDays = Horizon,
            ObservedThoughts = new Dictionary<string, Fact<EquatableArray<MoodThought>>> { ["pawn"] = observed },
        };

    /// <summary>A day in the mildest break tier unshifted, and none once shifted by <paramref name="shift"/>; the room serves at <paramref name="servedAt"/>.</summary>
    private static ForecastCoupledOutcome Serving(double servedAt, double shift) =>
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
        { ServedRooms = [new(1, "WoodLog", servedAt)] };

    private static ForecastInvestmentContribution[] Rows(ForecastCoupledContext context, ForecastCoupledOutcome outcome, string role)
        => [.. ForecastValue.Of(outcome, context, Day).InvestmentContributions.Where(r => r.Key.StartsWith(role + "/", StringComparison.Ordinal))];

    [Fact]
    public void AKnightWithAnUnmetBedroomRequirementIsCreditedItsMoodThroughGainPerDayByAPlannedSuiteThatMeetsIt()
    {
        // Impressiveness 39.6 rounds to 40, as RoomRequirement_Impressiveness rounds it; 24 cells meet the area.
        var context = Context(PlannedRole.Suite, 39.6, 24, KnightBedroom, new MoodThought("TitleBedroomRequirementsNotMet", -6));
        Assert.True(context.Priced(1));
        var outcome = Serving(Day / 2, 0.06);
        var perDay = ForecastHousing.GainPerDay(6, outcome.Needs["pawn"].Mood, context.MoodBreaks, context.Value.WellbeingWeight, 1)!.Value;
        Assert.Equal(context.Value.WellbeingWeight * 6 / 100 + context.MoodBreaks.DaysLost([Day, 0, 0])!.Value, perDay, 12);
        var row = Assert.Single(Rows(context, outcome, "suite"));
        Assert.Equal(("pawn", "suite/1/pawn", Horizon - 0.5), (row.Pawn, row.Key, row.Units));
        Assert.Equal(-perDay * (Horizon - 0.5), row.Value, 12);
    }

    [Theory]
    [InlineData(39.4, 24)]
    [InlineData(80, 23)]
    public void ASuiteShortOfTheRequiredImpressivenessOrAreaEarnsNothing(double impressiveness, int cells)
    {
        var context = Context(PlannedRole.Suite, impressiveness, cells, KnightBedroom, new MoodThought("TitleBedroomRequirementsNotMet", -6));
        var ledger = ForecastValue.Of(Serving(0, 0.06), context, Day);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("suite/", StringComparison.Ordinal));
        Assert.DoesNotContain("suite_credit_unassessed:1", ledger.Findings);
    }

    [Fact]
    public void AHolderWithoutAThroneIsCreditedTheNoThroneRoomThoughtByAThroneRoomThatMeetsItsTitle()
    {
        var context = Context(PlannedRole.Throne, 60, 30, new RoyalRoomNeed("pawn", 30, 60), new MoodThought("TitleNoThroneRoom", -8));
        var outcome = Serving(0, 0.08);
        var perDay = ForecastHousing.GainPerDay(8, outcome.Needs["pawn"].Mood, context.MoodBreaks, context.Value.WellbeingWeight, 1)!.Value;
        var row = Assert.Single(Rows(context, outcome, "throne"));
        Assert.Equal(("throne/1/pawn", Horizon), (row.Key, row.Units));
        Assert.Equal(-perDay * Horizon, row.Value, 12);
    }

    [Fact]
    public void AColonistWithNoTitleRoomThoughtNowOrNoTitleEarnsNothing()
    {
        Assert.Empty(Rows(Context(PlannedRole.Suite, 60, 30, KnightBedroom), Serving(0, 0.06), "suite"));
        var untitled = ForecastValue.Of(Serving(0, 0.06), Context(PlannedRole.Suite, 60, 30, null), Day);
        Assert.DoesNotContain(untitled.InvestmentContributions, r => r.Key.StartsWith("suite/", StringComparison.Ordinal));
        Assert.DoesNotContain("suite_credit_unassessed:1", untitled.Findings);
    }

    [Fact]
    public void UnreadObservedThoughtsLeaveTheSuiteUnassessed()
    {
        var ledger = ForecastValue.Of(Serving(0, 0.06), Context(PlannedRole.Suite, 60, 30, KnightBedroom, default(Fact<EquatableArray<MoodThought>>)), Day);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("suite/", StringComparison.Ordinal));
        Assert.Contains("suite_credit_unassessed:1", ledger.Findings);
    }

    // The production path (RoundsSleepingUpkeepPlanner.PlannedOccupantsOf): the suite step's claims over the sleeping census as
    // the decode gives it (SleepingPerson.Title from RoyalTitleOf) and the owed throne need (ThroneRules.NextThroneNeed).
    private static readonly PawnId A = new("pawn");
    private static readonly PlannedRoom Standard = new() { Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new() { X = 0, Z = 0, Width = 3, Height = 4 }) };
    private static readonly PlannedRoom Suite = new() { Id = 2, Role = PlannedRole.Suite, Region = RegionRules.RectRegion(new() { X = 10, Z = 0, Width = 7, Height = 7 }) };
    private static readonly PlannedRoom ThroneRoom = new() { Id = 3, Role = PlannedRole.Throne, Region = RegionRules.RectRegion(new() { X = 20, Z = 0, Width = 8, Height = 5 }) };
    private static readonly LayoutPlan Plan = new() { Rooms = [Standard, Suite, ThroneRoom], NextRoomId = 4 };
    private static readonly RoomObservation StandingStandard = RoomFixtures.Census((RoomFixtures.State("std", RoomRole.Bedroom, beds: ["b1"]), Standard.Region.Cells()));

    private static SleepingObservation Sleeping(RoyalTitle? title) => new()
    {
        Colonists = 1,
        People = [new() { Id = A, OwnedBed = Known("b1"), Title = title }],
        Beds = [new() { Id = "b1", Definition = new("Bed"), Humanlike = true, Room = "std", Owners = [A], AccessibleTo = [A] }],
        Rooms = Known<EquatableArray<UpkeepRoom>>([new() { Id = "std", Role = RoomRole.None, Quality = new RoomQuality(30, 0, 0, 0, 30), Cells = 12, Beds = [] }]),
    };

    private static IReadOnlyDictionary<int, RoyalRoomNeed> ProductionRooms(SleepingObservation sleeping, RoyaltyFacts? royalty, TraitEffects? effects = null)
    {
        var traits = effects is null ? new Dictionary<PawnId, TraitEffects>() : new Dictionary<PawnId, TraitEffects> { [A] = effects };
        var levels = new ImpressivenessLevels { Dull = 20, Mediocre = 30, Decent = 40, SlightlyImpressive = 50 };
        var targets = RoomQualityRules.RoomQualityTargets(sleeping, traits, default, levels);
        var claims = RoomQualityRules.SuiteClaims(Plan, StandingStandard, sleeping, targets, traits, new Dictionary<PawnId, double>());
        var (throne, owed) = royalty is null ? (new ThroneNeed(), false) : ThroneRules.NextThroneNeed(royalty);
        return RoyalRoomRules.PlannedRoyalRooms(Plan, StandingStandard, sleeping, claims, owed ? throne : null);
    }

    private static RoyalRung Rung(string title, int seniority, int area, int impressiveness) => new()
    {
        Title = title, Seniority = Known(seniority), FavorNeeded = Known(seniority),
        Throne = Known(new ThroneRequirements { Things = ["Throne"], MinArea = area, MinImpressiveness = impressiveness }),
    };

    [Fact]
    public void AFreshTitledColonysPlannedSuiteAndThroneRoomAreCreditedFromTheProductionFacts()
    {
        var sleeping = Sleeping(new RoyalTitle { BedroomMinArea = 24, BedroomMinImpressiveness = 40 });
        var royalty = new RoyaltyFacts
        {
            Ladder = [Rung("Baron", 1, 30, 60), Rung("Count", 2, 40, 80)],
            Holders = new Dictionary<PawnId, EquatableArray<RoyalHolding>> { [A] = [new RoyalHolding { FactionDef = "Empire", Title = "Baron" }] },
        };
        var rooms = ProductionRooms(sleeping, royalty);
        // The baron claims the vacant suite (its standard room cannot reach the title's target) at its bedroom requirement; the
        // throne room sized for the next rung (Count) is credited against the title it holds now (Baron).
        Assert.Equal(new RoyalRoomNeed("pawn", 24, 40), rooms[Suite.Id]);
        Assert.Equal(new RoyalRoomNeed("pawn", 30, 60), Assert.Single(rooms, r => r.Key == ThroneRoom.Id).Value);

        var context = Context(PlannedRole.Suite, 40, 49, null, new MoodThought("TitleBedroomRequirementsNotMet", -6)) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Suite },
            RoyalRooms = new Dictionary<int, RoyalRoomNeed> { [1] = rooms[Suite.Id] },
        };
        Assert.Single(Rows(context, Serving(0, 0.06), "suite"));
    }

    [Fact]
    public void AColonyWithoutRoyaltyPlansNoTitledRoomAndCreditsItsSuitesNothingWithoutThrowing()
    {
        var rooms = ProductionRooms(Sleeping(null), null);
        Assert.Empty(rooms);
        var context = Context(PlannedRole.Suite, 60, 49, null) with { RoyalRooms = rooms };
        var ledger = ForecastValue.Of(Serving(0, 0.06), context, Day);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("suite/", StringComparison.Ordinal));
        Assert.DoesNotContain("suite_credit_unassessed:1", ledger.Findings);
    }

    // #3826 (epic #3650 P1, P7): a suite relieves its occupant's Greedy and Jealous bedroom thoughts too, title or not.

    /// <summary>Core's Greedy rows (ThoughtWorker_Greedy: stage 0 without a room, else the room's Impressiveness stage + 1; stages 5-10 null, rows at 0).</summary>
    private static NeedThoughtStage[] GreedyStages(bool greedy) =>
        [.. new double[] { -8, -8, -8, -6, -4, 0, 0, 0, 0, 0, 0 }.Select((o, i) => new NeedThoughtStage("Greedy", ForecastHousing.GreedyRole, i, greedy ? o : 0, default, false))];

    /// <summary>Core's Jealous row (ThoughtWorker_BedroomJealous); 0 for a colonist without the trait (CanGetThought refuses it).</summary>
    private static NeedThoughtStage JealousStage(bool jealous) => Stage("Jealous", ForecastHousing.JealousRole, jealous ? -8 : 0);

    private static ForecastCoupledContext TraitContext(NeedsForecastPawn pawn, double impressiveness, int stage, RoyalRoomNeed need, Fact<EquatableArray<MoodThought>> observed)
        => ForecastValueTests.Context(pawn) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Suite },
            Closures = [new(1, "WoodLog", [Inside], ["planned/room1/wall/WoodLog"], [],
                new("1", "WoodLog", "Bedroom", impressiveness, Known(stage), 49, 0, false))],
            RoyalRooms = new Dictionary<int, RoyalRoomNeed> { [1] = need },
            InvestmentHorizonDays = Horizon,
            ObservedThoughts = new Dictionary<string, Fact<EquatableArray<MoodThought>>> { ["pawn"] = observed },
        };

    private static readonly RoyalRoomNeed Untitled = new("pawn", 0, 0);

    private static NeedsForecastPawn Traited(bool greedy, bool jealous, params double[] rivals) => ForecastValueTests.Pawn() with
    {
        ThoughtStages = EquatableArray.From([.. GreedyStages(greedy), JealousStage(jealous)]),
        RivalRooms = EquatableArray.From(rivals),
    };

    private static double SuiteValue(ForecastCoupledContext context, double delta)
    {
        var outcome = Serving(0, delta / 100);
        var perDay = ForecastHousing.GainPerDay(delta, outcome.Needs["pawn"].Mood, context.MoodBreaks, context.Value.WellbeingWeight, 1)!.Value;
        var row = Assert.Single(Rows(context, outcome, "suite"));
        Assert.Equal(("pawn", "suite/1/pawn", Horizon), (row.Pawn, row.Key, row.Units));
        Assert.Equal(-perDay * Horizon, row.Value, 12);
        return perDay;
    }

    [Fact]
    public void AGreedyOccupantsSuiteEarnsTheGreedyMoodItRelieves()
    {
        // Greedy at -6 now (a decent room: stage 3 + 1); the suite is slightly impressive (stage 4), so Greedy stage 5, null: 0.
        var context = TraitContext(Traited(greedy: true, jealous: false), 55, 4, Untitled, Known(EquatableArray.From([new MoodThought("Greedy", -6)])));
        Assert.True(SuiteValue(context, 6) > 0);
    }

    [Fact]
    public void AGreedyOccupantsSuiteStillBelowItsWantRelievesOnlyTheDifference()
    {
        // A mediocre suite (stage 2) leaves Greedy at stage 3 (-6) against -8 now (no room: stage 0).
        var context = TraitContext(Traited(greedy: true, jealous: false), 35, 2, Untitled, Known(EquatableArray.From([new MoodThought("Greedy", -8)])));
        SuiteValue(context, 2);
    }

    [Fact]
    public void AJealousOccupantsSuiteEarnsTheJealousMoodWhenNoOtherRoomBeatsItByATenth()
    {
        var jealous = Known(EquatableArray.From([new MoodThought("Jealous", -8)]));
        // 65 - 60 = 5 < 6: the jealousy goes.
        SuiteValue(TraitContext(Traited(greedy: false, jealous: true, 20, 65), 60, 4, Untitled, jealous), 8);
        // 66 - 60 = 6 >= 6 (ThoughtWorker_BedroomJealous's |own x 0.1|): it stays, and the suite relieves nothing.
        Assert.Empty(Rows(TraitContext(Traited(greedy: false, jealous: true, 66), 60, 4, Untitled, jealous), Serving(0, 0.08), "suite"));
    }

    [Fact]
    public void AnUntraitedUntitledOccupantsSuiteEarnsZeroWithoutReadingItsThoughts()
    {
        var context = TraitContext(Traited(greedy: false, jealous: false, 90), 60, 4, Untitled, default);
        var ledger = ForecastValue.Of(Serving(0, 0.06), context, Day);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("suite/", StringComparison.Ordinal));
        Assert.DoesNotContain("suite_credit_unassessed:1", ledger.Findings);
    }

    [Fact]
    public void ATitledGreedyOccupantsSuiteEarnsBothThoughtsItRelieves()
    {
        var pawn = Knight() with { ThoughtStages = EquatableArray.From([.. TitleStages, .. GreedyStages(true), JealousStage(false)]) };
        var observed = Known(EquatableArray.From([new MoodThought("TitleBedroomRequirementsNotMet", -6), new MoodThought("Greedy", -6)]));
        SuiteValue(TraitContext(pawn, 55, 4, KnightBedroom, observed), 12);
    }

    [Fact]
    public void AGreedyOccupantsUnreadThoughtsOrRoomAreARequiredFact()
    {
        var pawn = Traited(greedy: true, jealous: false);
        Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Serving(0, 0.06), TraitContext(pawn, 55, 4, Untitled, default), Day));
        var unread = TraitContext(pawn, 55, 4, Untitled, Known(EquatableArray.From([new MoodThought("Greedy", -6)])));
        unread = unread with { Closures = [unread.Closures[0] with { Prediction = unread.Closures[0].Prediction with { Stage = default } }] };
        Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Serving(0, 0.06), unread, Day));
    }

    [Fact]
    public void AnUntitledGreedyColonistsPlannedSuiteIsServedFromTheProductionFacts()
    {
        // The suite step's claim (a Greedy target above the standard room's reach) names the untitled occupant, with no requirement.
        var rooms = ProductionRooms(Sleeping(null), null, new TraitEffects { Greedy = true });
        Assert.Equal(Untitled, rooms[Suite.Id]);
        Assert.DoesNotContain(ThroneRoom.Id, rooms.Keys);
    }

    [Fact]
    public void AFreshColonysProductionContextCreditsItsSuiteNothingWithoutThrowing()
    {
        // The production path (#3822): GenerateCoupledActivities builds the context from the review's facts, and
        // RoundsSleepingUpkeepPlanner.PlannedOccupantsOf adds the suite occupants (RoyalRoomRules.PlannedRoyalRooms): a fresh
        // colony without Greedy, Jealous or titled colonists houses nobody in its suite.
        var facts = ForecastCoupledTests.ConstructionReview() with
        {
            PlannedShelters = [new() { Id = 2, Role = PlannedRole.Suite, Region = Suite.Region }],
        };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context;
        context = context with { RoyalRooms = ProductionRooms(Sleeping(null), null) };
        Assert.Equal(PlannedRole.Suite, context.PlannedRoomRoles[2]);
        Assert.Empty(ForecastHousing.RoyalRoomCredit(new(2, "", Day), Serving(0, 0.06), context, Day)!);
        Assert.Empty(ForecastHousing.BreakShifts(context));
    }
}
