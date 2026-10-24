using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>movement/arrival</c>: a move intent through operations_apply orders a real native Goto for an owned drafted pawn,
/// the pawn arrives, and the same order resent under a new key applies again as a no-op. Only a live game judges the
/// draft, reachability and the job it issues.
/// </summary>
internal static class MovementArrival
{
    private static readonly Fx.StageCell From = new(1, 4);
    private static readonly Fx.StageCell To = new(8, 4);

    internal static TypedCase Case() => new()
    {
        Name = "movement/arrival",
        Scope = "A MoveIntent for an undrafted colonist is refused; drafted, it applies as a native Goto job, the pawn walks "
            + "onto the staged destination, and the same order resent under a new key applies again.",
        Ticks = 2000,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 10,
                Height = 9,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 6,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 10, 9), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Pawns] }],
            Pawns = [new Fx.StagePawn { Name = "walker", Colonist = 0, Cell = From }],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var pawnId = s.Staged!.Pawns["walker"].Id;
        var site = s.Staged.Site ?? throw new AcceptanceException("stage: no site");
        var destination = new Common.Cell { X = site.X + To.X, Z = site.Z + To.Z };
        var move = new Operations.MoveIntent { PawnId = pawnId, Destination = destination };

        _ = await TypedApply.RefusedAsync(s, "move-undrafted", move, cancellationToken).ConfigureAwait(false);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "draft", new Operations.DraftIntent { PawnId = pawnId, Drafted = true }, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "move", move, cancellationToken).ConfigureAwait(false);

        var moving = await TypedApply.PawnAsync(s, "moving", pawnId, cancellationToken).ConfigureAwait(false);
        s.RecordWire("moving", moving.Job);
        if (moving.Job.DefName != "Goto")
        {
            throw new AcceptanceException($"moving: pawn job is \"{moving.Job.DefName}\", not Goto");
        }

        Common.Cell? at = null;
        _ = await s.RunUntilAsync("arrive", 2000, async ct =>
        {
            at = ((await TypedApply.PawnAsync(s, "position", pawnId, ct).ConfigureAwait(false)).Pawn.Place as RimGovernor.Contracts.Wire.Observations.EntityPlace.OnMap)?.Cell;
            return (Wait.Signature(at?.X, at?.Z), at?.X == destination.X && at?.Z == destination.Z);
        }, cancellationToken).ConfigureAwait(false);
        s.RecordWire("arrived", at);

        _ = await TypedApply.AppliedAsync(s, "move-resend", move, cancellationToken).ConfigureAwait(false);
    }
}
