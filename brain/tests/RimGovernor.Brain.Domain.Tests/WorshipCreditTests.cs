using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using RimWorld;
using Verse;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Obs = RimGovernor.Contracts.Wire.Observations;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3808: a planned worship room is credited, per believer, the ideoligion effects it meets: a building precept's
/// missing or disrespected mood once the room is predicted to meet every requirement of its presence demand, and the
/// expected outcome memory of each ritual the room lets the colony hold at the room's quality.
/// </summary>
public sealed class WorshipCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;
    private const string Altar = "Altar_Small", Column = "Column";

    private static readonly BuildingDemandMoods Moods = new(-4, -4);

    /// <summary>A gathering held every 15 days: quality 0.2 plus the participants' and the room impressiveness's curves.</summary>
    private static readonly P.RitualDef Gathering = new()
    {
        IntervalDaysMin = 15,
        CanStartAnytime = true,
        RequiredBuildings = [Altar],
        Outcome = new RitualOutcomeModel
        {
            StartingQuality = 0.2,
            MinQuality = 0,
            MaxQuality = 1,
            Comps =
            [
                new(RitualQualityInput.Participants, [new(1, 0.1), new(5, 0.3)]),
                new(RitualQualityInput.Impressiveness, [new(0, 0), new(100, 0.4)]),
            ],
            Chances = [new(0.3, false, -5, 6), new(0.5, true, 4, 6), new(0.2, true, 8, 6)],
        },
    };

    private static BuildingDemand Missing(int area = 0) => new() { Applies = true, MinArea = area };

    private static Ideoligion Ideo(BuildingDemand? demand, params P.RitualDef[] rituals) => new(
        new IdeologyDefs { BuildingMoods = Moods, Rituals = rituals.Select((r, i) => (r, i)).ToDictionary(x => "Pattern" + x.i, x => x.r) },
        new IdeoligionFacts
        {
            IdeoId = "Ideo_1",
            BelieverPawns = [new PawnId("pawn")],
            Buildings = demand is null ? [] : [new HeldBuilding(Altar) { Demand = demand }],
            Rituals = [.. rituals.Select((_, i) => new HeldRitual { Id = "Ritual_" + i, Pattern = "Pattern" + i })],
        });

    private static PlannedRoomPrediction Prediction(int cells = 4, double impressiveness = 30) =>
        PlannedRoomPredictionTests.Closed("1", "WoodLog", "WorshipRoom", impressiveness, 0, cells, 0, false) with { Terrains = [new(["Floor"])] };

    private static ForecastCoupledContext Context(WorshipFacts worship) =>
        ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Worship },
            Closures = [new(1, "WoodLog", [new(1, 1)], [], [], Prediction())],
            LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = new() { Id = 1, Role = PlannedRole.Worship } },
            InvestmentHorizonDays = Horizon,
            Worship = worship,
        };

    /// <summary>A day in the mildest break tier unshifted, and none once shifted by <paramref name="shift"/>.</summary>
    private static ForecastCoupledOutcome Served(double shift) =>
        new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>
        {
            ["pawn"] = new([], default, default, new(new("pawn", "", new ForecastCapacityWindows.Unread(), 0), []), [])
            {
                Mood = new([], default, default, default, default, default, [])
                {
                    PressureTicks = Known(0d), BreakRisk = Known(0d), BreakTierTicks = [Day, 0, 0],
                    ShiftedBreakTierTicks = [new(shift, [0, 0, 0])],
                },
            },
        }, [])
        { ServedRooms = [new(1, "WoodLog", 0)] };

    private static ForecastValueResult Ledger(ForecastCoupledContext context)
    {
        var delta = ForecastWorship.Deltas(context).GetValueOrDefault((1, "WoodLog"));
        return ForecastValue.Of(Served(delta / 100), context, Day);
    }

    private static ForecastInvestmentContribution[] Credits(ForecastValueResult ledger) =>
        [.. ledger.InvestmentContributions.Where(r => r.Key.StartsWith("worship/", StringComparison.Ordinal))];

    private static double PerDay(ForecastCoupledContext context, double delta) =>
        context.Value.WellbeingWeight * delta / 100 + context.MoodBreaks.DaysLost([Day, 0, 0])!.Value;

    [Fact]
    public void AnIdeoligionMissingItsAltarIsCreditedThatMoodThroughGainPerDay()
    {
        var context = Context(WorshipRules.FactsOf(Ideo(Missing())));
        Assert.Equal(4, ForecastWorship.Deltas(context)[(1, "WoodLog")]);
        var row = Assert.Single(Credits(Ledger(context)));
        Assert.Equal(("pawn", "worship/1/pawn", Horizon), (row.Pawn, row.Key, row.Units));
        Assert.Equal(-PerDay(context, 4) * Horizon, row.Value, 12);
        // The believer's mood timeline integrates breaks at the worship shift.
        Assert.Contains(0.04, PlannedRoomCredits.BreakShifts(context)["pawn"]);
    }

    [Fact]
    public void ARitualEveryFifteenDaysAddsItsExpectedOutcomeAtTheRoomsQuality()
    {
        var context = Context(WorshipRules.FactsOf(Ideo(Missing(), Gathering)));
        // One participant (0.1) and impressiveness 30 (0.12) on a starting quality of 0.2.
        var quality = 0.2 + 0.1 + 0.12;
        Assert.Equal(quality, ForecastWorship.Quality(Gathering.Outcome!, 1, 30, 0), 12);
        double[] weights = [0.3, 0.5 * quality, 0.2 * quality];
        var expected = ((weights[0] * -5) + (weights[1] * 4) + (weights[2] * 8)) / weights.Sum() * 6 / 15;
        var delta = ForecastWorship.Deltas(context)[(1, "WoodLog")];
        Assert.Equal(4 + expected, delta, 12);
        var row = Assert.Single(Credits(Ledger(context)));
        Assert.Equal(-PerDay(context, delta) * Horizon, row.Value, 12);
    }

    [Fact]
    public void NoIdeoligionEarnsNothing()
    {
        var ledger = Ledger(Context(WorshipRules.FactsOf(null)));
        Assert.Empty(Credits(ledger));
        Assert.DoesNotContain(ledger.Findings, f => f.StartsWith("worship", StringComparison.Ordinal));
        // Nor does an ideoligion that holds no building precept and no ritual needing one: it owes no worship room.
        var none = WorshipRules.FactsOf(Ideo(null));
        Assert.Null(none.Need);
        Assert.Empty(Credits(Ledger(Context(none))));
    }

    [Fact]
    public void ARoomTooSmallForTheDemandOnlyTradesMissingForDisrespected()
    {
        // Area 25 against a 4-cell room: the altar stands but the room is disrespected, -4 for -4.
        var context = Context(WorshipRules.FactsOf(Ideo(Missing(area: 25))));
        Assert.Equal(0, ForecastWorship.Deltas(context)[(1, "WoodLog")]);
        Assert.Empty(Credits(Ledger(context)));
        // A demand that does not apply (the colony's expectations are below its minimum) gives no thought either way.
        Assert.Empty(Credits(Ledger(Context(WorshipRules.FactsOf(Ideo(Missing() with { Applies = false }))))));
    }

    [Fact]
    public void ARequirementOrRitualThePlanCannotPredictIsNamedNotModelled()
    {
        // #3835: a demand of a requirement kind the plan cannot predict adds nothing, named a not-modelled mechanic.
        var unread = Context(WorshipRules.FactsOf(Ideo(Missing() with { Unread = ["RoomRequirement_Modded"] })));
        var ledger = Ledger(unread);
        Assert.Contains(ForecastWorship.RequirementUnmodelled + "RoomRequirement_Modded", ledger.Findings);
        Assert.Empty(Credits(ledger));
        // A ritual whose outcome effect is not drawn from quality adds nothing beside the altar's demand, and is named.
        var worker = Gathering with { Outcome = new RitualOutcomeModel { Unread = "RitualOutcomeEffectWorker_Modded" } };
        var ritual = Context(WorshipRules.FactsOf(Ideo(Missing(), worker)));
        Assert.Equal(4, ForecastWorship.Deltas(ritual)[(1, "WoodLog")]);
        ledger = Ledger(ritual);
        Assert.Contains(ForecastWorship.RitualUnmodelled + "RitualOutcomeEffectWorker_Modded", ledger.Findings);
        Assert.Equal(-PerDay(ritual, 4) * Horizon, Assert.Single(Credits(ledger)).Value, 12);
    }

    [Fact]
    public void ThePlannedRoomMeetsADemandByItsCellsImpressivenessFloorsAndFurniture()
    {
        var demand = new BuildingDemand
        {
            Applies = true, MinArea = 25, MinImpressiveness = 60, FloorTags = ["FineFloor", "Floor"],
            Counts = [new P.ThingCount { Def = Column, Count = 2 }], ForbiddenDefs = ["Bed"],
        };
        var need = WorshipRules.WorshipRoomNeed(Ideo(demand)).Need;
        Assert.True(ForecastWorship.Meets(demand, Prediction(cells: 25, impressiveness: 59.5), need));
        Assert.False(ForecastWorship.Meets(demand, Prediction(cells: 24, impressiveness: 60), need));
        Assert.False(ForecastWorship.Meets(demand, Prediction(cells: 25, impressiveness: 59.4), need));
        Assert.False(ForecastWorship.Meets(demand, Prediction(cells: 25, impressiveness: 60) with { Terrains = [new([])] }, need));
        Assert.False(ForecastWorship.Meets(demand, Prediction(cells: 25, impressiveness: 60), need with { Furniture = [need.Furniture[0]] }));
    }

    [Fact]
    public void TheWorshipRoomIsPlannedToMeetItsDemands()
    {
        var demand = new BuildingDemand
        {
            Applies = true, MinArea = 25, MinImpressiveness = 60, FloorTags = ["FineFloor", "Floor"],
            Counts = [new P.ThingCount { Def = Column, Count = 2 }], ForbiddenDefs = ["Bed"],
        };
        var need = WorshipRules.WorshipRoomNeed(Ideo(demand)).Need;
        Assert.Equal([(Altar, 1), (Column, 2)], need.Furniture.Items.Select(f => (f.Defs[0], f.Count)));
        Assert.Equal((25, 60), (need.MinArea, need.MinImpressiveness));
        Assert.Equal(["FineFloor", "Floor"], need.FloorTags.Items);
        Assert.Equal(["Bed"], need.ForbiddenDefs.Items);
        FurnitureDefinition Def(string name) => new() { Name = name, Available = Known(true), Size = Known(new P.Bounds(1, 1)) };
        var shape = need.Shape([Def(Altar), Def(Column)])!;
        Assert.Equal(25, shape.MinArea);
        var (w, d) = LayoutChildRoomsRules.ChildRoomSizes(shape)[0];
        Assert.True(w * d >= 25, $"{w}x{d}");
        Assert.True(shape.Holds(w, d));
        Assert.False(shape.Holds(4, 4));
        Assert.Equal([Altar, Column], WorshipRules.FactsOf(Ideo(demand)).Definitions);
    }

    [Fact]
    public void TheWorshipRoomsFloorMustCarryADemandedTag()
    {
        // The reconcile hands the worship room its demand's tags (StageChildRoomAsync): a floor without one is torn up, as in
        // the throne room; without tags the worship room keeps any floor.
        var room = new PlannedRoom { Id = 1, Role = PlannedRole.Worship, Region = RegionRules.RectRegion(new(1, 1, 3, 3)) };
        var facts = new P.FlooringFacts
        {
            Definitions = new Dictionary<string, P.FloorDefinition> { ["Tile"] = new() { Tags = ["Floor"] }, ["Straw"] = new() { Tags = [] } },
        };
        var policy = FlooringRules.DefaultFlooringPolicy();
        Assert.True(WantedFloorRules.FloorKept(room, ["Floor"], facts, policy)("Tile", "Carpet"));
        Assert.False(WantedFloorRules.FloorKept(room, ["Floor"], facts, policy)("Straw", "Carpet"));
        Assert.True(WantedFloorRules.FloorKept(room, [], facts, policy)("Straw", "Carpet"));
    }

    [Fact]
    public void TheCatalogMirrorsTheDemandMoodsAndTheRitualOutcome()
    {
        var impressiveness = new RoomStatDef { defName = "Impressiveness" };
        ThoughtDef Thought(string name, float mood, float days = 0) => new() { defName = name, durationDays = days, stages = [new ThoughtStage { baseMoodEffect = mood }] };
        var good = Thought("GoodGathering", 8, 6);
        var effect = new RitualOutcomeEffectDef
        {
            defName = "Gathering",
            workerClass = typeof(RitualOutcomeEffectWorker_FromQuality),
            startingQuality = 0.2f,
            maxQuality = 1,
            comps =
            [
                new RitualOutcomeComp_ParticipantCount { curve = new SimpleCurve { new Verse.CurvePoint(1, 0.1f), new Verse.CurvePoint(5, 0.3f) } },
                new RitualOutcomeComp_RoomStat { statDef = impressiveness, curve = new SimpleCurve { new Verse.CurvePoint(0, 0), new Verse.CurvePoint(100, 0.4f) } },
            ],
            outcomeChances = [new RitualOutcomePossibility { chance = 0.3f, positivityIndex = -1 }, new RitualOutcomePossibility { chance = 0.2f, positivityIndex = 1, memory = good }],
        };
        var pattern = new RitualPatternDef { defName = "Gathering", canStartAnytime = true, ritualFreeStartIntervalDaysRange = new FloatRange(15, 30), ritualOutcomeEffect = effect };
        var catalog = new DefinitionCatalog(
            [
                new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" },
                new ThingDef { defName = "Bed", building = new BuildingProperties { buildingTags = ["Bed"] } },
                pattern, effect, good, impressiveness,
                Thought(nameof(ThoughtDefOf.IdeoBuildingMissing), -4), Thought(nameof(ThoughtDefOf.IdeoBuildingDisrespected), -3),
            ],
            DefStatTable.Empty,
            new CatalogDerived("Silver", "Wort", 10f));
        var defs = catalog.IdeologyDefs()!;
        Assert.Equal(new BuildingDemandMoods(-4, -3), defs.BuildingMoods);
        var model = defs.Rituals["Gathering"].Outcome!;
        Assert.Equal(("", 0.2, 0d, 1d), (model.Unread, model.StartingQuality, model.MinQuality, model.MaxQuality));
        Assert.Equal([RitualQualityInput.Participants, RitualQualityInput.Impressiveness], model.Comps.Select(c => c.Input));
        Assert.Equal([new(0.3, false, 0, 0), new(0.2, true, 8, 6)], model.Chances);
        Assert.Equal(15, defs.Rituals["Gathering"].IntervalDaysMin);

        // The ideology section's presence demand decodes over the same catalog, its forbidden building tags to the defs carrying them.
        var ideo = IdeologyDecoder.Decode(IdeologyReadsTests.NoPrecepts with
        {
            BelieverPawns = [new PawnId("pawn")],
            Buildings =
            [
                new Obs.IdeoBuilding
                {
                    Building = Altar, Applies = true, Present = true, Satisfied = false, MinArea = 25, MinImpressiveness = 60, FloorTags = ["Floor"],
                    Things = [new(Column, 2)], ForbiddenBuildingTags = ["Bed"], Unread = [],
                },
            ],
        }, catalog);
        var held = Assert.Single(ideo.Facts.Buildings);
        Assert.Equal((true, true, false, 25, 60), (held.Demand.Applies, held.Demand.Present, held.Demand.Satisfied, held.Demand.MinArea, held.Demand.MinImpressiveness));
        Assert.Equal(["Bed"], held.Demand.ForbiddenDefs);
        Assert.Equal([new P.ThingCount { Def = Column, Count = 2 }], held.Demand.Counts);
        Assert.Equal([new PawnId("pawn")], ideo.Facts.BelieverPawns);
    }

    [Fact]
    public void TheCoupledContextCarriesTheWorshipFactsTheRoundsRead()
    {
        // The production path (#3822): GenerateCoupledActivities builds the context from the review's facts, which RoundsRead
        // fills from the frame's ideology section (WorshipRules.FactsOf).
        var worship = WorshipRules.FactsOf(Ideo(Missing()));
        var facts = ForecastCoupledTests.ConstructionReview() with
        {
            PlannedShelters = [new() { Id = 1, Role = PlannedRole.Worship, Region = RegionRules.RectRegion(new(1, 1, 1, 1)) }],
            Worship = worship,
        };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context;
        Assert.Same(worship, context.Worship);
        Assert.Equal(PlannedRole.Worship, context.LayoutRooms[1].Role);
        // A fresh colony's review (no Ideology: the frame carries no section) credits nothing and throws nothing.
        var fresh = ForecastCoupledCandidates.GenerateCoupledActivities(ForecastCoupledTests.ConstructionReview())!.Context;
        Assert.Empty(ForecastWorship.WorshipCredit(new(1, "", Day), Served(0), [], fresh, Day));
        // With the ideoligion but no prediction of the planned room (a shape whose ring closes no room, #3834), it earns nothing.
        Assert.Empty(ForecastWorship.WorshipCredit(new(1, "", Day), Served(0), [], context, Day));
    }
}
