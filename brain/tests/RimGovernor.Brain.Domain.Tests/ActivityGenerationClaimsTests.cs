using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameAccess;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ActivityGenerationClaimsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthorityRolloverEndsActivityClaimsWithoutDroppingPersistentConstruction(bool accepted)
    {
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-generation-claims-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("pawn") with
        {
            Census = ((WorkResourceTestFacts.Create("pawn")).Census with { ColonistCount = 1, IndoorSleepingCapacity = 0, BedCapacity = 0 }) with { Resources = [new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = "Rice", Units = 20 }] }, };
        var review = journal.Review(new() { Current = world, Tick = new(100), Enabled = true,
            Policy = Rules.DefaultPlannerSettings(), Facts = facts }, ct);
        var activity = DomainAction.NewPawnActivitiesAction(new("delivery"), new(1,
            [new("pawn", "assignment", "method", 0, 1, false,
                [new PawnActivityStep(PawnActivityKind.Deliver, "rice-stack", "", "", 6, 500).At(new(2, 2))])
                { OutputDelivery = new("Rice", new(1, 1), new(2, 2), 6, 500, 3) }]));
        var building = DomainAction.NewBuildingAction(new("wall"),
            new("TestWall", new(3, 3), Rotation.North, "Rice"), ConstructionTier.Survive);
        var plan = new PlanSpec(new("plan"), new(1), [activity]);
        TestNeed.Commit(journal, world, new("method"), plan, ct);
        var construction = new PlanSpec(new("construction"), new(1), [building]);
        Assert.Equal(RimGovernor.Brain.Store.ClaimOutcome.Admitted, journal.CommitClaimedMethod(ShelterFamily.Sleeping, world, new("shelter-method"), "", construction, ct).Outcome);
        if (accepted)
        {
            var scope = world with { Plan = plan.Id };
            journal.Prepare(plan.Id, activity.Id, scope, new(100), ct);
            var sent = journal.Dispatch(plan.Id, activity.Id, scope, new(100), ct);
            journal.RecordReceipt(plan.Id, activity.Id, sent.View.Attempt, Receipt.Accepted, ct);
        }
        Assert.Contains("pawn", journal.WorkResources(world, ct).Claims.Pawns);
        Assert.Equal(8, journal.WorkResources(world, ct).Materials.AssessedBalances()[new("Rice")].ExactCount());
        var changed = world with { Native = new(2) };
        journal.Review(new() { Revision = review.Review.Revision, Current = changed, Tick = new(110), Enabled = true,
            Policy = Rules.DefaultPlannerSettings(), Facts = facts with { Tick = new(110) } }, ct);
        var remaining = journal.WorkResources(changed, ct);
        Assert.Empty(remaining.Claims.Pawns);
        Assert.DoesNotContain("rice-stack", remaining.Claims.Targets);
        Assert.Contains(WorkRules.BuildingClaimTarget(building.Building!), remaining.Claims.Targets);
        Assert.Equal(14, remaining.Materials.AssessedBalances()[new("Rice")].ExactCount());
        Assert.False(journal.LoadPlan(plan.Id, ct).Retired); // Historical intent is not an unauthorized release order.
        Assert.False(journal.LoadPlan(construction.Id, ct).Retired);
    }

    [Fact]
    public void RetiredActivityHistoryBeyondTheBoundDoesNotMakeTheCensusUnknown()
    {
        var (journal, world, dbPath) = OpenWithLiveActivityAndBuilding();
        using (journal)
        {
            var ct = TestContext.Current.CancellationToken;
            SeedRetiredClones(dbPath, 1100, "pawn_activities");
            var census = journal.WorkActions(world, ct);
            Assert.False(census.Truncated);
            Assert.Equal(2, census.Actions.Count); // the live activity and the persistent building; history is not claimed
            Assert.Contains("pawn", journal.WorkResources(world, ct).Claims.Pawns);
        }
    }

    [Fact]
    public void RetiredPlansStillCarryingPersistentConstructionKeepTheCensusBounded()
    {
        var (journal, world, dbPath) = OpenWithLiveActivityAndBuilding();
        using (journal)
        {
            SeedRetiredClones(dbPath, 1100, "building");
            Assert.True(journal.WorkActions(world, TestContext.Current.CancellationToken).Truncated);
        }
    }

    private static (Journal Journal, GenerationSnapshot World, string Path) OpenWithLiveActivityAndBuilding()
    {
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        var path = Path.Combine(Path.GetTempPath(), "rg-generation-claims-" + Guid.NewGuid().ToString("N") + ".db");
        var journal = Journal.Open(path, ct);
        var facts = WorkResourceTestFacts.Create("pawn") with
        {
            Census = ((WorkResourceTestFacts.Create("pawn")).Census with { ColonistCount = 1, IndoorSleepingCapacity = 0, BedCapacity = 0 }) with { Resources = [new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = "Rice", Units = 20 }] }, };
        journal.Review(new() { Current = world, Tick = new(100), Enabled = true,
            Policy = Rules.DefaultPlannerSettings(), Facts = facts }, ct);
        var activity = DomainAction.NewPawnActivitiesAction(new("delivery"), new(1,
            [new("pawn", "assignment", "method", 0, 1, false,
                [new PawnActivityStep(PawnActivityKind.Deliver, "rice-stack", "", "", 6, 500).At(new(2, 2))])
                { OutputDelivery = new("Rice", new(1, 1), new(2, 2), 6, 500, 3) }]));
        var building = DomainAction.NewBuildingAction(new("wall"),
            new("TestWall", new(3, 3), Rotation.North, "Rice"), ConstructionTier.Survive);
        TestNeed.Commit(journal, world, new("method"), new PlanSpec(new("plan"), new(1), [activity]), ct);
        Assert.Equal(RimGovernor.Brain.Store.ClaimOutcome.Admitted, journal.CommitClaimedMethod(ShelterFamily.Sleeping, world, new("shelter-method"), "",
            new PlanSpec(new("construction"), new(1), [building]), ct).Outcome);
        return (journal, world, path);
    }

    /// <summary>
    /// Clones the live plan holding an action of <paramref name="kind"/> as retired history under the same Need owner
    /// (the activity is the test Need's, the construction the shelter starter Need's, #3257).
    /// </summary>
    private static void SeedRetiredClones(string path, int count, string kind)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Foreign Keys=True");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<$count)
            INSERT INTO plans(id,revision,method_id,superseded_by,retired) SELECT 'old-'||i,'1','old-'||i,NULL,1 FROM n;
            INSERT INTO plan_owner(plan_id,kind) SELECT id,(SELECT o.kind FROM actions a JOIN plan_owner o ON o.plan_id=a.plan_id
                WHERE a.kind=$kind AND a.plan_id NOT LIKE 'old-%' LIMIT 1) FROM plans WHERE id LIKE 'old-%';
            INSERT INTO need_methods(colony,load_token,map_id,need_id,method_id,attempt,plan_id,kind,reason)
                SELECT s.colony,s.load_token,s.map_id,s.need_id,p.id,1,p.id,'need',NULL FROM plans p,
                (SELECT m.colony,m.load_token,m.map_id,m.need_id FROM actions a JOIN need_methods m ON m.plan_id=a.plan_id WHERE a.kind=$kind LIMIT 1) s
                WHERE p.id LIKE 'old-%';
            INSERT INTO actions(id,plan_id,ordinal,kind,fight,body)
                SELECT 'old-action-'||p.id,p.id,0,$kind,NULL,(SELECT body FROM actions WHERE kind=$kind AND plan_id NOT LIKE 'old-%' LIMIT 1)
                FROM plans p WHERE p.id LIKE 'old-%';
            """;
        _ = command.Parameters.AddWithValue("$count", count);
        _ = command.Parameters.AddWithValue("$kind", kind);
        _ = command.ExecuteNonQuery();
    }
}
