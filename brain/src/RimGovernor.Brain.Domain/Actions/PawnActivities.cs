namespace RimGovernor.Brain.Domain;

public enum PawnActivityKind { Work, Eat, Sleep, Hold, Recreation }

/// <summary>An authorized step, not a native-priority request. Deadlines are absolute game ticks.</summary>
public sealed record PawnActivityStep(PawnActivityKind Kind, string Target, string WorkGiver,
    string Job, int Count, long Deadline, string WaitForFilth = "");

public sealed record PawnActivityOrder(string Pawn, string Assignment, string Method, long ExpectedRevision,
    long Revision, bool Release, PawnActivityStep[] Steps);

/// <summary>One atomic execution-table edit under the existing world/authority.</summary>
public sealed record PawnActivities(ulong AuthorityGeneration, PawnActivityOrder[] Orders) : IActionPayload
{
    public void Validate()
    {
        DomainException.Require(AuthorityGeneration > 0 && Orders is { Length: > 0 and <= 64 }, "activity batch requires authority and bounded orders");
        var pawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in Orders)
        {
            DomainException.Require(order != null && Identity.IsValidText(order.Pawn) && pawns.Add(order.Pawn)
                && Identity.IsValidText(order.Assignment) && Identity.IsValidText(order.Method)
                && order.ExpectedRevision >= 0 && order.Revision > order.ExpectedRevision
                && order.Steps != null && (order.Release ? order.Steps.Length == 0 : order.Steps.Length is > 0 and <= 64), "invalid activity assignment");
            foreach (var step in order.Steps)
                DomainException.Require(step != null && Enum.IsDefined(step.Kind) && step.Deadline > 0
                    && (step.Kind == PawnActivityKind.Hold || Identity.IsValidText(step.Target))
                    && (step.Kind != PawnActivityKind.Work || Identity.IsValidText(step.WorkGiver) && Identity.IsValidText(step.Job))
                    && (step.Kind != PawnActivityKind.Eat || step.Count > 0), "invalid activity step");
        }
    }
}

public sealed partial record DomainAction
{
    public static DomainAction NewPawnActivitiesAction(ActionId id, PawnActivities value)
    {
        value.Validate();
        return Create(id, ActionKind.PawnActivities, value);
    }
    public PawnActivities? PawnActivities => PayloadOf<PawnActivities>(ActionKind.PawnActivities);
}