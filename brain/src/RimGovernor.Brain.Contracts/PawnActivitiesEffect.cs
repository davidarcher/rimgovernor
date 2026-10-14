namespace RimGovernor.Contracts.Wire.Receipts;
/// <summary>Only installation acknowledgement; never evidence of started/completed pawn work.</summary>
public sealed record PawnActivitiesEffect
{
    public Operations.PawnActivityExecution[] Assignments { get; init; } = [];
}