using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>needs/recreation-shared-seat</c> (#3570): one colonist plays chess, holding the two-seat chess table at the giver job's
/// joyMaxParticipants. The activity-task bind read still binds a second colonist's selected recreation to that table, because
/// the bind reserves with the claim the play job makes rather than a single-pawn claim the holder's reservation refuses.
/// </summary>
internal static class NeedsRecreationSharedSeat
{
    private const string HoldTool = "test/recreation_hold_seat";
    private const string Table = "table";
    private const string TaskId = "recreation-seat";

    private static readonly FixtureOp<Fx.StageReply, Fx.FixtureDone> Hold = new(
        HoldTool,
        r => ServedOps.Args(("holder", r.Pawns["holder"].Id), ("building", r.Buildings[Table].Ids.Single())),
        ServedOps.Done(HoldTool));

    internal static TypedCase Case() => new()
    {
        Name = "needs/recreation-shared-seat",
        Scope = "A paused colony where one colonist's chess game holds the two-seat chess table binds a second colonist's selected "
            + "recreation to that table. A unit test cannot prove the native ReservationManager's maxPawns rule or the giver's legality.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 5,
                Height = 3,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unfogged],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 5, 3), Kinds = [Fx.StageClearKind.Things] }],
            Buildings =
            [
                new Fx.StageBuilding { Name = Table, Def = "ChessTable", Stuff = "WoodLog", Cells = [new(2, 1)] },
                new Fx.StageBuilding { Name = "chairs", Def = "Stool", Stuff = "WoodLog", Cells = [new(1, 1), new(3, 1)] },
            ],
            Pawns =
            [
                new Fx.StagePawn { Name = "holder", Colonist = 0 },
                new Fx.StagePawn { Name = "player", Colonist = 1 },
            ],
        },
        Ops = [HoldTool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged ?? throw new AcceptanceException("the stage made nothing");
        var player = staged.Pawns["player"].Id;
        var table = staged.Buildings[Table].Ids.Single();
        _ = await s.FixtureAsync("hold", Hold, staged, cancellationToken).ConfigureAwait(false);

        // The bind read validates the batch's shape only; it never compares the authority generation.
        var step = new PawnActivityStep(PawnActivityKind.Recreation, table, "", "", 0, int.MaxValue);
        var cell = staged.Buildings[Table].Cells.Single();
        var batch = new PawnActivities(1, [new PawnActivityOrder(player, TaskId, TaskId, 0, 1, false, [step])])
        {
            Tasks = [new SelectedActivityTask(TaskId, player, [], step, new Cell(cell.X, cell.Z), 0)],
        };
        var read = await s.CallAsync("bind", NativeOps.ReadActivityTasks, new Observations.ActivityTaskRequest(s.Identity, batch), cancellationToken).ConfigureAwait(false);
        s.RecordWireRows("bindings", read.Bindings);
        if (!read.Bindings.Any(b => b.Task == TaskId && b.Step.Target == table))
        {
            throw new AcceptanceException($"the held chess table {table} did not bind the second colonist's recreation: [{string.Join(' ', read.Bindings.Select(b => $"{b.Task}:{b.Step.Target}"))}]");
        }
    }
}
