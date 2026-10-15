namespace RimGovernor.Brain.Domain;

public enum PawnActivityKind { Work, Eat, Sleep, Hold, Recreation, Deliver, Recipe, Approach, Supply }

public sealed record PawnActivityPractice(string Skill, string Role, int RequirementIndex, string Partner, string PartnerTarget, int DurationTicks)
{ public string Beneficiary { get; init; } = ""; }

public sealed record PawnActivityIngredient(string Thing, int Count);
public sealed record PawnActivityRecipe(string Bill, string Recipe, EquatableArray<PawnActivityIngredient> Ingredients)
{
    public string Resource { get; init; } = "";
    public string Stuff { get; init; } = "";
    public string Unfinished { get; init; } = "";
}

/// <summary>An authorized step, not a native-priority request. Deadlines are absolute game ticks.</summary>
public sealed record PawnActivityStep(PawnActivityKind Kind, string Target, string WorkGiver,
    string Job, int Count, long Deadline, string WaitForFilth = "")
{
    public Cell? Destination { get; init; }
    public PawnActivityRecipe? Recipe { get; init; }
    public PawnActivityPractice? Practice { get; init; }
    public string Medicine { get; init; } = "";
    public long NotBefore { get; init; }
    public string Container { get; init; } = "";
    public string SupportDemand { get; init; } = "";
}

/// <summary>Selected remaining intent owned by this Method. No forecast or mutable progress flags.</summary>
public sealed record SelectedActivityInput(string Resource, string Lot, int Count, Cell Location, string Provenance)
{
    public bool Observed { get; init; }
}

public sealed record SelectedActivityTask(string Id, string Pawn, EquatableArray<string> Prerequisites,
    PawnActivityStep Step, Cell Location, long DepartTick)
{
    public string Resource { get; init; } = "";
    public Cell? Source { get; init; }
    public string SiteDefinition { get; init; } = "";
    public string SupportDemand { get; init; } = "";
    public string ProductStuff { get; init; } = "";
    /// <summary>Selected service-time bound; fresh native mechanics must still support it before a later recipe.</summary>
    public Fact<double> ProductiveWorkTicks { get; init; }
    public bool CompletionMustBeObserved { get; init; }
    public EquatableArray<SelectedActivityInput> Inputs { get; init; }
    public int AttemptsLeft { get; init; } = 3;
}

public sealed record ActivityRecipeContinuation(string Bench, string Bill, string Recipe, string WorkGiver,
    Cell Origin, string Product, int ProductCount, Cell Destination);
public sealed record ActivityOutputDelivery(string Resource, Cell Origin, Cell Destination, int Count, long Deadline, int AttemptsLeft)
{
    public ActivityRecipeContinuation? Recipe { get; init; }
}
public sealed record PawnActivityOrder(string Pawn, string Assignment, string Method, long ExpectedRevision,
    long Revision, bool Release, PawnActivityStep[] Steps)
{
    /// <summary>A bounded obligation, never inventory credit or a predicted output identity.</summary>
    public ActivityOutputDelivery? OutputDelivery { get; init; }
    public string Task { get; init; } = "";
    public bool Preparing { get; init; }
}

/// <summary>One atomic execution-table edit under the existing world/authority.</summary>
public sealed record PawnActivities(ulong AuthorityGeneration, PawnActivityOrder[] Orders) : IActionPayload
{
    public EquatableArray<SelectedActivityTask> Tasks { get; init; }
    public static bool SameOrder(PawnActivityOrder a, PawnActivityOrder b) => a.Pawn == b.Pawn && a.Assignment == b.Assignment
        && a.Method == b.Method && a.ExpectedRevision == b.ExpectedRevision && a.Revision == b.Revision
        && a.Release == b.Release && a.OutputDelivery == b.OutputDelivery && a.Task == b.Task && a.Preparing == b.Preparing
        && a.Steps.SequenceEqual(b.Steps);
    private static void ValidatePractice(PawnActivityStep step)
    {
        DomainException.Require(step.Practice != null || step.Job is not ("RimGovernor_TrainShooting" or "RimGovernor_Spar"), "practice requires an assigned mandate");
        if (step.Practice is { } practice)
            DomainException.Require(step.Kind == PawnActivityKind.Work && Identity.IsValidText(practice.Role) && Identity.IsValidText(practice.Beneficiary)
                && practice.RequirementIndex >= 0 && practice.DurationTicks > 0 && practice.DurationTicks <= GameTime.TicksPerDay
                && (practice.Skill == "Shooting" && step.Job == "RimGovernor_TrainShooting" && practice.Partner.Length == 0 && practice.PartnerTarget.Length == 0
                    || practice.Skill == "Melee" && step.Job == "RimGovernor_Spar" && Identity.IsValidText(practice.Partner) && Identity.IsValidText(practice.PartnerTarget)),
                "invalid selected practice");
    }
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
                    && (step.Kind is PawnActivityKind.Hold or PawnActivityKind.Approach || Identity.IsValidText(step.Target))
                    && (step.Kind != PawnActivityKind.Work || Identity.IsValidText(step.WorkGiver) && Identity.IsValidText(step.Job))
                    && (step.Kind is not (PawnActivityKind.Eat or PawnActivityKind.Deliver or PawnActivityKind.Supply) || step.Count > 0)
                    && (step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Approach
                        ? step.Destination is { X: >= 0, Z: >= 0 } && step.WorkGiver.Length == 0 && step.Job.Length == 0
                        : step.Kind == PawnActivityKind.Work && step.Job == "BuildRoof"
                            ? step.Destination is { X: >= 0, Z: >= 0 } : step.Destination is null)
                    && step.NotBefore >= 0 && step.NotBefore < step.Deadline
                    && (step.Kind == PawnActivityKind.Supply || step.Kind == PawnActivityKind.Deliver && step.SupportDemand.Length != 0
                        ? Identity.IsValidText(step.Container) : step.Container.Length == 0)
                    && (step.SupportDemand.Length == 0 || (step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Work) && Identity.IsValidText(step.SupportDemand))
                    && (step.Medicine.Length == 0 || step.Kind == PawnActivityKind.Work && (step.Job is "TendPatient" or "DoBill") && Identity.IsValidText(step.Medicine)), "invalid activity step");
                ValidatePractice(step);
                if (step.Kind == PawnActivityKind.Recipe || step.Kind == PawnActivityKind.Work && step.Job == "DoBill")
                {
                    var cooking = step.Recipe;
                    DomainException.Require(cooking != null && (Identity.IsValidText(cooking.Bill) || Identity.IsValidText(cooking.Stuff)) && Identity.IsValidText(cooking.Recipe)
                        && Identity.IsValidText(step.WorkGiver) && (step.Job.Length == 0 || step.Kind == PawnActivityKind.Work && step.Job == "DoBill") && step.Count == 0
                        && (cooking.Unfinished.Length != 0 ? cooking.Ingredients.Count == 0 : cooking.Ingredients.Count is > 0 and <= 16)
                        && cooking.Ingredients.All(i => i != null && Identity.IsValidText(i.Thing) && i.Count > 0)
                        && cooking.Ingredients.Select(i => i.Thing).Distinct(StringComparer.Ordinal).Count() == cooking.Ingredients.Count,
                        "invalid exact cooking activity");
                }
                else DomainException.Require(step.Recipe == null, "cooking arguments on another activity family");
            }
            if (order.OutputDelivery is { } output)
            {
                DomainException.Require(Identity.IsValidText(output.Resource) && output.Count > 0 && output.Deadline > 0
                    && output.AttemptsLeft is >= 0 and <= 3 && output.Origin is { X: >= 0, Z: >= 0 }
                    && output.Destination is { X: >= 0, Z: >= 0 }, "invalid output delivery obligation");
                if (output.Recipe is { } cook)
                    DomainException.Require(Identity.IsValidText(cook.Bench) && Identity.IsValidText(cook.Bill)
                        && Identity.IsValidText(cook.Recipe) && Identity.IsValidText(cook.WorkGiver) && Identity.IsValidText(cook.Product)
                        && cook.ProductCount > 0 && cook.Origin is { X: >= 0, Z: >= 0 } && cook.Destination is { X: >= 0, Z: >= 0 }, "invalid cooking continuation");
            }
        }
        DomainException.Require(Tasks.Count <= 32 && Tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() == Tasks.Count,
            "invalid selected activity task identities");
        var taskIds = Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var task in Tasks)
            DomainException.Require(Identity.IsValidText(task.Id) && Orders.Any(o => o.Pawn == task.Pawn && !o.Release)
                && task.Prerequisites.All(p => p != task.Id && taskIds.Contains(p)) && task.AttemptsLeft is >= 0 and <= 3
                && (!task.ProductiveWorkTicks.IsKnown || task.Step.Kind == PawnActivityKind.Recipe
                    && double.IsFinite(task.ProductiveWorkTicks.Value) && task.ProductiveWorkTicks.Value > 0)
                && task.Inputs.All(i => Identity.IsValidText(i.Resource) && Identity.IsValidText(i.Lot) && i.Count > 0
                    && i.Location is { X: >= 0, Z: >= 0 } && Identity.IsValidText(i.Provenance))
                && task.DepartTick >= 0 && task.Step.Deadline > task.DepartTick, "invalid selected activity obligation");
        foreach (var task in Tasks) ValidatePractice(task.Step);
        foreach (var task in Tasks.Where(t => t.Step.Practice?.Skill == "Melee"))
            DomainException.Require(Tasks.Any(partner => partner.Pawn == task.Step.Practice!.Partner
                && partner.Step.Target == task.Step.Practice.PartnerTarget && partner.Step.Practice?.Partner == task.Pawn
                && partner.Step.Practice.PartnerTarget == task.Step.Target && partner.Pawn != task.Pawn && partner.Step.Target != task.Step.Target),
                "selected sparring needs reciprocal exact participants");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Count < Tasks.Count)
        {
            var ready = Tasks.Where(t => !visited.Contains(t.Id) && t.Prerequisites.All(visited.Contains)).ToArray();
            DomainException.Require(ready.Length > 0, "cyclic selected activity obligations");
            foreach (var task in ready) visited.Add(task.Id);
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
