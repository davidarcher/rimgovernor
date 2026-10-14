namespace RimGovernor.Brain.Domain;

public enum PawnActivityKind { Work, Eat, Sleep, Hold, Recreation, Deliver, Cook }

public sealed record PawnActivityIngredient(string Thing, int Count);
public sealed record PawnActivityCooking(string Bill, string Recipe, EquatableArray<PawnActivityIngredient> Ingredients);

/// <summary>An authorized step, not a native-priority request. Deadlines are absolute game ticks.</summary>
public sealed record PawnActivityStep(PawnActivityKind Kind, string Target, string WorkGiver,
    string Job, int Count, long Deadline, string WaitForFilth = "")
{
    public Cell? Destination { get; init; }
    public PawnActivityCooking? Cooking { get; init; }
}

public sealed record ActivityCookingContinuation(string Bench, string Bill, string Recipe, string WorkGiver,
    Cell Origin, string Product, int ProductCount, Cell Destination);
public sealed record ActivityOutputDelivery(string Resource, Cell Origin, Cell Destination, int Count, long Deadline, int AttemptsLeft)
{
    public ActivityCookingContinuation? Cooking { get; init; }
}
public sealed record PawnActivityOrder(string Pawn, string Assignment, string Method, long ExpectedRevision,
    long Revision, bool Release, PawnActivityStep[] Steps)
{
    /// <summary>A bounded obligation, never inventory credit or a predicted output identity.</summary>
    public ActivityOutputDelivery? OutputDelivery { get; init; }
}

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
            {
                DomainException.Require(step != null && Enum.IsDefined(step.Kind) && step.Deadline > 0
                    && (step.Kind == PawnActivityKind.Hold || Identity.IsValidText(step.Target))
                    && (step.Kind != PawnActivityKind.Work || Identity.IsValidText(step.WorkGiver) && Identity.IsValidText(step.Job))
                    && (step.Kind is not (PawnActivityKind.Eat or PawnActivityKind.Deliver) || step.Count > 0)
                    && (step.Kind == PawnActivityKind.Deliver
                        ? step.Destination is { X: >= 0, Z: >= 0 } && step.WorkGiver.Length == 0 && step.Job.Length == 0
                        : step.Destination is null), "invalid activity step");
                if (step.Kind == PawnActivityKind.Cook)
                {
                    var cooking = step.Cooking;
                    DomainException.Require(cooking != null && Identity.IsValidText(cooking.Bill) && Identity.IsValidText(cooking.Recipe)
                        && Identity.IsValidText(step.WorkGiver) && step.Job.Length == 0 && step.Count == 0
                        && cooking.Ingredients.Count is > 0 and <= 16
                        && cooking.Ingredients.All(i => i != null && Identity.IsValidText(i.Thing) && i.Count > 0)
                        && cooking.Ingredients.Select(i => i.Thing).Distinct(StringComparer.Ordinal).Count() == cooking.Ingredients.Count,
                        "invalid exact cooking activity");
                }
                else DomainException.Require(step.Cooking == null, "cooking arguments on another activity family");
            }
            if (order.OutputDelivery is { } output)
            {
                DomainException.Require(Identity.IsValidText(output.Resource) && output.Count > 0 && output.Deadline > 0
                    && output.AttemptsLeft is >= 0 and <= 3 && output.Origin is { X: >= 0, Z: >= 0 }
                    && output.Destination is { X: >= 0, Z: >= 0 }, "invalid output delivery obligation");
                if (output.Cooking is { } cook)
                    DomainException.Require(Identity.IsValidText(cook.Bench) && Identity.IsValidText(cook.Bill)
                        && Identity.IsValidText(cook.Recipe) && Identity.IsValidText(cook.WorkGiver) && Identity.IsValidText(cook.Product)
                        && cook.ProductCount > 0 && cook.Origin is { X: >= 0, Z: >= 0 } && cook.Destination is { X: >= 0, Z: >= 0 }, "invalid cooking continuation");
            }
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
