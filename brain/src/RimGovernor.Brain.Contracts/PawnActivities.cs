namespace RimGovernor.Contracts.Wire.Operations;
public sealed record PawnActivitiesIntent
{
    public required RimGovernor.Brain.Domain.PawnActivities Batch { get; init; }
}
public sealed record PawnActivityExecution(string Assignment, string Method, long Revision, int Step,
    string State, string Reason, long Tick);