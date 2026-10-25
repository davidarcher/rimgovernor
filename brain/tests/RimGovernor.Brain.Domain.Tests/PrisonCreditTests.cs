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

    private static readonly MoodBreakModel Breaks = new() { Threshold = 0.35, Tiers = [new(1, 2), new(1, 3), new(1, 5)] };

    private static readonly FoodHaulLegs Legs = new() { Field = true, Bench = true, FieldToStore = 20, SourceToBench = 10, BenchToDish = 5 };

    private static readonly NeedThoughtStage DecentCell = new("PrisonCell", "prison_cell", 3, 4, NeedsFixtures.Situational);

    private static readonly ForecastRoomService Served = new(1, "", Day);

    private static PrisonerRecruitRead Prisoner(ForecastCoupledContext context, bool recruitable = true) => new()
    {
        Pawn = "prisoner", Recruitable = recruitable, Resistance = 10, PrisonBed = false, WildMan = false,
        // Its prison thought now none, the decent cell's +4 brings its instant mood to 0.5: the mood curve is 1 there, and the
        // opinion curve 1 at opinion 0.
        Mood = new(0.5 - 0.04, 0) { CellStages = [DecentCell], Memories = [] },
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
        double walkThere = 0, double walkBack = 0, Cell? prisonBed = null)
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
        var origins = context.Activities.Activities.Select(t => t.Location).Concat(context.Needs.Pawns.Select(p => "cell/" + p.Location.X + "/" + p.Location.Z))
            .Where(l => l.StartsWith("cell/", StringComparison.Ordinal)).Distinct().Select(l => l.Split('/'))
            .Select(p => new Cell(int.Parse(p[1]), int.Parse(p[2]))).ToArray();
        var walks = EquatableArray.From(origins.SelectMany(o => new[]
        {
            new CoupledRoute("pawn", o, bed, new RouteTicks.Reachable(walkThere)) { FromStart = false },
            new CoupledRoute("pawn", bed, o, new RouteTicks.Reachable(walkBack)) { FromStart = false },
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

    [Fact]
    public void AFartherPrisonChargesMoreWardenWorkPerChatThroughTheRoomCredit()
    {
        var near = Of(c => [Prisoner(c)], walkThere: 30, walkBack: 50, prisonBed: new(10, 10));
        var far = Of(c => [Prisoner(c)], walkThere: 300, walkBack: 500, prisonBed: new(50, 50));
        var a = Assert.Single(near.Gains());
        var b = Assert.Single(far.Gains());
        Assert.Equal(a.PerDay, b.PerDay, 9);
        Assert.True(b.Once < a.Once);
        Assert.Equal(11 * (LostPerJob(far, ForecastPrison.ChatJobTicks + 800) - LostPerJob(near, ForecastPrison.ChatJobTicks + 80)), a.Once - b.Once, 9);
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
    public void ConversionAndRecruitmentEachPayTheirOwnRoundTrip()
    {
        var plain = Of(c => [Prisoner(c) with { Mood = null, Conversion = Conversion() }], census: ForeignCensus());
        var walked = Of(c => [Prisoner(c) with { Mood = null, Conversion = Conversion() }], census: ForeignCensus(), walkThere: 300, walkBack: 500);
        var recruitDelta = LostPerJob(walked, ForecastPrison.ChatJobTicks + 800) - LostPerJob(plain);
        var convertTicks = ForecastPrison.ConvertJobTicks(plain.Context.Needs.Prison);
        var convertDelta = LostPerJob(walked, convertTicks + 800) - LostPerJob(plain, convertTicks);
        Assert.Equal((8 * recruitDelta) + (2 * convertDelta), Assert.Single(plain.Gains()).Once - Assert.Single(walked.Gains()).Once, 9);
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
        Assert.Equal(4, probes.Count);
        Assert.All(beds[1], b =>
        {
            Assert.Contains(probes, p => p.From == origin && p.To == b && !p.Return && ReferenceEquals(p.Projection, shape));
            Assert.Contains(probes, p => p.From == origin && p.To == b && p.Return && ReferenceEquals(p.Projection, shape));
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
        var plain = ForecastPrison.Recruitment(read, Warden(), null, prisoner, 4, Day, 29);
        var other = ForecastPrison.Recruitment(read, Warden(new("Frenzy_Work", 0, 8)), null, prisoner, 4, Day, 29);
        Assert.Equal(plain, other);
        // No mood need: the curve at 1 (InteractionWorker_RecruitAttempt), 1.5 an attempt; seven to 0, the eighth recruits.
        Assert.Equal(8, plain.Jobs, 12);
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
        Assert.True(ForecastPrison.Recruitment(read, Warden(), null, Bare(10, rapport: 2), null, 0, 29).Jobs < 8);
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
        var mood = new PrisonerMoodRead(0.4, 0) { CellStages = [], Memories = [group] };
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
        var mood = new PrisonerMoodRead(0.4, 0) { CellStages = [DecentCell], Memories = [group] };
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
            Mood = new(0.1, 0) { CellStages = [], Memories = [group] },
        };
        var read = NeedsFixtures.NoPrison with { CertaintyDrift = [new(0, 0), new(1, 0.2)] };
        var converter = new ConversionReductionRead("pawn", 0.25);
        var renewed = ForecastPrison.Converted(read, prisoner, converter, 0, Day, 10 * Day, 0.45);
        var expired = ForecastPrison.Converted(read, prisoner with { Mood = prisoner.Mood! with { Memories = [group with { BedlessRenewal = null }] } }, converter, 0, Day, 10 * Day, 0.45);
        Assert.Equal((2, 2d), renewed);
        Assert.True(expired.Attempts > renewed.Attempts);
    }

    [Fact]
    public void AWildManIsTamedAtTheWardensChanceEachJob()
    {
        var read = NeedsFixtures.NoPrison;
        var expected = ForecastPrison.Recruitment(read, Warden(), null, Bare(0, wild: true, tame: 0.5), null, 0, 10);
        // Job k is reached with 0.5^k and tames with half of that, 1,750 ticks after it starts half a day apart.
        double days = 0, jobs = 0, alive = 1;
        for (var k = 0; k * Day / 2 + 1750 <= 10 * Day; k++)
        {
            jobs += alive;
            days += alive * 0.5 * ((k * Day / 2) + 1750) / Day;
            alive *= 0.5;
        }

        Assert.Equal(jobs, expected.Jobs, 9);
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
        var never = ForecastPrison.Recruitment(read, Warden(), 1, Bare(1000), null, 0, 29);
        var inspired = Warden() with { Inspirations = [new("Inspired_Recruitment", 1, 8), new("Frenzy_Work", 3, 8)] };
        var sooner = ForecastPrison.Recruitment(read, inspired, 1, Bare(1000), null, 0, 29);
        Assert.True(sooner.Days < never.Days);
        var blocked = ForecastPrison.Recruitment(read, inspired with { InspirationBlocked = true }, 1, Bare(1000), null, 0, 29);
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
        var opinion = new ForecastPrison.Opinion(read, prisoner.Opinions[0]);
        Assert.Equal((2, 2d), ForecastPrison.Converted(read, prisoner, converter, null, 0, 10 * Day, 0.5, opinion));
        Assert.Equal(8, opinion.At((Day / 2) + (2 * 350))); // six chats, raw 7.92
        Assert.Equal(7, opinion.At(Day + 1750)); // one daily decay before the first recruit attempt
        var same = ForecastPrison.Recruitment(read, Warden(), null, prisoner, null, 0, 10, converter, 0.5);
        var different = ForecastPrison.Recruitment(read, Warden(), null, prisoner, null, 0, 10, converter with { Warden = "other" }, 0.5);
        Assert.Equal(3, same.Jobs);
        Assert.Equal(4, different.Jobs);
        Assert.Equal(0.5, different.Days - same.Days, 12);
        Assert.Equal(different, ForecastPrison.Recruitment(read, Warden(), null,
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
        Assert.Equal((4, 4d), ForecastPrison.Converted(read, prisoner, converter, null, Day, Day + (30 * Day), 1));
        // Resistance 0: the first recruit attempt, in slot 4, recruits; every attempt is 1,050 or 1,750 ticks into its job.
        var expected = ForecastPrison.Recruitment(read, Warden(), null, prisoner, null, Day, 29, converter, 1);
        Assert.Equal(new ForecastPrison.Expected(((4 * Day / 2) + 1750) / Day, 1, 4), expected);
        // Without the conversion it is recruited at the first slot.
        Assert.Equal(1750 / Day, ForecastPrison.Recruitment(read, Warden(), null, Bare(0), null, Day, 29).Days, 12);
    }

    [Fact]
    public void CertaintyDriftsUpEachDayByTheMoodCurveAndAChildsFactorScalesBothWays()
    {
        var read = NeedsFixtures.NoPrison;
        var converter = new ConversionReductionRead("pawn", 0.25);
        var moody = Bare(0) with { Mood = new(0.5 - 0.04, 0) { CellStages = [DecentCell], Memories = [] } };
        // Certainty 0.5 falls to 0 in two attempts undrifted; the mood drifts it up (0.02 a day at mood 0.5), so a third is needed.
        Assert.Equal((2, 2d), ForecastPrison.Converted(read, Bare(0) with { Conversion = Conversion() }, converter, null, Day, 31 * Day, 0.5));
        Assert.Equal((3, 3d), ForecastPrison.Converted(read, moody with { Conversion = Conversion() }, converter, 4, Day, 31 * Day, 0.5));
        // A child (change factor 2) loses twice the reduction: two attempts from 1.
        Assert.Equal((2, 2d), ForecastPrison.Converted(read, Bare(0) with { Conversion = Conversion(factor: 2) }, converter, null, Day, 31 * Day, 1));
    }

    [Fact]
    public void AConversionTheServedDaysDoNotReachLeavesThePrisonerUnrecruited()
    {
        var read = NeedsFixtures.NoPrison;
        var converter = new ConversionReductionRead("pawn", 0.01);
        var prisoner = Bare(0) with { Conversion = Conversion() };
        // Attempts at Day + k half-days + 1,050 within three days: k = 0..3.
        Assert.Equal(new ForecastPrison.Expected(2, 0, 4), ForecastPrison.Recruitment(read, Warden(), null, prisoner, null, Day, 2, converter, 1));
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
        var review = Of(c => [Prisoner(c) with { PrisonBed = true }]);
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
        var review = Of(c => [Prisoner(c) with { Mood = new(0.5, 0) { CellStages = [], Memories = [] } }]);
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
}
