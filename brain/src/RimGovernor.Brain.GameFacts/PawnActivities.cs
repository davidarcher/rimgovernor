using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Operations;
public sealed record PawnActivityExecution(string Assignment, string Method, long Revision, int Step,
    string State, string Reason, long Tick);