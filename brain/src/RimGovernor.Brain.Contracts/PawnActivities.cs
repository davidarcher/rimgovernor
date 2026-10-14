namespace RimGovernor.Contracts.Wire.Operations;
public sealed record PawnActivitiesIntent
{
    public RimGovernor.Brain.Domain.PawnActivities? Batch { get; init; }
}
public sealed record PawnActivityExecution(string Assignment, string Method, long Revision, int Step,
    string State, string Reason, int? JobId, long Tick);