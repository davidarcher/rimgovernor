using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3805: a planned prison is priced by the recruitment it enables. A held prisoner without a prison bed is bedded, chatted
/// to resistance 0 and recruited <c>d</c> days after the room serves, then adds its #3825 value as a colonist a day: the credit
/// is that value times the served days less <c>d</c>.
/// </summary>
public sealed class PrisonCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 30;

    // Price every tick through the production work-cost seam, including conversion work, instead of exposing job counters.
    private static IReadOnlyList<ForecastPrison.Expected> JointRecruitment(PrisonRecruitRead read, WardenRead warden,
        double? wardenMood, IReadOnlyList<ForecastPrison.RecruitmentTarget> targets) =>
        ForecastPrison.JointRecruitment(read, warden, wardenMood,
            targets.Select(t => t with { WorkCost = t.WorkCost ?? ((_, ticks) => ticks) }).ToArray());

    private static ForecastPrison.Expected Recruitment(PrisonRecruitRead read, WardenRead warden, double? wardenMood,
        PrisonerRecruitRead prisoner, double? cell, double servedAt, double units,
        ConversionReductionRead? converter = null, double? certainty = null) =>
        JointRecruitment(read, warden, wardenMood,
            [new(prisoner, prisoner.Mood is null ? 0 : cell ?? throw new RequiredFactException("prison thought stage"),
                servedAt, units, converter, converter is null ? 0 : certainty ?? throw new RequiredFactException("prisoner certainty"))])[0];

    private static readonly MoodBreakModel Breaks = new() { Threshold = 0.35, Recovery = new MoodRecovery.Measured(.25), Tiers = [new(1, 2), new(1, 3), new(1, 5)] };

    private static readonly FoodHaulLegs Legs = new() { Field = true, Bench = true, FieldToStore = 20, SourceToBench = 10, BenchToDish = 5 };

    private static readonly NeedThoughtStage DecentCell = new("PrisonCell", "prison_cell", 3, 4, NeedsFixtures.Situational);

    private static readonly ForecastRoomService Served = new(1, "", Day);

    private static PrisonerRecruitRead Prisoner(ForecastCoupledContext context, bool recruitable = true) => new()
    {
        Pawn = "prisoner", Recruitable = recruitable, Resistance = 10, PrisonBed = false, WildMan = false,
        // Its prison thought now none, the decent cell's +4 brings its instant mood to 0.5: the mood curve is 1 there, and the
        // opinion curve 1 at opinion 0.
        Mood = new(0.5 - 0.04, 0) { Comfort = null, Beauty = null, Rest = null, Meals = [], CellStages = [DecentCell], Memories = [] },
        Opinions = [Opinion()], Conversion = null,
        Joiner = new()
        {
            Upkeep = context.Needs.Pawns[0].Upkeep,
            Mood = new(0.6, 0, 0.35) { Traits = [], RoomTraits = [], SituationalTraits = [], SituationStages = [], Memories = [] },
            Needs = context.Needs.Pawns[0].Needs,
        },
    };

    /// <summary>The prisoner's opinion of the warden "pawn": no relation, situational thought or memory, no rapport a chat gives.</summary>
    private static WardenOpinionRead Opinion(double rapport = 0, double tame = 0) =>
        new("pawn", 0, 0, 1, false) { Memories = [], Rapport = rapport, Chitchat = 0, OtherRapport = [], OtherChitchat = [], TameChance = tame };

    /// <summary>The warden "pawn" at NegotiationAbility 1, uninspired, with no inspiration it can start.</summary>
    private static WardenRead Warden(InspirationNowRead? inspired = null) =>
        new("pawn", 1) { Inspired = inspired, InspirationBlocked = false, Inspirations = [] };

    private sealed record Review(ForecastCoupledContext Context, ForecastCoupledOutcome Outcome, ForecastCommitment[] Commitments, double Horizon)
    {
        public IReadOnlyList<RoomGain> Gains() => ForecastPrison.PrisonCredit(Served, Outcome, Commitments, Context, Horizon);

        public string[] Findings() => [.. ForecastPrison.Findings(Served, Outcome, Commitments, Context, Horizon)];
    }

    /// <summary>
    /// A one-colonist construction review short of work (PawnValueTests' production path, its selection the production
    /// review's), its tier-2 bases set, and room 1 a planned prison closing a decent (Impressiveness stage 3) cell of one bed,
    /// served from day 1, the colony holding <paramref name="prisoners"/>.
    /// </summary>
    private static Review Of(Func<ForecastCoupledContext, PrisonerRecruitRead[]> prisoners, bool planned = true, bool warden = true, WardenRead? wardenRead = null, PrisonerCensus? census = null,
        double walkThere = 0, double walkBack = 0, Cell? prisonBed = null, Cell? interactionEndpoint = null)
    {
        var catalog = Game.WorkSpeedFactsTests.Catalog();
        var facts = ForecastCoupledTests.ConstructionReview(1, 100);
        var observed = facts.ForecastObserved.Coupled!;
        var needs = facts.ForecastObserved.Needs!;
        facts = facts with
        {
            Population = census is null ? facts.Population : census,
            MoodBreak = Breaks, WorkSpeeds = catalog.WorkSpeedFacts(), FoodWork = catalog.FoodWork(new HashSet<string>(StringComparer.Ordinal)),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = needs with
                {
                    Pawns = [needs.Pawns[0] with { Upkeep = NeedsFixtures.Upkeep with { Speeds = [new("ConstructionSpeed", 1.0), new("CookSpeed", 1.0), new("MoveSpeed", 4.6), new("CarryingCapacity", 75)], FoodPerDay = 1.6, Mood = new(0.6, 0.35) } }],
                },
                Coupled = observed with
                {
                    Construction = observed.Construction with
                    {
                        Options = [.. observed.Construction.Options.Select(o => o with { WorkGiver = "ConstructFinishFrames", Finish = new ConstructionFinish.Finishes(3 * Day, (o.Finish as ConstructionFinish.Finishes)?.SkillWork) })],
                    },
                },
            },
        };
        facts = ForecastCoupledCandidates.ReviewCoupledActivities(facts);
        var coupled = facts.CoupledAlternatives!;
        // The production review's selection (Rules.InspectReview): its outcome and chosen roots leave the construction short.
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new(100),
            ForecastCoupled.TierOneHorizonDays, facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []), coupled);
        var chosen = selected.Selection.Selected.ToHashSet(StringComparer.Ordinal);
        var input = ColonyModelFixtures.Devilstrand();
        input = input with { Pawns = EquatableArray.From(input.Pawns.Select(p => p with { Pawn = "pawn" })) };
        var context = coupled.Context with
        {
            ModelBasis = new(input, new() { MedicineTendsPerDay = 0 }),
            Joiners = new() { Roots = [.. coupled.Alternatives, .. coupled.Incumbents], Work = facts.WorkSpeeds, Food = facts.FoodWork },
            ColonistThoughts = new Dictionary<string, EquatableArray<MoodThought>>(StringComparer.Ordinal) { ["pawn"] = [] },
            Bed = new(true, 0), FoodHaul = planned ? Legs : null, InvestmentHorizonDays = Horizon,
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Prison },
            RoomCredits = PlannedRoomCredits.Shipped,
            Closures = [new(1, "", [], [], [], PlannedRoomPredictionTests.Closed("1", "", "PrisonCell", 40d, 3, 9, 0, false) with { Beds = ["Bed"] })],
        };
        // The colonist's needs read as tier 2 models them (ReviewColonyModel.InputOf reads the model's needs from it), at bed effectiveness 1.
        EquatableArray<NeedRate> rates = [.. input.Pawns[0].Needs.Select(n => NeedsFixtures.Rate(n.Kind, 0.5, n.Fall, n.Threshold) with
        {
            OutdoorShareByTwelfth = n.OutdoorShares, GainPerTickPerEffectiveness = 1,
        })];
        context = context with { Needs = context.Needs with { Pawns = [context.Needs.Pawns[0] with { Needs = rates }] } };
        context = context with
        {
            Needs = context.Needs with { Prison = NeedsFixtures.NoPrison with { Wardens = warden ? [wardenRead ?? Warden()] : [], Prisoners = [.. prisoners(context)] } },
        };
        // These arithmetic fixtures intentionally recruit their prospects through a named goal; a supplied census owns its own intent.
        var population = census ?? PopulationFixtures.Census(context.Needs.Prison.Prisoners.Select(p => PopulationFixtures.Held(p.Pawn, p.Recruitable)));
        if (census is null) population = population with { Colony = population.Colony with { NamedPrisoners = [.. population.Prisoners.Select(p => p.Pawn)] } };
        context = context with { PrisonerPopulation = population };
        var bed = prisonBed ?? new Cell(90, 90);
        var arrival = interactionEndpoint ?? bed;
        var origins = context.Activities.Activities.Select(t => t.Location).Concat(context.Needs.Pawns.Select(p => "cell/" + p.Location.X + "/" + p.Location.Z))
            .Where(l => l.StartsWith("cell/", StringComparison.Ordinal)).Distinct().Select(l => l.Split('/'))
            .Select(p => new Cell(int.Parse(p[1]), int.Parse(p[2]))).Append(arrival).Distinct().ToArray();
        var walks = EquatableArray.From(origins.SelectMany(o => new[]
        {
            new CoupledRoute("pawn", o, bed, new RouteTicks.Reachable(walkThere)) { FromStart = false, Kind = ProjectedRouteKind.PrisonInteraction, Arrival = new ServicePositionRead.Located(arrival) },
            new CoupledRoute("pawn", arrival, o, new RouteTicks.Reachable(walkBack)) { FromStart = false, Kind = ProjectedRouteKind.WorkReturn, Arrival = new ServicePositionRead.Located(o) },
        }));
        context = context with { PrisonBeds = new Dictionary<int, Cell[]> { [1] = [bed] }, Closures = [.. context.Closures.Select(c => c with { Prediction = c.Prediction with { Routes = walks } })] };
        return new(context, selected.Selected with { ServedRooms = [Served] },
            [.. coupled.Alternatives.Concat(coupled.Incumbents).Where(c => chosen.Contains(c.Id))], selected.Horizon);
    }

    // The warden's work one chat job takes (#3899): the shortfall rows its toil ticks a day fill, a job's share.
    private static double LostPerJob(Review review, double jobTicks = ForecastPrison.ChatJobTicks)
    {
        var shortfall = JoinerMoodRules.ShortfallOf(review.Outcome, review.Commitments, review.Context, review.Horizon);
        var pawn = review.Context.Needs.Pawns[0];
        return PawnValueRules.WorkAdded(new PawnValueFacts("pawn")
        {
            Speeds = pawn.Upkeep.Speeds.ToDictionary(s => s.Stat, s => s.Value, StringComparer.Ordinal), Incapable = pawn.Upkeep.IncapableWork,
            FreeTicksPerDay = jobTicks * 2,
        }, shortfall).Sum(s => s.TicksPerDay) / Day / 2;
    }

    [Fact]
    public void ARecruitableResistanceTenPrisonerInADecentCellIsCreditedItsValueOverTheDaysTheRoomSavesLessTheWardensChats()
    {
        var review = Of(c => [Prisoner(c)]);
        var context = review.Context;
        var shortfall = JoinerMoodRules.ShortfallOf(review.Outcome, review.Commitments, context, review.Horizon);
        Assert.NotEmpty(shortfall.Rows);
        var value = JoinerMoodRules.ValueOf("prisoner", context.Needs.Prison.Prisoners[0].Joiner, shortfall, context, review.Outcome, review.Commitments, review.Horizon).PerDay;
        Assert.True(value > 0);
        // An attempt lowers 1 × curve(0.5) 1 × curve(0) 1: ten jobs to 0, the eleventh recruits, its attempt 1,750 ticks in,
        // jobs half a day apart from the room's service.
        var days = ((10 * Day / 2) + 1750) / Day;
        var lost = LostPerJob(review);
        Assert.True(lost > 0);
        var gain = Assert.Single(review.Gains());
        Assert.Equal(("prisoner", value), (gain.Subject, gain.PerDay));
        Assert.Equal(-(value * days) - (lost * 11), gain.Once, 9);
        Assert.DoesNotContain("prison_warden_walk", review.Findings());

        // Through the #3796 seam: one investment row, the value over the served days less the recruitment's and the chats' work.
        var rows = new List<ForecastInvestmentContribution>();
        PlannedRoomCredits.Credit(review.Outcome, review.Commitments, context, review.Horizon, rows, new SortedSet<string>(StringComparer.Ordinal));
        var row = Assert.Single(rows);
        Assert.Equal(("prison/1/prisoner", Horizon - 1), (row.Key, row.Units));
        Assert.Equal(-((value * (Horizon - 1 - days)) - (lost * 11)), row.Value, 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NurseryCreditsTheSharedRecruitmentLedgerForNewAndAlreadyHousedPrisoners(bool housed)
    {
        var review = Of(c => [Prisoner(c) with { PrisonBed = housed, OwnedPrisonBedPosition = housed ? new(new(0, 0)) : null,
            Mood = Prisoner(c).Mood! with { Rest = new(.1, .28, .8, 1e-5, 1e-9, false, 1e-9, true) } }], prisonBed: new(0, 0));
        var nursery = NurseryCreditTests.Nonworker(NurseryRecipientKind.Prisoner, "Crying");
        var prisoner = review.Context.Needs.Prison.Prisoners[0];
        var recipient = nursery.Needs.Babies.OtherRecipients[0] with { Pawn = prisoner.Pawn, Mood = prisoner.Mood! };
        var baby = nursery.Needs.Babies.Babies[0] with
        { Hearers = [new() { Pawn = prisoner.Pawn, Hearing = 1, Memories = nursery.Needs.Babies.Babies[0].Hearers[0].Memories }] };
        var context = review.Context with
        {
            Needs = review.Context.Needs with
            {
                Pawns = [.. review.Context.Needs.Pawns.Select(p => p with
                { Sleep = new("warden-bed", false, false, 3e-5, .5, 21) { Position = new ServicePositionRead.Located(new(10, 0)) } }),
                    nursery.Needs.Pawns.Single(p => p.Pawn == "baby")],
                Babies = nursery.Needs.Babies with { Babies = [baby], OtherRecipients = [recipient] },
            },
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Prison, [2] = PlannedRole.Nursery },
            ColonistThoughts = new Dictionary<string, EquatableArray<MoodThought>>(review.Context.ColonistThoughts) { ["baby"] = [] },
            PlannedBeds = new Dictionary<int, IReadOnlyList<SleepingBedFacts?>> { [2] = nursery.PlannedBeds[1] },
            NurserySites = [.. nursery.NurserySites.Select(s => s with { Room = 2 })],
            Closures = [.. review.Context.Closures.Select(c => c with { Interior = [new(0, 0)], Prediction = c.Prediction with { SleepingSurfaces = [new(new(0, 0), .5, 1)] } }),
                .. nursery.Closures.Select(c => c with { Room = 2, Prediction = c.Prediction with { Room = "2" } })],
        };
        var outcome = review.Outcome with
        {
            Needs = review.Outcome.Needs.Concat(new[] { new KeyValuePair<string, ForecastNeedsResult>("baby",
                new([], new(new("baby", "", new ForecastCapacityWindows.Unread(), 0), []), [])
                { Mood = MoodResults.Flat(.2, [0, 0, 0]) with { Response = new(.2, 1, 1, .3, [new(0, Day, .2, .2, false)]) } }) }).ToDictionary(),
            ServedRooms = [new(1, "", 0), new(2, "", 0)],
        };
        Assert.Same(prisoner.Mood, context.Needs.Babies.OtherRecipients[0].Mood);
        var gains = ForecastNursery.NurseryCredit(new(2, "", 0), outcome, [], context, Day);
        var recruitment = Assert.Single(gains, g => g.Subject == "recruit/prisoner");
        Assert.True(recruitment.Once > 0);
        Assert.DoesNotContain(context.Needs.Pawns, p => p.Pawn == prisoner.Pawn);
        var disconnected = context with { Needs = context.Needs with { Babies = context.Needs.Babies with
        { Babies = [baby with { Hearers = [baby.Hearers[0] with { Hearing = 0 }] }] } } };
        Assert.DoesNotContain(ForecastNursery.NurseryCredit(new(2, "", 0), outcome, [], disconnected, Day), g => g.Subject == "recruit/prisoner");
        Assert.DoesNotContain(ForecastNursery.NurseryCredit(new(2, "", 100 * Day), outcome, [], context, Day), g => g.Subject == "recruit/prisoner");
    }

    [Fact]
    public void TheProductionPrisonCreditIncludesMealsGainedInTheCellAtEachRecruitAttempt()
    {
        var baseline = Of(c => [Prisoner(c)]);
        var fed = Of(c =>
        {
            var prisoner = Prisoner(c);
            return [prisoner with { Mood = prisoner.Mood! with { Meals = [new("pawn", 500, Day / 2, false, false)
            {
                Thoughts = [new("GoodMeal", "meal", 0, 40, new ThoughtLasting.Memory(2 * Day, false, 1, 1, false))], RoomStages = [],
            }] } }];
        });
        var gain = Assert.Single(fed.Gains());
        // Mood .9 lowers resistance by 1.4 each attempt: eight attempts reach zero, the ninth recruits, against eleven
        // without the meal. Renewal keeps one native stack; no daily fixed job or fixture-only projection supplies it.
        var days = (8 * Day / 2 + 1750) / Day;
        Assert.Equal(-(gain.PerDay * days) - LostPerJob(fed) * 9, gain.Once, 9);
        Assert.True(gain.Once > Assert.Single(baseline.Gains()).Once);
    }

    [Fact]
    public void CellMealMemoriesExpireAndRenewTheirOldestNativeGroupMember()
    {
        var fresh = NeedsFixtures.Memory("Meal", 20, Day, fades: true);
        var held = fresh with { Offset = 10, Life = new MemoryLife.Expires(Day / 2) };
        var mood = new PrisonerMoodRead(.5, 0)
        {
            Comfort = null, Beauty = null, Rest = null, Meals = [], CellStages = [], Memories = [new(.5) { Stage = 0, BedlessRenewal = null, Members = [held], FreshMembers = [fresh] }],
        };
        var meal = new PrisonMealRead("pawn", Day / 4, 2 * Day, false, false)
        {
            Thoughts = [new("Meal", "meal", 0, 40, new ThoughtLasting.Memory(Day, true, 1, .5, false))], RoomStages = [],
        };
        var exposure = new PrisonCellForecast(mood, meal, 0, false, 0);
        Assert.Equal(0, exposure.At(Day / 8));
        // The old native member is renewed, preserving +20 rather than taking the new meal's +40.
        Assert.Equal(15, exposure.At(Day / 4));
        Assert.Equal(0, exposure.At(1.5 * Day));
    }

    [Fact]
    public void CellBeautyBoundsChangeProductionRecruitmentWithoutAnInventedWanderSchedule()
    {
        Review InBeauty(double beauty)
        {
            var review = Of(c =>
            {
            var prisoner = Prisoner(c);
            return [prisoner with { Mood = prisoner.Mood! with
            {
                Beauty = new(.4, .4, .32, .08, true, 0, false)
                { Stages = [new("NeedBeauty", "need_beauty", 0, -15, NeedsFixtures.Situational),
                    new("NeedBeauty", "need_beauty", 1, -10, NeedsFixtures.Situational), new("NeedBeauty", "need_beauty", 2, -5, NeedsFixtures.Situational),
                    new("NeedBeauty", "need_beauty", 3, 5, NeedsFixtures.Situational), new("NeedBeauty", "need_beauty", 4, 10, NeedsFixtures.Situational),
                    new("NeedBeauty", "need_beauty", 5, 15, NeedsFixtures.Situational)] },
            } }];
            });
            return review with { Context = review.Context with { Closures = [.. review.Context.Closures.Select(c => c with
            { Prediction = c.Prediction with { PerceptibleBeauty = [new(new(90, 90), beauty)] } })] } };
        }
        var ugly = InBeauty(-10);
        var beautiful = InBeauty(10);
        Assert.True(Assert.Single(beautiful.Gains()).Once > Assert.Single(ugly.Gains()).Once);
        Assert.Contains("prison_cell_exposure_bounded", beautiful.Findings());
    }

    [Fact]
    public void CellComfortFollowsThePrisonersRestCycleAndChangesItsProductionCredit()
    {
        Review WithComfort(double comfort)
        {
            var review = Of(c =>
            {
                var prisoner = Prisoner(c);
                return [prisoner with { Mood = prisoner.Mood! with
                {
                    Rest = new(.1, .3, 1, 1 / Day, 3 / Day, false, 3 / Day, false),
                    Comfort = new(.1, .5, .6, .04, false, 0, false)
                    { Stages = [new("NeedComfort", "need_comfort", 0, -3, NeedsFixtures.Situational),
                        new("NeedComfort", "need_comfort", 1, 4, NeedsFixtures.Situational), new("NeedComfort", "need_comfort", 2, 6, NeedsFixtures.Situational),
                        new("NeedComfort", "need_comfort", 3, 8, NeedsFixtures.Situational), new("NeedComfort", "need_comfort", 4, 10, NeedsFixtures.Situational)] },
                } }];
            });
            return review with { Context = review.Context with { Closures = [.. review.Context.Closures.Select(c => c with
            { Prediction = c.Prediction with { SleepingSurfaces = [new(new(90, 90), comfort, 1)], MaximumComfort = comfort } })] } };
        }
        Assert.True(Assert.Single(WithComfort(1).Gains()).Once > Assert.Single(WithComfort(.05).Gains()).Once);
    }

    [Fact]
    public void GroundRestBeforeCellServiceCarriesTheActualSleepPhaseIntoComfort()
    {
        var comfort = new PrisonSeekerRead(.1, .5, .6, .6, false, 0, false)
        {
            Stages = [new("NeedComfort", "need_comfort", 0, -3, NeedsFixtures.Situational),
                new("NeedComfort", "need_comfort", 1, 4, NeedsFixtures.Situational), new("NeedComfort", "need_comfort", 2, 6, NeedsFixtures.Situational),
                new("NeedComfort", "need_comfort", 3, 8, NeedsFixtures.Situational), new("NeedComfort", "need_comfort", 4, 10, NeedsFixtures.Situational)],
        };
        var mood = new PrisonerMoodRead(.5, 0)
        {
            Comfort = comfort, Beauty = null, Meals = [], CellStages = [], Memories = [],
            Rest = new(.1, .3, 1, 1 / Day, 3 / Day, false, 3 / Day, true),
        };
        var bed = new PlannedSleepingSurface(new(90, 90), 1, 1);
        var delayed = new PrisonCellForecast(mood, null, 3, false, Day, bed);
        // Ground sleep reaches 1 at day .3, then waking fall reaches .3 at day 1. At service the next sleep
        // starts from .3. Treating the entire pre-service day as waking instead would start at zero.
        var atService = mood with { Rest = mood.Rest with { Level = .3, Asleep = true } };
        var immediate = new PrisonCellForecast(atService, null, 3, false, 0, bed);
        var exhausted = new PrisonCellForecast(mood with { Rest = mood.Rest with { Level = 0 } }, null, 3, false, 0, bed);
        Assert.Equal(immediate.At(.28 * Day), delayed.At(1.28 * Day), 8);
        Assert.NotEqual(exhausted.At(.28 * Day), delayed.At(1.28 * Day));
    }

    [Fact]
    public void AFartherPrisonChargesMoreWardenWorkPerChatThroughTheRoomCredit()
    {
        var near = Of(c => [Prisoner(c)], walkThere: 30, walkBack: 50, prisonBed: new(10, 10));
        var far = Of(c => [Prisoner(c)], walkThere: 300, walkBack: 500, prisonBed: new(50, 50));
        var a = Assert.Single(near.Gains());
        var b = Assert.Single(far.Gains());
        Assert.Equal(a.PerDay, b.PerDay, 9);
        Assert.True(b.Once < a.Once);
        Assert.Equal((a.PerDay * 270 / Day) + 11 * (LostPerJob(far, ForecastPrison.ChatJobTicks + 800) - LostPerJob(near, ForecastPrison.ChatJobTicks + 80)), a.Once - b.Once, 9);
    }

    [Fact]
    public void APrisonWithNoReturnPathCreditsNoRecruitAndNamesTheUnreachableWalk()
    {
        var review = Of(c => [Prisoner(c)]);
        var bed = review.Context.PrisonBeds[1][0];
        var context = review.Context with { Closures = [.. review.Context.Closures.Select(c => c with
        {
            Prediction = c.Prediction with { Routes = [.. c.Prediction.Routes.Select(r => r.From == bed ? r with { Ticks = RouteTicks.None } : r)] },
        })] };
        review = review with { Context = context };
        Assert.Empty(review.Gains());
        Assert.Contains("prison_warden_unreachable:prisoner", review.Findings());
    }

    [Fact]
    public void ProductionCreditUsesTheInteractionEndpointForChainedEligibilityAndTheDirectReturn()
    {
        var atBed = Of(c => [Prisoner(c)], walkThere: 300, walkBack: 500);
        var besideBed = Of(c => [Prisoner(c)], walkThere: 300, walkBack: 500, interactionEndpoint: new(89, 90));
        var expected = Assert.Single(atBed.Gains());
        var actual = Assert.Single(besideBed.Gains());
        Assert.Equal(expected.PerDay, actual.PerDay, 12);
        Assert.Equal(expected.Once, actual.Once, 12);
        var routes = besideBed.Context.Closures[0].Prediction.Routes;
        Assert.Contains(routes, r => r.From == new Cell(89, 90) && r.Kind == ProjectedRouteKind.WorkReturn);
        Assert.DoesNotContain(routes, r => r.From == new Cell(90, 90) && r.Kind == ProjectedRouteKind.WorkReturn);
    }

    [Fact]
    public void ACompletedConversionChargesItsDirectReturnToTheConverterBeforeRecruitmentStarts()
    {
        var read = NeedsFixtures.NoPrison;
        var expected = Assert.Single(JointRecruitment(read, Warden(), null,
            [new(Bare(0) with { Conversion = Conversion() }, 0, 0, 2, new("pawn", 1), 0.5)
            {
                Route = (_, _) => new(300, 500, new(1, 0)),
            }]));
        Assert.Equal(ForecastPrison.ConvertJobTicks(read) + 800, expected.ConversionCost);
        Assert.Equal(ForecastPrison.ChatJobTicks + 800, expected.WorkCost);
        Assert.Equal((Day / 2 + 300 + 1750) / Day, expected.Days, 12);
    }

    [Fact]
    public void ConversionAndRecruitmentEachPayTheirOwnRoundTrip()
    {
        var plain = Of(c => [Prisoner(c) with { Mood = null, Conversion = Conversion() }], census: ForeignCensus());
        var walked = Of(c => [Prisoner(c) with { Mood = null, Conversion = Conversion() }], census: ForeignCensus(), walkThere: 300, walkBack: 500);
        var recruitDelta = LostPerJob(walked, ForecastPrison.ChatJobTicks + 800) - LostPerJob(plain);
        var convertTicks = ForecastPrison.ConvertJobTicks(plain.Context.Needs.Prison);
        var convertDelta = LostPerJob(walked, convertTicks + 800) - LostPerJob(plain, convertTicks);
        Assert.Equal((Assert.Single(plain.Gains()).PerDay * 300 / Day) + (8 * recruitDelta) + (2 * convertDelta), Assert.Single(plain.Gains()).Once - Assert.Single(walked.Gains()).Once, 9);
    }

    [Fact]
    public void StandingAndPlannedPrisonBedsUseTheirCanonicalLocationsAndBothRouteDirections()
    {
        var current = new Cell(2, 2);
        var planned = new Cell(4, 4);
        var room = new PlannedRoom { Id = 1, Role = PlannedRole.Prison, Region = RegionRules.RectRegion(new Policy.Rectangle(1, 1, 5, 5)) };
        var sleeping = new SleepingObservation { Beds = [new()
        {
            Native = SleepingNativeFixtures.Bed("standing", prisoners: true),
            Building = SleepingNativeFixtures.Building("standing", cell: current), Humanlike = true,
        }] };
        var beds = ForecastPrison.BedsOf([room], [new("planned", 1, OpKind.Build, "Bed", "Steel", Rotation.North, [planned])], sleeping,
            [new("Bed", "Steel", false, 1, 0.75)]);
        Assert.Equal(new[] { current, planned }, beds[1]);
        var shape = new PlannedRoomShape("1", "Steel", [current, planned], []);
        var origin = new Cell(20, 20);
        var read = NeedsFixtures.NoPrison with { Prisoners = [Bare(10)] };
        var probes = ForecastPrison.RouteProbes([shape], beds, [origin], read);
        Assert.Equal(2, probes.Count(p => p.From == origin));
        Assert.Contains(probes, p => p.From == new Cell(1, 1) && p.To == planned);
        Assert.Contains(probes, p => p.From == origin && p.WorkOrigin);
        Assert.All(beds[1], b =>
        {
            Assert.Contains(probes, p => p.From == origin && p.To == b && ReferenceEquals(p.Projection, shape));
        });
    }

    [Fact]
    public void AWardenInspiredToRecruitRecruitsAtTheFirstAttempt()
    {
        var review = Of(c => [Prisoner(c)], wardenRead: Warden(new("Inspired_Recruitment", 1, 8)));
        var value = Assert.Single(review.Gains());
        Assert.Equal(-(value.PerDay * 1750 / Day) - LostPerJob(review), value.Once, 9);
    }

    [Fact]
    public void AnotherInspirationRecruitsNothingAndTheResistancePathHolds()
    {
        var read = NeedsFixtures.NoPrison;
        var prisoner = Bare(10);
        var plain = Recruitment(read, Warden(), null, prisoner, 4, Day, 29);
        var other = Recruitment(read, Warden(new("Frenzy_Work", 0, 8)), null, prisoner, 4, Day, 29);
        Assert.Equal(plain, other);
        // No mood need: the curve at 1 (InteractionWorker_RecruitAttempt), 1.5 an attempt; seven to 0, the eighth recruits.
        Assert.Equal(8, plain.WorkCost / ForecastPrison.ChatJobTicks, 12);
    }

    // A prisoner with no mood need (the mood curve at 1: 1.5) of <paramref name="resistance"/>, no rapport, opinion 0.
    private static PrisonerRecruitRead Bare(double resistance, bool wild = false, double rapport = 0, double tame = 0) => new()
    {
        Pawn = "prisoner", Recruitable = true, Resistance = resistance, PrisonBed = false, WildMan = wild, Mood = null,
        Opinions = [Opinion(rapport, tame)], Conversion = null,
        Joiner = new() { Upkeep = NeedsFixtures.Upkeep, Mood = null, Needs = [] },
    };

    [Fact]
    public void RapportRaisesTheOpinionTwoAChatAndRecruitsSooner()
    {
        var read = NeedsFixtures.NoPrison;
        var opinion = new ForecastPrison.Opinion(read, Opinion(2));
        for (var i = 0; i < 5; i++) opinion.Chat(i * 350);
        Assert.Equal(10, opinion.At(1750));
        // Opinion 10 makes the curve 1.05 from the first attempt and more after: fewer than the eight jobs of no rapport.
        Assert.True(Recruitment(read, Warden(), null, Bare(10, rapport: 2), null, 0, 29).WorkCost / ForecastPrison.ChatJobTicks < 8);
    }

    [Fact]
    public void RapportStacksToFiftyAPawnAndFadesFromSeventyPercentOfItsDuration()
    {
        var read = NeedsFixtures.NoPrison;
        var opinion = new ForecastPrison.Opinion(read, Opinion(1));
        for (var i = 0; i < 60; i++) opinion.Chat(i);
        Assert.Equal(50, opinion.At(60));
        // At 85% of the 20 days each memory counts half (InverseLerp(1, 0.7, 0.85)); past the duration none.
        Assert.Equal(25, opinion.At(0.85 * read.Rapport.DurationTicks + 30));
        Assert.Equal(0, opinion.At(read.Rapport.DurationTicks + 61));
    }

    [Fact]
    public void ARapportMemoryOfAnotherPawnOlderThanTheWardensIsTheOneTheDefLimitDrops()
    {
        var read = NeedsFixtures.NoPrison with { Rapport = NeedsFixtures.Rapport with { StackLimit = 2 } };
        var opinion = new ForecastPrison.Opinion(read, Opinion(1) with { OtherRapport = [100] });
        opinion.Chat(0);
        opinion.Chat(1);
        Assert.Equal(2, opinion.At(2));
        opinion.Chat(2);
        Assert.Equal(2, opinion.At(3));
    }

    [Fact]
    public void ACumulativeSocialMemoryMovesOneTowardZeroADay()
    {
        var group = new SocialMemoryGroupRead("Insulted", 1, 0.7) { Members = [new SocialMemoryRead.Cumulative(-5, 0, 100)] };
        var opinion = new ForecastPrison.Opinion(NeedsFixtures.NoPrison, Opinion() with { Memories = [group] });
        Assert.Equal(-5, opinion.At(0));
        Assert.Equal(-3, opinion.At(2.5 * Day));
        Assert.Equal(0, opinion.At(6 * Day));
    }

    [Fact]
    public void TheOpinionIsTheRoundedPartsTimesTheHediffFactorAndNoPositiveOneOfAHostilePawn()
    {
        var read = NeedsFixtures.NoPrison;
        Assert.Equal(8, new ForecastPrison.Opinion(read, Opinion() with { Relations = 10, Situational = 5, Factor = 0.5 }).At(0));
        Assert.Equal(0, new ForecastPrison.Opinion(read, Opinion() with { Relations = 10, Hostile = true }).At(0));
        Assert.Equal(-100, new ForecastPrison.Opinion(read, Opinion() with { Relations = -150 }).At(0));
    }

    [Fact]
    public void AMoodMemoryFadesAndExpiresOverTheRecruitment()
    {
        var group = new MemoryGroupRead(0.5) { BedlessRenewal = null, Members = [NeedsFixtures.Memory("AteWithoutTable", -10, 30000, fades: true), NeedsFixtures.Memory("AteWithoutTable", -4, null, fades: false)] };
        Assert.Equal(-7 * 1.5, ForecastPrison.MoodGroupAt(group, 0), 12);
        Assert.Equal(-4.5 * 1.5, ForecastPrison.MoodGroupAt(group, 15000), 12);
        Assert.Equal(-4, ForecastPrison.MoodGroupAt(group, 40000), 12);
        var mood = new PrisonerMoodRead(0.4, 0) { Comfort = null, Beauty = null, Rest = null, Meals = [], CellStages = [], Memories = [group] };
        Assert.Equal(0.4 + ((10.5 - 4) / 100), ForecastPrison.Mood(mood, 0, 40000)!.Value, 12);
    }

    [Theory]
    [InlineData("SleptOnGround")]
    [InlineData("SleptOutside")]
    [InlineData("SleptInCold")]
    [InlineData("SleptInHeat")]
    [InlineData("AteWithoutTable")]
    public void ABedlessMemoryStillWeighsOnMoodWhenTheRoomServesAndLowersItsProductionCredit(string thought)
    {
        var group = new MemoryGroupRead(1)
        {
            Members = [NeedsFixtures.Memory(thought, -10, Day / 4, fades: true)],
            BedlessRenewal = NeedsFixtures.Memory(thought, -20, Day, fades: true),
        };
        var mood = new PrisonerMoodRead(0.4, 0) { Comfort = null, Beauty = null, Rest = null, Meals = [], CellStages = [DecentCell], Memories = [group] };
        // The observed memory would have expired before service. Its daily renewal holds the fresh -20 there, then
        // fades over its full native day, rather than its observed quarter day. Unrelated memories still expire now.
        Assert.Equal(0.34, ForecastPrison.Mood(mood, 4, Day, Day)!.Value, 12);
        Assert.Equal(0.44, ForecastPrison.Mood(mood, 4, 1.5 * Day, Day)!.Value, 12);
        Assert.Equal(0.54, ForecastPrison.Mood(mood, 4, 2.1 * Day, Day)!.Value, 12);
        Assert.Equal(0.54, ForecastPrison.Mood(mood with { Memories = [group with { BedlessRenewal = null }] }, 4, Day, Day)!.Value, 12);
        // No elapsed bedless period: keep the observed expiry rather than granting a fresh memory.
        Assert.Equal(0.54, ForecastPrison.Mood(mood, 4, Day)!.Value, 12);

        var renewed = Of(c => [Prisoner(c) with { Resistance = 0.8, Mood = mood }]);
        var expired = Of(c => [Prisoner(c) with { Resistance = 0.8, Mood = mood with { Memories = [group with { BedlessRenewal = null }] } }]);
        Assert.True(Assert.Single(renewed.Gains()).Once < Assert.Single(expired.Gains()).Once);
        Assert.DoesNotContain("prison_bedless_memory_renewal", renewed.Findings());
    }

    [Fact]
    public void ConversionDriftAlsoRetainsABedlessMemoryUntilRoomService()
    {
        var group = new MemoryGroupRead(1)
        {
            Members = [NeedsFixtures.Memory("SleptOnGround", -40, Day / 4, fades: false)],
            BedlessRenewal = NeedsFixtures.Memory("SleptOnGround", -40, 2 * Day, fades: false),
        };
        var prisoner = Bare(0) with
        {
            Conversion = Conversion(),
            Mood = new(0.1, 0) { Comfort = null, Beauty = null, Rest = null, Meals = [], CellStages = [], Memories = [group] },
        };
        var read = NeedsFixtures.NoPrison with { CertaintyDrift = [new(0, 0), new(1, 0.2)] };
        var converter = new ConversionReductionRead("pawn", 0.25);
        var renewed = Recruitment(read, Warden(), null, prisoner, 0, Day, 9, converter, 0.45);
        var expired = Recruitment(read, Warden(), null, prisoner with { Mood = prisoner.Mood! with { Memories = [group with { BedlessRenewal = null }] } }, 0, Day, 9, converter, 0.45);
        Assert.Equal(2, renewed.ConversionCost / ForecastPrison.ConvertJobTicks(read));
        Assert.True(expired.ConversionCost / ForecastPrison.ConvertJobTicks(read) > renewed.ConversionCost / ForecastPrison.ConvertJobTicks(read));
        Assert.True(expired.Days > renewed.Days);
    }

    [Fact]
    public void AWildManIsTamedAtTheWardensChanceEachJob()
    {
        var read = NeedsFixtures.NoPrison;
        var expected = Recruitment(read, Warden(), null, Bare(0, wild: true, tame: 0.5), null, 0, 10);
        // Job k is reached with 0.5^k and tames with half of that, 1,750 ticks after it starts half a day apart.
        double days = 0, jobs = 0, alive = 1;
        for (var k = 0; k * Day / 2 + 1750 <= 10 * Day; k++)
        {
            jobs += alive;
            days += alive * 0.5 * ((k * Day / 2) + 1750) / Day;
            alive *= 0.5;
        }

        Assert.Equal(jobs, expected.WorkCost / ForecastPrison.ChatJobTicks, 9);
        Assert.Equal(days + (alive * 10), expected.Days, 9);
    }

    [Fact]
    public void AHeldWildManIsCreditedLikeARecruit()
    {
        var review = Of(c => [Prisoner(c) with { WildMan = true, Opinions = [Opinion(tame: 1)] }]);
        var gain = Assert.Single(review.Gains());
        Assert.Equal(-(gain.PerDay * 1750 / Day) - LostPerJob(review), gain.Once, 9);
    }

    [Fact]
    public void AFutureInspirationStartsAtTheMoodsMtbAndTheRecruitingDefsShare()
    {
        // At mood 1 the MTB is 10 days: a check's chance 100 / 600,000.
        Assert.Equal(1 - Math.Pow(1 - (100 / (10 * Day)), 600), ForecastPrison.Inspirations.StartChance(1, Day), 12);
        Assert.Equal(0, ForecastPrison.Inspirations.StartChance(0.49, Day));
        Assert.Equal(0, ForecastPrison.Inspirations.StartChance(null, Day));
        var read = NeedsFixtures.NoPrison;
        var never = Recruitment(read, Warden(), 1, Bare(1000), null, 0, 29);
        var inspired = Warden() with { Inspirations = [new("Inspired_Recruitment", 1, 8), new("Frenzy_Work", 3, 8)] };
        var sooner = Recruitment(read, inspired, 1, Bare(1000), null, 0, 29);
        Assert.True(sooner.Days < never.Days);
        var blocked = Recruitment(read, inspired with { InspirationBlocked = true }, 1, Bare(1000), null, 0, 29);
        Assert.Equal(never, blocked);
    }

    [Fact]
    public void ALoyalPrisonerEarnsNothing()
    {
        var review = Of(c => [Prisoner(c, recruitable: false)]);
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastPrison.Unrecruitable, review.Findings());
        Assert.Contains(ForecastPrison.NoRecruit, review.Findings());
    }

    [Fact]
    public void APrisonerTheColonyHoldsInsteadOfRecruitingEarnsNothingUntilAGoalNamesIt()
    {
        var person = PopulationFixtures.Held("prisoner");
        var census = PopulationFixtures.Census([person]);
        Assert.Null(PopulationRules.RecruitIntent(person, census.Colony));
        var held = Of(c => [Prisoner(c)], census: census);
        var shortfall = JoinerMoodRules.ShortfallOf(held.Outcome, held.Commitments, held.Context, held.Horizon);
        Assert.True(JoinerMoodRules.ValueOf("prisoner", held.Context.Needs.Prison.Prisoners[0].Joiner, shortfall, held.Context, held.Outcome, held.Commitments, held.Horizon).PerDay > 0);
        Assert.Empty(held.Gains());
        Assert.Contains(ForecastPrison.NotRecruited, held.Findings());
        Assert.Contains(ForecastPrison.NoRecruit, held.Findings());

        var named = census with { Colony = census.Colony with { NamedPrisoners = [new("prisoner")] } };
        Assert.Equal(PrisonerInteractionMode.Recruit, PopulationRules.RecruitIntent(person, named.Colony));
        var recruited = Of(c => [Prisoner(c)], census: named);
        Assert.Single(recruited.Gains());
        Assert.DoesNotContain(ForecastPrison.NotRecruited, recruited.Findings());
    }

    // A prisoner of another ideoligion the colony converts: Believers wanted makes PopulationRules.RecruitIntent set Convert (#3915).
    private static PrisonerCensus ForeignCensus() => PopulationFixtures.Census(
        [PopulationFixtures.Held("prisoner", ideo: "Ideo_other") with { Observed = PawnFixtures.Row("prisoner") with { Settings = PawnFixtures.Row("prisoner").Settings with { PolicyInputs = PawnFixtures.Row("prisoner").Settings.PolicyInputs with { Ideo = new PawnIdeo.Member("Ideo_other", [], null, 0.5) } } } }],
        colony: new PrisonerColony { Colonists = 1, BelieversWanted = true, Ideology = new ColonyIdeology.Installed(false, "Ideo_ours") });

    private static PrisonerConversionRead Conversion(double reduction = 0.25, double factor = 1) =>
        new(factor) { Converters = [new("pawn", reduction)] };

    [Fact]
    public void ConversionJobsGiveThreeSocialImpactScaledChitchatsToTheConvertersOpinionBeforeRecruitment()
    {
        var read = NeedsFixtures.NoPrison;
        // Native Chitchat's stage is 0.66; this converter's observed SocialImpact is 2.
        var prisoner = Bare(3.05) with { Conversion = Conversion(), Opinions = [Opinion() with { Chitchat = 1.32 }] };
        var converter = new ConversionReductionRead("pawn", 0.25);
        var same = Recruitment(read, Warden(), null, prisoner, null, 0, 10, converter, 0.5);
        var different = Recruitment(read, Warden(), null, prisoner, null, 0, 10, converter with { Warden = "other" }, 0.5);
        Assert.Equal(2, same.ConversionCost / ForecastPrison.ConvertJobTicks(read));
        Assert.Equal(same.ConversionCost / ForecastPrison.ConvertJobTicks(read), different.ConversionCost / ForecastPrison.ConvertJobTicks(read));
        Assert.Equal(3, same.WorkCost / ForecastPrison.ChatJobTicks);
        Assert.Equal(4, different.WorkCost / ForecastPrison.ChatJobTicks);
        Assert.Equal(0.5, different.Days - same.Days, 12);
        Assert.Equal(different, Recruitment(read, Warden(), null,
            prisoner with { Opinions = [Opinion()] }, null, 0, 10, converter, 0.5)); // blocked or nullified: native offset 0
    }

    [Fact]
    public void ConversionChitchatMergesRawOffsetWithoutResettingAgeOrAddingAGroupMember()
    {
        var read = NeedsFixtures.NoPrison;
        var old = new SocialMemoryGroupRead(read.Chitchat.Thought, 1, 0)
        {
            Members = [new SocialMemoryRead.Cumulative(1, Day - 350, 10)],
        };
        var opinion = new ForecastPrison.Opinion(read, Opinion() with { Chitchat = 0.66, Memories = [old] });
        opinion.ConversionChat(0);
        Assert.Equal(2, opinion.At(0));
        Assert.Equal(1, opinion.At(350)); // the merge did not reset the memory's almost-day-old age

        var stacked = new ForecastPrison.Opinion(read with { Chitchat = read.Chitchat with { StackedMultiplier = 0.5 } }, Opinion() with
        {
            Chitchat = 1, Memories = [old with { StackedMultiplier = 0.5, Members = [new SocialMemoryRead.Cumulative(4, 0, 10), new SocialMemoryRead.Cumulative(2, 0, 10)] }],
        });
        stacked.ConversionChat(0);
        Assert.Equal(6, stacked.At(0)); // (4 + 1) + 2/2; a third member would give another stacked result
    }

    [Fact]
    public void ConversionChitchatCapsOnlyTheDisplayedOffsetAndRecreatesDiscardedMemoriesAtAgeZero()
    {
        var read = NeedsFixtures.NoPrison;
        var opinion = new ForecastPrison.Opinion(read, Opinion() with { Chitchat = 5 });
        for (var i = 0; i < 3; i++) opinion.ConversionChat(i * 350);
        Assert.Equal(10, opinion.At(1050));
        Assert.Equal(10, opinion.At(5 * Day)); // raw 15 has decayed to 10
        Assert.Equal(9, opinion.At(6 * Day));

        var old = new SocialMemoryGroupRead(read.Chitchat.Thought, 1, 0) { Members = [new SocialMemoryRead.Cumulative(0.5, Day - 350, 10)] };
        var renewed = new ForecastPrison.Opinion(read, Opinion() with { Chitchat = 0.66, Memories = [old] });
        renewed.ConversionChat(350); // the old memory reached zero; the new one starts at this tick
        Assert.Equal(1, renewed.At(Day + 349));
        Assert.Equal(0, renewed.At(Day + 350));
    }

    [Fact]
    public void AChitchatDefLimitCanDropTheConvertersOldestMemoryEvenWhenTheChatMergedIntoIt()
    {
        var read = NeedsFixtures.NoPrison;
        var limited = read with { Chitchat = read.Chitchat with { StackLimit = 1 } };
        var old = new SocialMemoryGroupRead(read.Chitchat.Thought, 1, 0) { Members = [new SocialMemoryRead.Cumulative(2, 500, 10)] };
        var opinion = new ForecastPrison.Opinion(limited, Opinion() with
        {
            Chitchat = 0.66, Memories = [old], OtherChitchat = [new(2, 100, 10)],
        });
        opinion.ConversionChat(0);
        Assert.Equal(0, opinion.At(0)); // native checks def limits after the merge, dropping the oldest (this warden)
        opinion.ConversionChat(350);
        Assert.Equal(1, opinion.At(350)); // a new memory; now the other pawn's is older and is dropped
    }

    [Fact]
    public void TheProductionPrisonCreditValuesTheRecruitmentAndWorkSavedByConversionChitchats()
    {
        var census = ForeignCensus();
        var withChats = Of(c => [Prisoner(c) with { Resistance = 2.02, Conversion = Conversion(1), Opinions = [Opinion() with { Chitchat = 1.32 }] }], census: census);
        var blocked = Of(c => [Prisoner(c) with { Resistance = 2.02, Conversion = Conversion(1) }], census: census);
        var shortfall = JoinerMoodRules.ShortfallOf(withChats.Outcome, withChats.Commitments, withChats.Context, withChats.Horizon);
        var value = JoinerMoodRules.ValueOf("prisoner", withChats.Context.Needs.Prison.Prisoners[0].Joiner, shortfall,
            withChats.Context, withChats.Outcome, withChats.Commitments, withChats.Horizon).PerDay;
        // One conversion job gives opinion 4, then 3 after daily decay; two attempts remove 2.035 instead of 2 resistance,
        // saving half a day and one recruit job.
        Assert.Equal((0.5 * value) + LostPerJob(withChats), Assert.Single(withChats.Gains()).Once - Assert.Single(blocked.Gains()).Once, 9);
        Assert.DoesNotContain("prison_conversion_chitchat", withChats.Findings());
    }

    [Fact]
    public void AConversionTakesAttemptsUntilCertaintyIsGoneThenTheRecruitJobsStartInTheNextSlots()
    {
        var read = NeedsFixtures.NoPrison;
        var converter = new ConversionReductionRead("pawn", 0.25);
        // No mood need: no drift. Certainty 1 falls 0.25 an attempt: the fourth converts, its job the fourth slot.
        var prisoner = Bare(0) with { Conversion = Conversion() };
        // Resistance 0: the first recruit attempt, in slot 4, recruits; every attempt is 1,050 or 1,750 ticks into its job.
        var expected = Recruitment(read, Warden(), null, prisoner, null, Day, 29, converter, 1);
        Assert.Equal(((4 * Day / 2) + 1750) / Day, expected.Days, 12);
        Assert.Equal(ForecastPrison.ChatJobTicks, expected.WorkCost);
        Assert.Equal(4 * ForecastPrison.ConvertJobTicks(read), expected.ConversionCost);
        // Without the conversion it is recruited at the first slot.
        Assert.Equal(1750 / Day, Recruitment(read, Warden(), null, Bare(0), null, Day, 29).Days, 12);
    }

    [Fact]
    public void CertaintyDriftsUpEachDayByTheMoodCurveAndAChildsFactorScalesBothWays()
    {
        var read = NeedsFixtures.NoPrison;
        var converter = new ConversionReductionRead("pawn", 0.25);
        var moody = Bare(0) with { Mood = new(0.5 - 0.04, 0) { Comfort = null, Beauty = null, Rest = null, Meals = [], CellStages = [DecentCell], Memories = [] } };
        // Certainty 0.5 falls to 0 in two attempts undrifted; the mood drifts it up (0.02 a day at mood 0.5), so a third is needed.
        Assert.Equal(2, Recruitment(read, Warden(), null, Bare(0) with { Conversion = Conversion() }, null, Day, 30, converter, 0.5).ConversionCost / ForecastPrison.ConvertJobTicks(read));
        Assert.Equal(3, Recruitment(read, Warden(), null, moody with { Conversion = Conversion() }, 4, Day, 30, converter, 0.5).ConversionCost / ForecastPrison.ConvertJobTicks(read));
        // A child (change factor 2) loses twice the reduction: two attempts from 1.
        Assert.Equal(2, Recruitment(read, Warden(), null, Bare(0) with { Conversion = Conversion(factor: 2) }, null, Day, 30, converter, 1).ConversionCost / ForecastPrison.ConvertJobTicks(read));
    }

    [Fact]
    public void AConversionTheServedDaysDoNotReachLeavesThePrisonerUnrecruited()
    {
        var read = NeedsFixtures.NoPrison;
        var converter = new ConversionReductionRead("pawn", 0.01);
        var prisoner = Bare(0) with { Conversion = Conversion() };
        // Attempts at Day + k half-days + 1,050 within three days: k = 0..3.
        var expected = Recruitment(read, Warden(), null, prisoner, null, Day, 2, converter, 1);
        Assert.Equal(2, expected.Days);
        Assert.Equal(0, expected.WorkCost);
        Assert.Equal(4 * ForecastPrison.ConvertJobTicks(read), expected.ConversionCost);
    }

    [Fact]
    public void AConvertFirstPrisonerIsCreditedItsConversionDaysAndTheConvertersWorkBeforeItsRecruitment()
    {
        var census = ForeignCensus();
        var review = Of(c => [Prisoner(c) with { Mood = null, Conversion = Conversion() }], census: census);
        // The production census made it a conversion: the autopilot's rule, called.
        Assert.Same(census, review.Context.PrisonerPopulation);
        var shortfall = JoinerMoodRules.ShortfallOf(review.Outcome, review.Commitments, review.Context, review.Horizon);
        var value = JoinerMoodRules.ValueOf("prisoner", review.Context.Needs.Prison.Prisoners[0].Joiner, shortfall, review.Context, review.Outcome, review.Commitments, review.Horizon).PerDay;
        Assert.True(value > 0);
        // Certainty 0.5 is gone in two attempts; no mood need: the mood curve at 1 (1.5), so seven recruit jobs lower resistance 10 and
        // the eighth, in slot 2 + 7, recruits.
        var days = ((9 * Day / 2) + 1750) / Day;
        var gain = Assert.Single(review.Gains());
        var lost = LostPerJob(review);
        var convertLost = LostPerJob(review, 4 * ForecastPrison.ToilTicks);
        Assert.True(convertLost > 0);
        Assert.Equal(-(value * days) - (lost * 8) - (convertLost * 2), gain.Once, 9);
        var rows = new List<ForecastInvestmentContribution>();
        PlannedRoomCredits.Credit(review.Outcome, review.Commitments, review.Context, review.Horizon, rows, new SortedSet<string>(StringComparer.Ordinal));
        var row = Assert.Single(rows);
        Assert.Equal("prison/1/prisoner", row.Key);
        Assert.Equal(-((value * (Horizon - 1 - days)) - (lost * 8) - (convertLost * 2)), row.Value, 9);
        var findings = review.Findings();
        Assert.DoesNotContain(ForecastPrison.NoConverter, findings);

        // The same prisoner already of the colony's ideoligion is recruited at once: the conversion is the whole difference.
        var own = Of(c => [Prisoner(c) with { Mood = null }]);
        Assert.True(Assert.Single(own.Gains()).Once > gain.Once);
    }

    [Fact]
    public void APrisonerNoWardenCanConvertEarnsNothingAndNamesIt()
    {
        var review = Of(c => [Prisoner(c) with { Conversion = new(1) { Converters = [] } }], census: ForeignCensus());
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastPrison.NoConverter, review.Findings());
    }

    [Fact]
    public void ConvertingWithoutTheConversionReadOrTheCensusIsAnUnreadFact()
    {
        var review = Of(c => [Prisoner(c)], census: ForeignCensus());
        Assert.Equal("prisoner conversion:prisoner", Assert.Throws<RequiredFactException>(() => review.Gains()).Fact);
        var unread = Of(c => [Prisoner(c)]);
        unread = unread with { Context = unread.Context with { PrisonerPopulation = null } };
        Assert.Equal("prisoner population", Assert.Throws<RequiredFactException>(() => unread.Gains()).Fact);
    }

    [Fact]
    public void AColonyWithNoPrisonerEarnsNothing()
    {
        var review = Of(_ => []);
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastPrison.NoPrisoner, review.Findings());
    }

    [Fact]
    public void APrisonerAlreadyInAPrisonBedIsNoneTheRoomRecruits()
    {
        var review = Of(c => [Prisoner(c) with { PrisonBed = true, OwnedPrisonBedPosition = new(new(90, 90)) }]);
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastPrison.NoRecruit, review.Findings());
    }

    [Fact]
    public void BeforeTheFirstLayoutPlanTheRoomNamesThePlanItAwaitsInsteadOfThrowing()
    {
        var review = Of(c => [Prisoner(c)], planned: false);
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastPrison.AwaitingPlan, review.Findings());
    }

    [Fact]
    public void AColonyWithoutAWardenRecruitsNobody()
    {
        var review = Of(c => [Prisoner(c)], warden: false);
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastPrison.NoWarden, review.Findings());
    }

    [Fact]
    public void ThePrisonersPrisonThoughtStageMissingIsAnUnreadFact()
    {
        var review = Of(c => [Prisoner(c) with { Mood = new(0.5, 0) { Comfort = null, Beauty = null, Rest = null, Meals = [], CellStages = [], Memories = [] } }]);
        Assert.Throws<RequiredFactException>(() => review.Gains());
    }

    [Fact]
    public void AJoinersTraitThoughtTheColonistsAlsoHaveCountsOnceAtItsOwnOffset()
    {
        var mood = new JoinerMoodRead(0.5, 0, 0.35) { Traits = [new("Greedy_Room", -4)], RoomTraits = [], SituationalTraits = [], SituationStages = [], Memories = [new("AteFine", 2)] };
        var colony = new Dictionary<string, double>(StringComparer.Ordinal) { ["Greedy_Room"] = 6, ["AteWithoutTable"] = -3 };
        Assert.Equal(0.5 + ((-3 - 4 + 2) / 100d), JoinerMoodRules.MoodOf(mood, colony, [NeedsFixtures.Pawn()])!.Target, 12);
        Assert.Null(JoinerMoodRules.MoodOf(null, colony, [NeedsFixtures.Pawn()]));
    }

    [Fact]
    public void PrisonCreditPricesTheJoinersSelectedSituationAndClearsTheGenericFinding()
    {
        var review = Of(c =>
        {
            var prisoner = Prisoner(c);
            return [prisoner with { Joiner = prisoner.Joiner with { Mood = prisoner.Joiner.Mood! with
            {
                SituationStages = [new("HappyNude", JoinerSituationKind.Nude, true, false, 1, [20])],
            } } }];
        });
        review = review with { Context = review.Context with { Outfits = new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = [] } } };
        Assert.NotEmpty(review.Gains());
        Assert.DoesNotContain(ForecastPrison.SituationalTraits, review.Findings());
    }

    [Fact]
    public void AJoinersBedroomTraitThoughtsAreTheMedianOfThemInEachColonistsRoom()
    {
        EquatableArray<NeedThoughtStage> rows =
        [
            new("Greedy", "greedy", 0, -10, NeedsFixtures.Situational), new("Greedy", "greedy", 1, -5, NeedsFixtures.Situational),
            new("Greedy", "greedy", 2, 0, NeedsFixtures.Situational), new("Greedy", "greedy", 3, 0, NeedsFixtures.Situational),
            new("Jealous", "jealous", 0, -8, NeedsFixtures.Situational),
        ];
        // No room: Greedy stage 0, jealous of the 40 room. A stage-0 room (10): Greedy stage 1, jealous of 40. A stage-2 room at 60: neither.
        var colonists = new[]
        {
            NeedsFixtures.Pawn("a") with { RivalRooms = [40] },
            NeedsFixtures.Pawn("b") with { OwnedRoom = new(10, 0), RivalRooms = [40] },
            NeedsFixtures.Pawn("c") with { OwnedRoom = new(60, 2), RivalRooms = [40] },
        };
        Assert.Equal(-13, JoinerMoodRules.RoomTraits(rows, colonists), 12);
        var mood = new JoinerMoodRead(0.5, 0, 0.35) { Traits = [], RoomTraits = rows, SituationalTraits = [], SituationStages = [], Memories = [] };
        var colony = new Dictionary<string, double>(StringComparer.Ordinal) { ["Greedy"] = -20 };
        Assert.Equal(0.5 - 0.13, JoinerMoodRules.MoodOf(mood, colony, colonists)!.Target, 12);
        Assert.Throws<RequiredFactException>(() => JoinerMoodRules.RoomTraits(rows, [NeedsFixtures.Pawn() with { OwnedRoom = new(500, 5) }]));
    }
    [Fact]
    public void TwoPrisonersShareOneRecruitmentInspirationAndTheSecondUsesResistance()
    {
        var targets = new ForecastPrison.RecruitmentTarget[]
        {
            new(Bare(2), 0, 0, 5),
            new(Bare(2) with { Pawn = "second" }, 0, 0, 5),
        };
        var expected = JointRecruitment(NeedsFixtures.NoPrison,
            Warden(new("Inspired_Recruitment", 0, 8)), null, targets);
        Assert.Equal(1750 / Day, expected[0].Days, 12);
        Assert.Equal(1, expected[0].WorkCost / ForecastPrison.ChatJobTicks);
        Assert.Equal(1 + (ForecastPrison.ChatJobTicks + 1750) / Day, expected[1].Days, 12);
        Assert.Equal(3, expected[1].WorkCost / ForecastPrison.ChatJobTicks);
        Assert.All(expected, e => Assert.Equal(0, e.PrunedProbability));
    }

    [Fact]
    public void AZeroResistanceRecruitAlsoConsumesInspirationBeforeTheNextTarget()
    {
        var expected = JointRecruitment(NeedsFixtures.NoPrison,
            Warden(new("Inspired_Recruitment", 0, 8)), null,
            [new(Bare(0), 0, 0, 5), new(Bare(2) with { Pawn = "second" }, 0, 0, 5)]);
        Assert.Equal(1 + (ForecastPrison.ChatJobTicks + 1750) / Day, expected[1].Days, 12);
        Assert.Equal(3, expected[1].WorkCost / ForecastPrison.ChatJobTicks);
    }

    [Fact]
    public void TamingInspirationSurvivesAnOrdinaryRecruitAndIsConsumedByOnlyOneWildMan()
    {
        var expected = JointRecruitment(NeedsFixtures.NoPrison,
            Warden(new("Inspired_Taming", 0, 8)), null,
            [new(Bare(0), 0, 0, 1), new(Bare(0, wild: true), 0, 0, 1), new(Bare(0, wild: true), 0, 0, 1)]);
        Assert.Equal(1750 / Day, expected[0].Days, 12);
        Assert.Equal((ForecastPrison.ChatJobTicks + 1750) / Day, expected[1].Days, 12);
        Assert.Equal(1, expected[2].Days);
        Assert.Equal(2, expected[2].WorkCost / ForecastPrison.ChatJobTicks);
    }

    [Fact]
    public void RecruitmentConsumptionAllowsALaterInspirationToStart()
    {
        var warden = Warden(new("Inspired_Recruitment", 0, 8)) with
        { Inspirations = [new("Inspired_Recruitment", 1, 8)] };
        var targets = new ForecastPrison.RecruitmentTarget[]
        { new(Bare(0), 0, 0, 2), new(Bare(1000), 0, 0, 2) };
        var blocked = JointRecruitment(NeedsFixtures.NoPrison, warden with { InspirationBlocked = true }, 1, targets);
        var renewing = JointRecruitment(NeedsFixtures.NoPrison, warden, 1, targets);
        Assert.Equal(2, blocked[1].Days);
        Assert.True(renewing[1].Days < blocked[1].Days);
    }

    [Fact]
    public void ConversionDelaysParticipationInTheSharedRecruitmentInspiration()
    {
        var conversion = new ConversionReductionRead("pawn", 1);
        var expected = JointRecruitment(NeedsFixtures.NoPrison,
            Warden(new("Inspired_Recruitment", 0, 8)), null,
            [new(Bare(2) with { Conversion = Conversion() }, 0, 0, 5, conversion, 0.5),
                new(Bare(2), 0, 0, 5)]);
        Assert.Equal(1, expected[0].ConversionCost / ForecastPrison.ConvertJobTicks(NeedsFixtures.NoPrison));
        Assert.True(expected[0].Days > expected[1].Days);
        Assert.Equal((ForecastPrison.ConvertJobTicks(NeedsFixtures.NoPrison) + 1750) / Day, expected[1].Days, 12);
    }

    [Fact]
    public void LargeWildManCohortsPruneConservativelyWithAnExplicitProbabilityBound()
    {
        // One independent tame roll per target produces 8192 subsets without the approved joint bound.
        const double units = 0.49;
        var targets = Enumerable.Range(0, 13).Select(i => new ForecastPrison.RecruitmentTarget(
            Bare(0, wild: true, tame: 0.5) with { Pawn = "wild" + i }, 0, 0, units)).ToArray();
        var bounded = JointRecruitment(NeedsFixtures.NoPrison, Warden(), null, targets);
        Assert.Contains(bounded, e => e.PrunedProbability > 0);
        for (var n = 0; n < bounded.Count; n++)
        {
            // Every target can receive exactly one serialized attempt inside this horizon.
            var exactDays = 0.5 * ((n * ForecastPrison.ChatJobTicks + 1750) / Day + units);
            Assert.Equal(exactDays, bounded[n].Days, 9);
            Assert.Equal(1, bounded[n].WorkCost / ForecastPrison.ChatJobTicks, 9);
            Assert.InRange(bounded[n].PrunedProbability, 0, 1);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionRoomsAndHousedTargetsCompeteInOneNativeOrderedLedger(bool housedFirst)
    {
        var firstBed = new Cell(-1, 0);
        var secondBed = new Cell(1, 0);
        var review = Of(c =>
        [
            Prisoner(c) with { Pawn = "z-native-first", Resistance = 2, PrisonBed = housedFirst,
                OwnedPrisonBedPosition = housedFirst ? new(firstBed) : null },
            Prisoner(c) with { Pawn = "a-native-second", Resistance = 2 },
        ], wardenRead: Warden(new("Inspired_Recruitment", 0, 8)));
        var context = review.Context;
        var origins = context.Closures[0].Prediction.Routes.Select(r => r.From)
            .Concat([firstBed, secondBed, new(0, 0)]).Distinct().ToArray();
        var routes = origins.SelectMany(o => new[] { firstBed, secondBed }.SelectMany(b => new[]
        {
            new CoupledRoute("pawn", o, b, new RouteTicks.Reachable(0))
            { FromStart = false, Kind = ProjectedRouteKind.PrisonInteraction, Arrival = new ServicePositionRead.Located(b) },
            new CoupledRoute("pawn", b, o, new RouteTicks.Reachable(0))
            { FromStart = false, Kind = ProjectedRouteKind.WorkReturn, Arrival = new ServicePositionRead.Located(o) },
        }));
        var prediction = context.Closures[0].Prediction with { Routes = EquatableArray.From(routes) };
        context = context with
        {
            Needs = context.Needs with { Pawns = [context.Needs.Pawns[0] with { Location = new(0, 0) }] },
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Prison, [2] = PlannedRole.Prison },
            PrisonBeds = new Dictionary<int, Cell[]> { [1] = [secondBed], [2] = [firstBed] },
            Closures = [context.Closures[0] with { Prediction = prediction },
                context.Closures[0] with { Room = 2, Prediction = prediction with { Room = "2" } }],
        };
        var outcome = review.Outcome with { ServedRooms = [Served with { ServedAt = 0 }, new(2, "", 0)] };
        var gains = new[] { new ForecastRoomService(1, "", 0), new(2, "", 0) }
            .SelectMany(room => ForecastPrison.PrisonCredit(room, outcome, review.Commitments, context, review.Horizon)).ToArray();
        var second = Assert.Single(gains, g => g.Subject == "a-native-second");
        var loss = LostPerJob(review);
        Assert.Equal(-second.PerDay * (1 + (ForecastPrison.ChatJobTicks + 1750) / Day) - 3 * loss, second.Once, 9);
        if (housedFirst) Assert.Single(gains);
        else
        {
            var first = Assert.Single(gains, g => g.Subject == "z-native-first");
            Assert.Equal(-first.PerDay * 1750 / Day - loss, first.Once, 9);
        }
        Assert.DoesNotContain("prison_inspiration_shared", ForecastPrison.Findings(new(1, "", 0), outcome, review.Commitments, context, review.Horizon));
    }

    [Fact]
    public void ChainedJobsSelectTheNextTargetFromTheNativeInteractionEndpoint()
    {
        ForecastPrison.RecruitmentLeg? Route(string worker, Cell from) => new(0, 0, new(1, 0));
        var expected = JointRecruitment(NeedsFixtures.NoPrison,
            Warden(new("Inspired_Recruitment", 0, 8)), null,
        [
            new(Bare(0), 0, 0, 1) { Position = new(0, 0), Route = Route },
            new(Bare(0, wild: true, tame: 1), 0, 0, 1) { Position = new(-1, 0) },
            new(Bare(0, wild: true, tame: 1), 0, 0, 1) { Position = new(2, 0) },
        ]);
        // The first job ends at x=1. The x=2 target goes next, although x=-1 was closer to the starting origin.
        Assert.Equal(1750 / Day, expected[0].Days, 12);
        Assert.Equal((ForecastPrison.ChatJobTicks + 1750) / Day, expected[2].Days, 12);
        Assert.Equal((2 * ForecastPrison.ChatJobTicks + 1750) / Day, expected[1].Days, 12);
    }
    [Fact]
    public void DiscardedJointMassBoundsTheLostRecruitmentAndMaximumRemainingChats()
    {
        const double units = 1;
        var targets = Enumerable.Range(0, 13).Select(i => new ForecastPrison.RecruitmentTarget(
            Bare(0, wild: true, tame: 0.5) with { Pawn = "wild" + i }, 0, 0, units)).ToArray();
        var bounded = JointRecruitment(NeedsFixtures.NoPrison, Warden(), null, targets);
        Assert.Contains(bounded, e => e.PrunedProbability > 0);
        for (var n = 0; n < bounded.Count; n++)
        {
            // All thirteen first jobs fit before the first target's next eligibility. Subsequent starts retain those
            // half-day eligibility times, so each uncapped target has exactly these two possible attempts.
            var first = (n * ForecastPrison.ChatJobTicks + 1750) / Day;
            var exact = 0.5 * first + 0.25 * (first + 0.5) + 0.25 * units;
            var e = bounded[n];
            Assert.InRange(e.Days - exact, -1e-9, e.PrunedProbability * units + 1e-9);
            Assert.InRange(e.WorkCost / ForecastPrison.ChatJobTicks - 1.5, -1e-9, 2 * e.PrunedProbability + 1e-9);
        }
    }
}
