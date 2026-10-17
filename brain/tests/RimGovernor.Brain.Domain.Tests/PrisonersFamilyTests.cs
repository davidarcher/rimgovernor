using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The prisoners family (#3262): prisoners/recruit and prisoners/custody are Needs with pure met, unmet and unknown
/// predicates; each prisoner owed an interaction is its own Need-owned Method, claimed by its prisoner, so two
/// prisoners' changes run side by side and a second change to one prisoner waits on the first.
/// </summary>
public sealed class PrisonersFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly PrisonerPolicy Policy = new() { ReleaseAfterDays = 10, FoodTargetDays = 5 };

    /// <summary>A living, recruitable prisoner worth recruiting to a one-colonist colony, currently on <paramref name="current"/>.</summary>
    private static PrisonerFacts Prisoner(string pawn, PrisonerInteractionMode current = PrisonerInteractionMode.Maintain, string ideo = "") => new()
    {
        Pawn = new(pawn),
        Dead = Known(false),
        Prisoner = Known(true),
        Recruitable = Known(true),
        CurrentInteraction = Known(current),
        Resistance = Known(0.0),
        HeldTicks = Known(0L),
        Ideo = ideo,
        Prospect = Known(new PrisonerProspect
        {
            Age = 30,
            Health = 1,
            Skills = [new PrisonerSkill { Name = "Shooting", Level = 12, Passion = "Major" }],
        }),
    };

    private static readonly PrisonerColony Colony1 = new() { Colonists = 1 };

    /// <summary>A colony whose ideoligion differs from a prisoner's: a recruit there is converted first.</summary>
    private static readonly PrisonerColony Faithful = new() { Colonists = 1, IdeologyActive = true, Ideo = "ours" };

    private static PrisonersFacts Census(PrisonerColony colony, params PrisonerFacts[] rows) => new()
    {
        Prisoners = Known<EquatableArray<PrisonerFacts>>([.. rows]),
        Colony = Known(colony),
        FoodDays = Known(20.0),
        Custody = Known<EquatableArray<CustodyFacts>>([]),
    };

    private static CustodyFacts Downed(string pawn, bool guest) => new()
    {
        Pawn = new(pawn),
        Dead = Known(false),
        Downed = Known(true),
        Guest = Known(guest),
        Admitted = Known(false),
        Prisoner = Known(false),
        Hostile = Known(!guest),
        Luciferium = Known(false),
        WearingApparel = Known(false),
    };

    private static Need Derive(PrisonersFacts facts, NeedId id) => Assert.Single(new PrisonersFamily(Policy).Derive(facts), n => n.Id == id);

    [Fact]
    public void RecruitIsMetUnmetOrUnknownByThePrisonersOwedAnInteraction()
    {
        var met = Derive(Census(Colony1, Prisoner("a", PrisonerInteractionMode.Recruit)), PrisonersFamily.Recruit);
        Assert.Equal((Satisfaction.Met, 0.0, NeedTier.Development), (met.Satisfaction, met.Deficit.Value, met.Tier));
        Assert.Equal(Satisfaction.Met, Derive(Census(Colony1), PrisonersFamily.Recruit).Satisfaction);

        var unmet = Derive(Census(Colony1, Prisoner("a"), Prisoner("b"), Prisoner("c", PrisonerInteractionMode.Recruit)), PrisonersFamily.Recruit);
        Assert.Equal((Satisfaction.Unmet, 2.0), (unmet.Satisfaction, unmet.Deficit.Value));
        Assert.Contains(unmet.Evidence, e => e.Fact == "prisoner_interactions_owed" && e.Value.Value == "2");

        // An unknown census, or one prisoner whose decision facts are missing, never reads as settled.
        Assert.Equal(Satisfaction.Unknown, Derive(new PrisonersFacts(), PrisonersFamily.Recruit).Satisfaction);
        var partial = Derive(Census(Colony1, Prisoner("a"), Prisoner("b") with { Dead = default }), PrisonersFamily.Recruit);
        Assert.Equal(Satisfaction.Unknown, partial.Satisfaction);
        Assert.False(partial.Deficit.IsKnown);
    }

    [Fact]
    public void CustodyIsMetUnmetOrUnknownByTheDownedHumanlikesOwedCustody()
    {
        var met = Derive(Census(Colony1), PrisonersFamily.Custody);
        Assert.Equal((Satisfaction.Met, NeedTier.Wellbeing), (met.Satisfaction, met.Tier));

        var unmet = Derive(Census(Colony1) with { Custody = Known<EquatableArray<CustodyFacts>>([Downed("g", guest: true), Downed("h", guest: false)]) }, PrisonersFamily.Custody);
        Assert.Equal((Satisfaction.Unmet, 2.0), (unmet.Satisfaction, unmet.Deficit.Value));

        Assert.Equal(Satisfaction.Unknown, Derive(Census(Colony1) with { Custody = default }, PrisonersFamily.Custody).Satisfaction);
    }

    [Fact]
    public void ARescueOrCaptureIsHeldUnderItsOwnSubject()
    {
        var family = new PrisonersFamily(Policy);
        var ready = new[] { new Need { Id = PrisonersFamily.Custody, Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Unmet } };
        var rescue = Assert.Single(family.Propose(ready, Census(Colony1) with { Custody = Known<EquatableArray<CustodyFacts>>([Downed("g", guest: true)]) }));
        Assert.Equal(PrisonersFamily.RescueHeld, rescue.Hold!.Subject);
        var capture = Assert.Single(family.Propose(ready, Census(Colony1) with { Custody = Known<EquatableArray<CustodyFacts>>([Downed("h", guest: false)]) }));
        Assert.Equal(PrisonersFamily.CaptureHeld, capture.Hold!.Subject);
    }

    [Fact]
    public void EachPrisonerOwedAChangeIsItsOwnMethodNamedForItsModeAndPawn()
    {
        var ready = new[] { new Need { Id = PrisonersFamily.Recruit, Tier = NeedTier.Development, Satisfaction = Satisfaction.Unmet } };
        var methods = new PrisonersFamily(Policy).Propose(ready, Census(Faithful, Prisoner("b", ideo: "theirs"), Prisoner("a", ideo: "ours"), Prisoner("c", PrisonerInteractionMode.Recruit, "ours")));
        Assert.Equal(["prisoner-recruit-a", "prisoner-convert-b"], methods.Select(m => m.Method.Value));
        Assert.All(methods, m => Assert.Equal(PrisonersFamily.Recruit, m.Need));
    }

    [Fact]
    public void ThePrisonerInteractionClaimsItsPrisoner()
    {
        var plan = new PlanId("p");
        var action = DomainAction.NewPrisonerInteractionAction(new("p-0"), new PrisonerInteraction(new("a"), PrisonerInteractionMode.Recruit));
        var claims = MethodClaimsOf(new WorkAdmissionEvidence(), [new WorkAction(plan, action, new ProgressView { Action = action.Id, Stage = Stage.Pending })]);
        Assert.Contains("prisoner/a", claims.Targets);
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<PrisonersFacts> Runner { get; init; }
        public required RoundsRequest Review { get; init; }
        public PrisonersFacts Facts { get; set; } = new();

        public void File(FamilyRun run) =>
            Journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [PlannerWave.NeedProgressOf(PrisonersFamily.FamilyId, run)!], TestContext.Current.CancellationToken);

        public ConcernProgress? Row(NeedId need) => Journal.LoadRounds(TestContext.Current.CancellationToken).Progress.SingleOrDefault(p => p.Concern == need.FilingKey);

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run, NeedId need) => Assert.Single(run.Reports, r => r.Need == need).Verdict;
    }

    private static Colony Open()
    {
        BridgeFunctions.RegisterIntentKinds();
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-prisoners-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with { Colonists = Known(3L), IndoorCapacity = Known(3L), BedCapacity = Known(3L), FieldCoverage = Known(1.0) };
        var review = new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts };
        journal.ReviewRounds(review, ct);
        Colony? colony = null;
        var runner = new FamilyRunner<PrisonersFacts>(new PrisonersFamily(Policy), (_, _) => Task.FromResult(colony!.Facts), PrisonersRounds.Schedule);
        colony = new Colony { Journal = journal, Runner = runner, Review = review };
        return colony;
    }

    [Fact]
    public void TwoPrisonersInteractionsAreAdmittedSideBySideUnderTheRecruitNeed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = Census(Colony1, Prisoner("a"), Prisoner("b"));
        var run = colony.Run();

        Assert.Equal(Outcome.Admitted, Colony.Report(run, PrisonersFamily.Recruit).Outcome);
        Assert.Equal(BuildingRuntimePackage.BuildingReasonNoDeficit, Colony.Report(run, PrisonersFamily.Custody));
        Assert.Equal(["prisoner-recruit-a", "prisoner-recruit-b"], run.Methods.Select(m => m.Method.Value));
        var binding = colony.Journal.PlanMethod(run.Methods[0].Plan, ct)!;
        Assert.Equal(PrisonersFamily.Recruit.FilingKey, binding.Concern);
        Assert.StartsWith("Prisoners: ", OperationIntent.Of(binding));

        // The same changes continue their open Methods.
        var again = colony.Run();
        Assert.Equal(BuildingRuntimePackage.BuildingReasonExistingWork, Colony.Report(again, PrisonersFamily.Recruit));
        Assert.All(again.Methods, m => Assert.Equal(ClaimOutcome.Continued, m.Outcome));
        Assert.Equal(2, colony.Journal.LoadNeed(PrisonersFamily.Recruit, World, ct).Methods.Count);
    }

    [Fact]
    public void ASecondChangeToOnePrisonerIsHeldByTheOpenOnesClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = Census(Faithful, Prisoner("a", ideo: "theirs"));
        Assert.Equal("prisoner-convert-a", Assert.Single(colony.Run().Methods).Method.Value);

        // The prisoner now shares the colony's faith: recruiting it overlaps the open conversion on prisoner/a.
        colony.Facts = Census(Faithful, Prisoner("a", ideo: "ours"));
        var held = Colony.Report(colony.Run(), PrisonersFamily.Recruit);
        Assert.True(held.Is(Cause.Claim));
        Assert.Equal("prisoner/a", held.Refusal.Subject);
        Assert.Single(colony.Journal.LoadNeed(PrisonersFamily.Recruit, World, ct).Methods);
    }

    [Fact]
    public void UnknownFactsProposeNothingAndHeldCustodyFilesItsSubject()
    {
        using var colony = Open();
        colony.Facts = new PrisonersFacts { Custody = Known<EquatableArray<CustodyFacts>>([Downed("g", guest: true)]) };
        var run = colony.Run();
        Assert.True(Colony.Report(run, PrisonersFamily.Recruit).Is(Cause.FieldUnavailable));
        Assert.Empty(run.Methods);
        colony.File(run);

        var custody = colony.Row(PrisonersFamily.Custody)!;
        Assert.Equal((Cause.AwaitingPlan, PrisonersFamily.RescueHeld), (custody.Planner, custody.PlannerSubject));
    }

    [Fact]
    public void TheFamilyIsOneDueQueueEntryWakingOnItsReadsAndMethodKinds()
    {
        var entry = ClockPlannerCatalog.FamilyEntry(new FamilyRunner<PrisonersFacts>(new PrisonersFamily(Policy), (_, _) => Task.FromResult(new PrisonersFacts()), PrisonersRounds.Schedule));
        Assert.Equal(PrisonersFamily.FamilyId, entry.Name);
        Assert.Contains(ActionKind.PrisonerInteraction, entry.Kinds);
        Assert.Contains(ActionKind.BedUse, entry.Kinds);
        Assert.Contains(RimGovernor.Brain.Facts.Section.Rooms, entry.Sections);
        Assert.DoesNotContain(ClockPlannerCatalog.Entries, e => e.Name == "prisonerInteraction");
        Assert.False(entry.Immediate);
    }

    /// <summary>The Needs a thought owner may name: the migrated families' Needs whose facility removes a thought.</summary>
    private static readonly NeedId[] OwnerNeeds =
    [
        HomeUpkeepFamily.Repairs, HomeUpkeepFamily.Clearance, FoodStorageNeed, CookingNeed, PrisonersFamily.Recruit, PrisonersFamily.Custody, MilitaryFamily.Equipment, ShelterFamily.Sleeping, ShelterFamily.Temperature,
        RoomsFamily.Comfort, RoomsFamily.Lighting, MedicalCareNeed,
    ];

    /// <summary>The owner column names NeedIds only (#3304): a mood defers to the owner Need while it is at work.</summary>
    [Fact]
    public void EveryThoughtTriggerOwnerIsAKnownNeed()
    {
        var owners = ThoughtAuditOwners();
        Assert.NotEmpty(owners);
        var needs = OwnerNeeds.Select(n => n.Value).ToHashSet(StringComparer.Ordinal);
        Assert.All(owners, owner => Assert.True(new NeedId(owner).IsValid && needs.Contains(owner),
            $"thought_triggers.tsv names owner \"{owner}\", which is not a known NeedId"));
    }
}
