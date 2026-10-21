using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The joiners and anomaly families (#3302): the rest of the old population Concern. Each Need has a pure met,
/// unmet and unknown predicate; joiner letters and offers, door closes and extract flags are Need-owned Methods,
/// two doors close side by side, and an extract flag on a pawn another Need's settings hold is a claim conflict.
/// </summary>
public sealed class JoinersAnomalyFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    /// <summary>Room for one more colonist: nobody hosted, twenty days of food and a spare unowned bed.</summary>
    private static RoundsFacts Room(RoundsFacts f) => f with
    {
        Custody = Known<EquatableArray<CustodyFacts>>([]),
        FoodDays = Known(20.0),
        Sleeping = Known(new SleepingObservation
        {
            Beds = [new SleepingBed { Id = "bed", Humanlike = true }],
        }),
    };

    private static JoinerLetterOffer Letter(int id) => new() { Id = id, Token = "t" + id, Label = "Accept", CanAccept = true };

    private static RoundsFacts Census(params JoinerLetterOffer[] letters) => Room(new RoundsFacts
    {
        QuestOffers = Known<EquatableArray<JoinerOffer>>([]),
        JoinerLetters = Known<EquatableArray<JoinerLetterOffer>>([.. letters]),
        Royalty = Known(new RoyaltyFacts()),
    });

    private static Need Joiner(RoundsFacts f, NeedId id) => Assert.Single(new JoinersFamily().Derive(JoinersFacts.From(f)), n => n.Id == id);

    [Fact]
    public void JoinerNeedsAreMetUnmetOrUnknownByTheirCensus()
    {
        var met = Census();
        Assert.All(new JoinersFamily().Derive(JoinersFacts.From(met)), n =>
        {
            Assert.Equal((Satisfaction.Met, 0.0, NeedTier.Development), (n.Satisfaction, n.Deficit.Value, n.Tier));
            Assert.Equal(JoinersFamily.FamilyId, n.Id.Family);
        });

        var unmet = Joiner(Census(Letter(7)), JoinersFamily.Letters);
        Assert.Equal((Satisfaction.Unmet, 1.0), (unmet.Satisfaction, unmet.Deficit.Value));

        // A letter the colony may have no room for, or an unread census, never reads as settled.
        Assert.Equal(Satisfaction.Unknown, Joiner(Census(Letter(7)) with { FoodDays = default }, JoinersFamily.Letters).Satisfaction);
        Assert.All(new JoinersFamily().Derive(new JoinersFacts()), n =>
        {
            Assert.Equal(Satisfaction.Unknown, n.Satisfaction);
            Assert.False(n.Deficit.IsKnown);
        });
        Assert.Equal(Satisfaction.Unknown, Joiner(Census() with { Royalty = default }, JoinersFamily.Ceremony).Satisfaction);
    }

    [Fact]
    public void AJoinerLetterIsItsOwnMethodAndTheCeremonyIsHeld()
    {
        var family = new JoinersFamily();
        Need Ready(NeedId id) => new() { Id = id, Tier = NeedTier.Development, Satisfaction = Satisfaction.Unmet };
        var methods = family.Propose([Ready(JoinersFamily.Letters), Ready(JoinersFamily.Ceremony)], JoinersFacts.From(Census(Letter(9), Letter(7))));
        Assert.Equal(2, methods.Count);
        Assert.Equal(("joiner-letter-7", JoinersFamily.Letters), (methods[0].Method.Value, methods[0].Need));
        Assert.Equal(JoinersFamily.CeremonyHeld, methods[1].Hold!.Subject);
        Assert.Equal("joiner-accept-q1", JoinersFamily.AcceptMethod("joiner", new("q1")).Value);
    }

    private static readonly ResearchFacts Researched = new() { Finished = [EntityBioferriteRules.ResearchBioferriteExtraction] };

    private static BuiltHolder Holder(string pawn, int x, bool holdOpen) => new()
    {
        HeldPawn = pawn,
        Doors = Known<EquatableArray<ContainmentDoor>>([new ContainmentDoor
        {
            Cell = new(x, 1), HoldOpen = Known(holdOpen), Breached = Known(false), BlockedOpen = Known(false),
        }]),
    };

    private static CapturableEntity Held(string pawn, bool flag = false) => new()
    {
        Pawn = new(pawn),
        Dead = Known(false),
        Downed = Known(false),
        CanBeCaptured = Known(true),
        Held = Known(true),
        Need = Known(10.0),
        NeedsTend = Known(false),
        Bleeding = Known(false),
        Escaping = Known(false),
        CurrentlyStudiable = Known(false),
        Mode = Known(ContainmentMode.Study),
        ExtractBioferrite = Known(flag),
        HarvesterAttached = Known(false),
        BioferritePerDay = Known(1.0),
    };

    private static RoundsFacts Containment(BuiltHolder[] holders, params CapturableEntity[] entities) => new()
    {
        Containment = new ContainmentPlanning
        {
            Holders = Known<EquatableArray<BuiltHolder>>([.. holders]),
            Entities = Known<EquatableArray<CapturableEntity>>([.. entities]),
        },
        Research = Known(Researched),
        Gear = Known(new GearObservation()),
        Monolith = Known(new MonolithFacts()),
    };

    private static Need Anomaly(RoundsFacts f, NeedId id) => Assert.Single(new AnomalyFamily().Derive(AnomalyFacts.From(f)), n => n.Id == id);

    [Fact]
    public void AnomalyNeedsAreMetUnmetOrUnknownByTheirCensus()
    {
        var met = Containment([Holder("e1", 1, holdOpen: false)], Held("e1", flag: true));
        Assert.All(new AnomalyFamily().Derive(AnomalyFacts.From(met)), n =>
        {
            Assert.Equal(Satisfaction.Met, n.Satisfaction);
            Assert.Equal(AnomalyFamily.FamilyId, n.Id.Family);
        });
        Assert.Equal(NeedTier.Wellbeing, Anomaly(met, AnomalyFamily.Doors).Tier);
        Assert.Equal(NeedTier.Development, Anomaly(met, AnomalyFamily.Bioferrite).Tier);

        var unmet = Containment([Holder("e1", 1, holdOpen: true), Holder("e2", 3, holdOpen: true)], Held("e1"), Held("e2"));
        var doors = Anomaly(unmet, AnomalyFamily.Doors);
        Assert.Equal((Satisfaction.Unmet, 2.0), (doors.Satisfaction, doors.Deficit.Value));
        Assert.Equal((Satisfaction.Unmet, 2.0), (Anomaly(unmet, AnomalyFamily.Bioferrite).Satisfaction, Anomaly(unmet, AnomalyFamily.Bioferrite).Deficit.Value));

        // Unread containment, research, gear or monolith facts leave their Needs unknown, never met.
        Assert.All(new AnomalyFamily().Derive(new AnomalyFacts()), n => Assert.Equal(Satisfaction.Unknown, n.Satisfaction));
        Assert.Equal(Satisfaction.Unknown, Anomaly(unmet with { Research = default }, AnomalyFamily.Bioferrite).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Anomaly(unmet with { Research = default }, AnomalyFamily.Doors).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Anomaly(unmet with { Gear = default }, AnomalyFamily.Lance).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Anomaly(unmet with { Monolith = default }, AnomalyFamily.Monolith).Satisfaction);
    }

    [Fact]
    public void EachDoorAndEachEntityIsItsOwnMethodAndCaptureTendAndMonolithAreHeld()
    {
        var facts = AnomalyFacts.From(Containment([Holder("e2", 3, holdOpen: true), Holder("e1", 1, holdOpen: true)], Held("e2"), Held("e1")));
        Need Ready(NeedId id) => new() { Id = id, Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Unmet };
        var methods = new AnomalyFamily().Propose(
            [Ready(AnomalyFamily.Doors), Ready(AnomalyFamily.Bioferrite), Ready(AnomalyFamily.Capture), Ready(AnomalyFamily.Tend), Ready(AnomalyFamily.Monolith), Ready(AnomalyFamily.Lance)],
            facts);
        Assert.Equal(
            ["entity-door-1-1", "entity-door-3-1", "entity-bioferrite-e1", "entity-bioferrite-e2"],
            methods.Where(m => m.Hold is null).Select(m => m.Method.Value));
        Assert.Equal(
            [AnomalyFamily.CaptureHeld, AnomalyFamily.TendHeld, AnomalyFamily.MonolithHeld],
            methods.Where(m => m.Hold is not null).Select(m => m.Hold!.Subject));
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<AnomalyFacts> Runner { get; init; }
        public AnomalyFacts Facts { get; set; } = new();

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run, NeedId need) => Assert.Single(run.Reports, r => r.Need == need).Verdict;
    }

    private static Colony Open()
    {
        IntentActionRules.RegisterIntentKinds();
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-anomaly-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with { Colonists = 3L, IndoorCapacity = 3L, BedCapacity = 3L, FieldCoverage = Known(1.0) };
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        Colony? colony = null;
        var runner = new FamilyRunner<AnomalyFacts>(new AnomalyFamily(), (_, _) => Task.FromResult(colony!.Facts), AnomalyRounds.Schedule);
        colony = new Colony { Journal = journal, Runner = runner };
        return colony;
    }

    [Fact]
    public void TwoDoorsCloseSideBySideUnderTheDoorsNeed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = AnomalyFacts.From(Containment([Holder("e1", 1, holdOpen: true), Holder("e2", 3, holdOpen: true)], Held("e1", flag: true), Held("e2", flag: true)));
        var run = colony.Run();

        Assert.Equal(Outcome.Admitted, Colony.Report(run, AnomalyFamily.Doors).Outcome);
        Assert.Equal(["entity-door-1-1", "entity-door-3-1"], run.Methods.Select(m => m.Method.Value));
        var binding = colony.Journal.PlanMethod(run.Methods[0].Plan, ct)!;
        Assert.Equal(AnomalyFamily.Doors.FilingKey, binding.Concern);
        Assert.StartsWith("Containment doors: ", OperationIntent.Of(binding));

        // The same doors continue their open Methods.
        var again = colony.Run();
        Assert.All(again.Methods, m => Assert.Equal(ClaimOutcome.Continued, m.Outcome));
        Assert.Equal(2, colony.Journal.LoadNeed(AnomalyFamily.Doors, World, ct).Methods.Count);
    }

    [Fact]
    public void AnExtractFlagOnAPawnAnotherNeedsSettingsHoldIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var plan = PlanIds.MintPlanId();
        var held = colony.Journal.CommitClaimedMethod(HomeUpkeepFamily.Repairs, World, new MethodId("other"), "",
            new PlanSpec(plan, new PlanRevision(1), [DomainAction.NewPawnSettingsAction(new(plan.Value + "-0"), PawnSettings.NewExtractBioferriteSetting(new("e1"), false))]), ct);
        Assert.Equal(ClaimOutcome.Admitted, held.Outcome);

        colony.Facts = AnomalyFacts.From(Containment([Holder("e1", 1, holdOpen: false)], Held("e1")));
        var refused = Colony.Report(colony.Run(), AnomalyFamily.Bioferrite);
        Assert.True(refused.Is(Cause.Claim), refused.ToString());
        Assert.Equal("settings/e1", refused.Refusal.Subject);
        Assert.Empty(colony.Journal.LoadNeed(AnomalyFamily.Bioferrite, World, ct).Methods);
    }

    [Fact]
    public void EachFamilyIsOneDueQueueEntryWakingOnItsMethodKinds()
    {
        var anomaly = ClockPlannerCatalog.FamilyEntry(new FamilyRunner<AnomalyFacts>(new AnomalyFamily(), (_, _) => Task.FromResult(new AnomalyFacts()), AnomalyRounds.Schedule));
        Assert.Equal(AnomalyFamily.FamilyId, anomaly.Name);
        Assert.Contains(ActionKind.DoorControl, anomaly.Kinds);
        Assert.Contains(ActionKind.PawnSettings, anomaly.Kinds);

        var joiners = ClockPlannerCatalog.FamilyEntry(new FamilyRunner<JoinersFacts>(new JoinersFamily(), (_, _) => Task.FromResult(new JoinersFacts()), JoinersRounds.Schedule));
        Assert.Equal(JoinersFamily.FamilyId, joiners.Name);
        Assert.Contains(ActionKind.QuestAccept, joiners.Kinds);
        Assert.Contains(ActionKind.DialogAnswer, joiners.Kinds);
        Assert.DoesNotContain(ClockPlannerCatalog.Entries, e => e.Name is "populationJoiner" or "populationCustody");
        Assert.False(joiners.Immediate);
    }
}
