using RimGovernor.Brain.GameFacts.Operations;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Operations;
public sealed record PawnActivitiesIntent
{
    public required RimGovernor.Brain.Domain.PawnActivities Batch { get; init; }
}
