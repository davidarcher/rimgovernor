using Microsoft.Data.Sqlite;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using ConditionDuration = RimGovernor.Contracts.Wire.Observations.ConditionDuration;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The disaster family (#3304) replaced the disaster recovery incident Concern: disaster/sheltering (SeriousHarm) is
/// unmet while an allowed-area move is owed and runs in the immediate pass; disaster/services waits on it. An
/// area-only move claims only the pawn's allowed area, so it is admitted beside open service work; the review's
/// recovery readback is keyed by the services Need, and an older save's incident-keyed record is dropped on load.
/// </summary>
public sealed class DisasterFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly DisasterService[] AllServices =
    [
        DisasterService.Food, DisasterService.Production, DisasterService.Sleeping, DisasterService.Shelter,
        DisasterService.Temperature, DisasterService.Cooking, DisasterService.Power, DisasterService.Storage,
        DisasterService.Infrastructure,
    ];

    private static AllowedAreaChange Move(string pawn, string area, bool animal = false) => new() { Pawn = new(pawn), Area = area, Animal = animal };

    // Each Need's own predicate: the family raises on the first Need whose fact is unread.
    private static Need Derive(DisasterFacts facts, NeedId id) =>
        id == DisasterFamily.Sheltering ? DisasterFamily.ShelteringNeed(facts.Changes) : DisasterFamily.ServicesNeed(facts.Disaster);

    private static DisasterHistory Disrupted() => new()
    {
        Started = new(1),
        Observed = new(1),
        Phase = DisasterPhase.Disrupted,
        Conditions = [new DisasterCondition { Id = "condition-1", Definition = "Flare", Duration = new ConditionDuration.Timed { Ticks = 2500 } }],
        Services = [.. AllServices.Select(s => new DisasterEvidence(s, s == DisasterService.Infrastructure ? Finding.Unmet : Finding.Met))],
        Affected = [DisasterService.Infrastructure],
    };

    [Fact]
    public void ShelteringIsUnmetWhileAMoveIsOwedAndAnUnknownTriggerRaisesOnTheRead()
    {
        Assert.Equal("sheltering triggers", Assert.Throws<RequiredFactException>(() => DisasterFacts.From(new RoundsFacts(), null)).Fact);

        var owed = Derive(new DisasterFacts { Changes = [Move("a", "Safe"), Move("b", "Safe")] }, DisasterFamily.Sheltering);
        Assert.Equal((Satisfaction.Unmet, 2.0), (owed.Satisfaction, owed.Deficit));

        var none = Derive(new DisasterFacts { Changes = [] }, DisasterFamily.Sheltering);
        Assert.Equal((Satisfaction.Met, 0.0), (none.Satisfaction, none.Deficit));
    }

    [Fact]
    public void ServicesFollowTheDisasterHistoryAndWaitOnSheltering()
    {
        Assert.Equal([Satisfaction.Met, Satisfaction.Met], new DisasterFamily().Derive(new DisasterFacts()).Select(n => n.Satisfaction));
        Assert.Equal(Satisfaction.Met, Derive(new DisasterFacts(), DisasterFamily.Services).Satisfaction);
        var unmet = Derive(new DisasterFacts { Disaster = Disrupted() }, DisasterFamily.Services);
        Assert.Equal((Satisfaction.Unmet, NeedTier.Wellbeing), (unmet.Satisfaction, unmet.Tier));
        Assert.Equal([DisasterFamily.Sheltering], unmet.Preconditions);
        Assert.Equal("disaster service finding", Assert.Throws<RequiredFactException>(() => Derive(new DisasterFacts { Disaster = Disrupted() with { Phase = DisasterPhase.Unread } }, DisasterFamily.Services)).Fact);
        Assert.Empty(new DisasterFamily().Propose([unmet], new DisasterFacts()));
    }

    [Fact]
    public void AMoveMethodNamesItsPawnAndArea()
    {
        Assert.Equal("area-a-Safe", DisasterFamily.AreaMethod(Move("a", "Safe")).Value);
        Assert.Equal("area-a-unrestricted", DisasterFamily.AreaMethod(Move("a", "")).Value);
        Assert.NotEqual(DisasterFamily.AreaMethod(Move("a", "Safe")), DisasterFamily.AreaMethod(Move("b", "Safe")));
    }

    [Fact]
    public void AnAreaOnlyAssignmentClaimsOnlyThePawnsAllowedArea()
    {
        static MethodClaims Claims(DomainAction action) => MethodClaimsRules.MethodClaimsOf(new WorkAdmissionEvidence(),
            [new WorkAction(new PlanId("plan"), action, new ProgressView { Action = action.Id, Stage = Stage.Pending })]);

        var area = Claims(DomainAction.NewWorkAssignmentAction(new("move"), WorkAssignment.NewAreaAssignment(new("a"), false, "Safe")));
        Assert.Equal(["allowed-area/a"], area.Targets);
        Assert.Empty(area.Pawns);
        var animal = Claims(DomainAction.NewHusbandryAction(new("pen"), new Husbandry(new("dog"), HusbandryMethod.AllowedArea, "Safe")));
        Assert.Equal(["allowed-area/dog"], animal.Targets);
        var service = Claims(DomainAction.NewRecoveryServiceAction(new("fix"), new(new("a"), "battery", RecoveryMethod.Repair)));
        Assert.Contains("battery", service.Targets);
    }

    [Fact]
    public void AShelteringMoveIsAdmittedBesideOpenServiceWorkAndASecondMoveOfThatAreaIsHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-disaster-claims-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);

        var service = journal.CommitClaimedMethod(DisasterFamily.Services, World, new("recovery-a"), "",
            Plan(id => DomainAction.NewRecoveryServiceAction(id, new(new("a"), "battery", RecoveryMethod.Repair))), ct);
        Assert.Equal(ClaimOutcome.Admitted, service.Outcome);

        var move = journal.CommitClaimedMethod(DisasterFamily.Sheltering, World, DisasterFamily.AreaMethod(Move("a", "Safe")), "",
            Plan(id => DomainAction.NewWorkAssignmentAction(id, WorkAssignment.NewAreaAssignment(new("a"), false, "Safe"))), ct);
        Assert.Equal(ClaimOutcome.Admitted, move.Outcome);
        Assert.Equal(ClaimOutcome.Continued, journal.CommitClaimedMethod(DisasterFamily.Sheltering, World, DisasterFamily.AreaMethod(Move("a", "Safe")), "",
            Plan(id => DomainAction.NewWorkAssignmentAction(id, WorkAssignment.NewAreaAssignment(new("a"), false, "Safe"))), ct).Outcome);

        var held = journal.CommitClaimedMethod(DisasterFamily.Sheltering, World, DisasterFamily.AreaMethod(Move("a", "")), "",
            Plan(id => DomainAction.NewWorkAssignmentAction(id, WorkAssignment.NewAreaAssignment(new("a"), true, ""))), ct);
        Assert.Equal(ClaimOutcome.Conflict, held.Outcome);
        Assert.Equal("allowed-area/a", held.Conflict!.Claim);

    }

    [Fact]
    public void TheRecoveryReadbackIsKeyedByTheServicesNeedAndAnOlderJournalsIncidentKeyedReviewIsResetOnLoad()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "rg-disaster-recovery-" + Guid.NewGuid().ToString("N") + ".db");
        var facts = WorkResourceTestFacts.Create("a") with
        {
            DisasterConditions = EquatableArray.From([new DisasterCondition { Id = "condition-1", Definition = "Flare", Duration = new ConditionDuration.Timed { Ticks = 2500 } }]),
        };
        RoundsResult Review(Journal journal, long tick) => journal.ReviewRounds(new RoundsRequest
        {
            Current = World, Tick = new(tick), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts, Revision = journal.LoadRounds(ct).Revision,
        }, ct);
        using (var journal = Journal.Open(path, ct))
        {
            var review = Review(journal, 100).Review;
            Assert.NotNull(review.Disaster);
            var recovery = Assert.IsType<RoundsRecovery>(review.Recovery);
            Assert.Equal(DisasterFamily.Services, recovery.Need);
            Assert.Equal(new RimGovernor.Brain.Store.World(World.Colony, World.Load, World.Map), recovery.World);
            Assert.Equal(recovery, journal.LoadRounds(ct).Recovery);
        }
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var write = connection.CreateCommand();
            // An older journal's review keyed the recovery selection by an incident.
            write.CommandText = "UPDATE rounds SET payload=json_set(json_remove(payload,'$.Recovery.Need','$.Recovery.World'),'$.Recovery.Incident','incident-1')";
            Assert.Equal(1, write.ExecuteNonQuery());
        }
        SqliteConnection.ClearAllPools();
        using (var reopened = Journal.Open(path, ct))
        {
            Assert.ThrowsAny<Exception>(() => reopened.LoadRounds(ct));
            // The load sequence (GovernorState.EnsureAsync) resets the review before anything reads it.
            var saved = reopened.GovernorStateBlobs(ct);
            reopened.RebuildOwners(null, ct);
            reopened.RebuildFamilies(saved, ct);
            reopened.ResetRounds(ct);
            Assert.Null(reopened.LoadRounds(ct).Recovery);

            // The next full review selects under the Need.
            var review = Review(reopened, 110).Review;
            Assert.Equal(DisasterFamily.Services, review.Recovery?.Need);
        }
    }

    [Fact]
    public async Task TheNeedsReachRoundsProgressAndOnlyShelteringRunsInTheImmediatePass()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-disaster-progress-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a") }, ct);
        var facts = new DisasterFacts { Changes = [Move("a", "Safe")], Disaster = Disrupted() };
        var runner = new FamilyRunner<DisasterFacts>(new DisasterFamily(), (_, _) => Task.FromResult(facts), DisasterRounds.Schedule);

        var immediate = await runner.RunAsync(new FamilyStep { Journal = journal, World = World, Immediate = true }, ct);
        Assert.Equal([DisasterFamily.Sheltering], immediate.Standings.Select(s => s.Need.Id));

        var run = await runner.RunAsync(new FamilyStep { Journal = journal, World = World }, ct);
        var services = Assert.Single(run.Reports, r => r.Need == DisasterFamily.Services).Verdict;
        Assert.True(services.Is(Cause.WaitingOnNeed));
        journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [PlannerWave.NeedProgressOf(DisasterFamily.FamilyId, run)!], ct);
        var progress = journal.LoadRounds(ct).Progress;
        Assert.Contains(progress, p => p.Concern == DisasterFamily.Sheltering.FilingKey);
        Assert.Contains(progress, p => p.Concern == DisasterFamily.Services.FilingKey);

        var entry = ClockPlannerCatalog.FamilyEntry(runner);
        Assert.Equal("disaster", entry.Name);
        Assert.True(entry.Immediate);
        Assert.DoesNotContain(ClockPlannerCatalog.Entries, e => e.Name == "recovery");
    }

    private static PlanSpec Plan(Func<ActionId, DomainAction> action)
    {
        var id = PlanIds.MintPlanId();
        return new PlanSpec(id, new PlanRevision(1), [action(new ActionId(id.Value + "-0"))]);
    }
}
