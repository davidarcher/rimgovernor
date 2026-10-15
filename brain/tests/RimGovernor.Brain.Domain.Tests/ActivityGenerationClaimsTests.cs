using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Bridge.Contracts;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
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
        BridgeFunctions.RegisterIntentKinds();
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-generation-claims-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("pawn") with
        { Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 20)]),
            Colonists = Known(1L), IndoorCapacity = Known(0L), BedCapacity = Known(0L) };
        var review = journal.ReviewRounds(new() { Current = world, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        var owner = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainWorkLedger);
        var activity = DomainAction.NewPawnActivitiesAction(new("delivery"), new(1,
            [new("pawn", "assignment", "method", 0, 1, false,
                [new(PawnActivityKind.Deliver, "rice-stack", "", "", 6, 500) { Destination = new(2, 2) }])
                { OutputDelivery = new("Rice", new(1, 1), new(2, 2), 6, 500, 3) }]));
        var building = DomainAction.NewBuildingAction(new("wall"),
            new("TestWall", new(3, 3), Rotation.North, "Rice"), ConstructionTier.Survive);
        var plan = new PlanSpec(new("plan"), new(1), [activity]);
        journal.CommitMethod(owner.Standard.Id, owner.Revision, new("method"), plan, ct);
        var housing = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainHousing);
        var construction = new PlanSpec(new("construction"), new(1), [building]);
        journal.CommitMethod(housing.Standard.Id, housing.Revision, new("shelter-method"), construction, ct);
        if (accepted)
        {
            var scope = world with { Plan = plan.Id };
            journal.Prepare(plan.Id, activity.Id, scope, new(100), ct);
            var sent = journal.Dispatch(plan.Id, activity.Id, scope, new(100), ct);
            journal.RecordReceipt(plan.Id, activity.Id, sent.View.Attempt, Receipt.Accepted, ct);
        }
        Assert.Contains("pawn", journal.WorkResources(world, ct).Claims.Pawns);
        Assert.Equal(8, journal.WorkResources(world, ct).Materials.Value[new("Rice")].Value);
        var changed = world with { Native = new(2) };
        journal.ReviewRounds(new() { Revision = review.Review.Revision, Current = changed, Tick = new(110), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = facts with { Tick = new(110) } }, ct);
        var remaining = journal.WorkResources(changed, ct);
        Assert.Empty(remaining.Claims.Pawns);
        Assert.DoesNotContain("rice-stack", remaining.Claims.Targets);
        Assert.Contains(BuildingClaimTarget(building.Building!), remaining.Claims.Targets);
        Assert.Equal(14, remaining.Materials.Value[new("Rice")].Value);
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
        BridgeFunctions.RegisterIntentKinds();
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        var path = Path.Combine(Path.GetTempPath(), "rg-generation-claims-" + Guid.NewGuid().ToString("N") + ".db");
        var journal = Journal.Open(path, ct);
        var facts = WorkResourceTestFacts.Create("pawn") with
        { Resources = Known<EquatableArray<Amount>>([new(new("Rice"), 20)]),
            Colonists = Known(1L), IndoorCapacity = Known(0L), BedCapacity = Known(0L) };
        var review = journal.ReviewRounds(new() { Current = world, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        var owner = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainWorkLedger);
        var activity = DomainAction.NewPawnActivitiesAction(new("delivery"), new(1,
            [new("pawn", "assignment", "method", 0, 1, false,
                [new(PawnActivityKind.Deliver, "rice-stack", "", "", 6, 500) { Destination = new(2, 2) }])
                { OutputDelivery = new("Rice", new(1, 1), new(2, 2), 6, 500, 3) }]));
        var building = DomainAction.NewBuildingAction(new("wall"),
            new("TestWall", new(3, 3), Rotation.North, "Rice"), ConstructionTier.Survive);
        journal.CommitMethod(owner.Standard.Id, owner.Revision, new("method"), new PlanSpec(new("plan"), new(1), [activity]), ct);
        var housing = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainHousing);
        journal.CommitMethod(housing.Standard.Id, housing.Revision, new("shelter-method"), new PlanSpec(new("construction"), new(1), [building]), ct);
        return (journal, world, path);
    }

    /// <summary>Clones the live plan holding an action of <paramref name="kind"/> as retired history under the same owner.</summary>
    private static void SeedRetiredClones(string path, int count, string kind)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Foreign Keys=True");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<$count)
            INSERT INTO plans(id,revision,method_id,superseded_by,retired) SELECT 'old-'||i,'1','old-'||i,NULL,1 FROM n;
            INSERT INTO plan_owner(plan_id,kind) SELECT id,'standard' FROM plans WHERE id LIKE 'old-%';
            INSERT INTO standard_methods(standard_id,episode,method_id,plan_id,kind,reason)
                SELECT s.standard_id,s.episode,p.id,p.id,'standard',NULL FROM plans p,
                (SELECT m.standard_id,m.episode FROM actions a JOIN standard_methods m ON m.plan_id=a.plan_id WHERE a.kind=$kind LIMIT 1) s
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
