using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A building's interaction cell is At an offset or None (#3957). The piece derives the cell the worker stands on only
/// for At, and the store persists each state and reads it back.
/// </summary>
public sealed class InteractionCellStatesTests
{
    private static InteriorPiece Piece(InteractionCell interaction) => new()
    {
        Slot = "bench",
        Def = "Bench",
        Size = new Cell(3, 1),
        Rot = Rotation.North,
        Rect = new Rectangle(10, 10, 3, 1),
        InteractionOffset = interaction,
    };

    [Fact]
    public void OnlyAPieceWithAnInteractionOffsetHasAWorkerCell()
    {
        var (cell, at) = Piece(new InteractionCell.At(new Cell(1, -1))).Interaction();
        Assert.True(at);
        Assert.Equal(new Cell(12, 9), cell);

        Assert.False(Piece(new InteractionCell.None()).Interaction().Ok);
        Assert.IsType<InteractionCell.None>(new InteriorPiece().InteractionOffset);
        Assert.IsType<InteractionCell.None>(new InteriorPieceDef().Interaction);
    }

    [Fact]
    public void TheStoreRoundTripsBothInteractionStates()
    {
        foreach (var state in new InteractionCell[] { new InteractionCell.At(new Cell(1, -1)), new InteractionCell.None() })
        {
            var piece = Piece(state);
            Assert.Equal(state, StoreJson.Deserialize<InteriorPiece>(StoreJson.Serialize(piece)).InteractionOffset);
        }
    }
}
