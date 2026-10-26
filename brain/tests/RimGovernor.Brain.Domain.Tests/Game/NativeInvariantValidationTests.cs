using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Store.Codec;
using RimWorld;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class NativeInvariantValidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlanSpec Backup(string definition, IGameDefinitionSemantics? semantics, bool sameCell = true, bool coupled = false)
    {
        var build = DomainAction.NewBuildingAction(new("backup"), new(definition, new(2, 3), Rotation.North, ""), ConstructionTier.Survive);
        var remove = DomainAction.NewWallRemovalAction(new("remove"), new("", build.Id, sameCell ? new(2, 3) : new(3, 3)));
        return new(new("wall-plan"), new(1), [build, remove], [new(remove.Id, build.Id, coupled)], semantics);
    }

    [Fact]
    public void BackupRequiresARealWallAndAnUncoupledDependencyAtTheSameCell()
    {
        var native = GameDefinitionSemantics.Instance;
        Assert.Throws<DomainException>(() => Backup(nameof(ThingDefOf.Door), native));
        Assert.Throws<DomainException>(() => Backup(nameof(ThingDefOf.Wall), null));
        Assert.Throws<DomainException>(() => Backup(nameof(ThingDefOf.Wall), native, sameCell: false));
        Assert.Throws<DomainException>(() => Backup(nameof(ThingDefOf.Wall), native, coupled: true));
        Backup(nameof(ThingDefOf.Wall), native).Validate();
    }

    [Fact]
    public void JournalRevalidatesCallerProofAndDecodedBuildingIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), "rg-native-invariants-" + Guid.NewGuid().ToString("N") + ".db");
        var native = GameDefinitionSemantics.Instance;
        using (var journal = Journal.Open(path, Ct, native))
        {
            Assert.Throws<DomainException>(() => journal.CreatePlan(Backup(nameof(ThingDefOf.Door), new Permissive()), Ct));
            journal.CreatePlan(Backup(nameof(ThingDefOf.Wall), native), Ct);
        }
        using (var restored = Journal.Open(path, Ct, native))
        {
            var plan = restored.LoadPlan(new("wall-plan"), Ct).Spec;
            var order = IntentActionRules.IntentAction("wall-order", plan.Actions[0]);
            var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(order.Intent).Value;
            Assert.Equal(nameof(ThingDefOf.Wall), intent.Placement!.DefName);
            Assert.Equal(nameof(ThingDefOf.Wall), plan.Actions[0].Building!.Definition);
        }
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE actions SET body=replace(body,'\"Definition\":\"Wall\"','\"Definition\":\"Door\"') WHERE id='backup'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        using var corrupt = Journal.Open(path, Ct, native);
        Assert.Throws<DomainException>(() => corrupt.LoadPlan(new("wall-plan"), Ct));
    }

    [Fact]
    public void TradeBudgetRejectsOtherPackedGoodsAfterCodecAndJournalBoundaries()
    {
        var mission = new TradeMission
        {
            HomeColony = new("colony"), HomeMap = new(1), HomeTile = 1, Settlement = "settlement", SettlementTile = 2,
            Crew = [new("pawn")], Negotiator = new("pawn"), SilverBudget = 100, Demand = [new("medicine", 1)],
            Pack = [new(nameof(ThingDefOf.Silver), 100)], ReturnHome = true,
        };
        var native = GameDefinitionSemantics.Instance;
        mission.Validate(native);
        Assert.True(RimGovernor.Brain.Runtime.TradePlanner.TradeMissionValid(mission));
        var wrong = StoreJson.Deserialize<TradeMission>(StoreJson.Serialize(mission with { Pack = [new(nameof(ThingDefOf.Gold), 100)] }));
        Assert.Throws<DomainException>(() => wrong.Validate(native));
        Assert.False(RimGovernor.Brain.Runtime.TradePlanner.TradeMissionValid(wrong));
        Assert.Throws<DomainException>(() => mission.Validate());
        Assert.Throws<DomainException>(() => (mission with { SilverBudget = 101 }).Validate(native));
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-currency-invariant-" + Guid.NewGuid().ToString("N") + ".db"), Ct, native);
        var record = new RimGovernor.Brain.Store.TradeMissionsRecord { World = new(mission.HomeColony, new("load"), mission.HomeMap) }.With("mission", mission);
        journal.SaveTradeMissions(record, Ct);
        Assert.Equal(100, Assert.Single(journal.LoadTradeMissions(record.World, Ct).Missions).Mission.SilverBudget);
        Assert.Throws<DomainException>(() => journal.SaveTradeMissions(record.With("mission", wrong), Ct));
    }

    private sealed class Permissive : IGameDefinitionSemantics
    {
        public bool IsColonyWall(string definition) => true;
        public bool IsTradeCurrency(string definition) => true;
    }
}
