namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>Names the throne in the throne room's plan (ThroneWants).</summary>
    internal const string ThroneSlot = "throne";
}

/// <summary>One piece a title's room requirement asks for besides the throne, planned in the slot (ThroneNeed.RequiredPieces names them).</summary>
public sealed record RequiredPiece(string Slot, InteriorPieceDef Piece);
