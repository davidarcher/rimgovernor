namespace RimGovernor.Brain.Domain;

/// <summary><see cref="Standby"/> is the explicit idle every controlled colonist holds when nothing is selected for it (#3322):
/// native wander or wait, no work, no deadline. It is the order's only step and any other assignment replaces it from
/// revision zero (pawn-activities.md).</summary>
public enum PawnActivityKind { Work, Eat, Sleep, Hold, Recreation, Deliver, Recipe, Approach, Supply, Idle, Standby }

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
    /// <summary>A set step (#3613): the Work repeats over these thing ids, nearest first, until <see cref="Count"/> members
    /// are done or none is left legal. Empty keeps the single-<see cref="Target"/> behaviour.</summary>
    public EquatableArray<string> Targets { get; init; }
    /// <summary>The cell form of a set step (#3615): the Work repeats over these cells, nearest first (RemoveFloor; RemoveRoof
    /// over the roofed cells a roof-holder leaves unsupported, #3617).</summary>
    public EquatableArray<Cell> Cells { get; init; }
    public bool IsSet => Targets.Count > 0 || Cells.Count > 0;
}

/// <summary>Selected remaining intent owned by this Method. No forecast or mutable progress flags.</summary>
public sealed record SelectedActivityInput(string Resource, string Lot, int Count, Cell Location, string Provenance)
{
    public bool Observed { get; init; }
    public EquatableArray<string> Producers { get; init; }
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
    /// <summary>Work steps the task's order runs before <see cref="Step"/>, in order (#3617: a roof-holder's RemoveRoof
    /// cell set before its Deconstruct). The same task, relief and output as the step alone.</summary>
    public EquatableArray<PawnActivityStep> PriorSteps { get; init; }
    /// <summary>The tick the selected schedule ends the task (its allocation's end), so a stack claim holds from
    /// <see cref="DepartTick"/> to here (#3626); 0 when not selected by a schedule, and the claim then holds to the step's deadline.</summary>
    public long UseUntilTick { get; init; }

    /// <summary>The <see cref="UseUntilTick"/> of a task scheduled to depart <paramref name="depart"/> and end <paramref name="end"/>
    /// ticks after <paramref name="tick"/>: ticks are whole, as <see cref="DepartTick"/> rounds up, and a use holds at least one.</summary>
    public static long UseUntil(long tick, double depart, double end) =>
        checked(tick + Math.Max((long)Math.Ceiling(end), (long)Math.Ceiling(depart) + 1));
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
    /// <summary>A live order whose one step is <see cref="PawnActivityKind.Standby"/>: an explicit idle that claims nothing.</summary>
    public static bool IsStandby(PawnActivityOrder order) => !order.Release && order.Steps is [{ Kind: PawnActivityKind.Standby }];
    /// <summary>A live order that only defers its pawn: one <see cref="PawnActivityKind.Idle"/> step until the pawn is
    /// reconsidered (#3047).</summary>
    public static bool IsDeferral(PawnActivityOrder order) => !order.Release && order.Task.StartsWith("defer/", StringComparison.Ordinal)
        && order.Steps is [{ Kind: PawnActivityKind.Idle }];
    public static bool SameOrder(PawnActivityOrder a, PawnActivityOrder b) => a.Pawn == b.Pawn && a.Assignment == b.Assignment
        && a.Method == b.Method && a.ExpectedRevision == b.ExpectedRevision && a.Revision == b.Revision
        && a.Release == b.Release && a.OutputDelivery == b.OutputDelivery && a.Task == b.Task && a.Preparing == b.Preparing
        && a.Steps.SequenceEqual(b.Steps);
    /// <summary>A set step (#3613) is Work over distinct thing ids or cells, never both, with <c>Count</c> the set's size.</summary>
    private static void ValidateSet(PawnActivityStep step)
    {
        if (!step.IsSet) return;
        DomainException.Require(step.Kind == PawnActivityKind.Work && step.Targets.Count <= 64 && step.Cells.Count <= 256
            && (step.Targets.Count == 0 || step.Cells.Count == 0) && step.Count == step.Targets.Count + step.Cells.Count
            && step.Targets.All(IdText.IsValidText) && step.Targets.Distinct(StringComparer.Ordinal).Count() == step.Targets.Count
            && step.Cells.All(c => c is { X: >= 0, Z: >= 0 }) && step.Cells.Distinct().Count() == step.Cells.Count, "invalid activity set step");
    }
    private static void ValidatePractice(PawnActivityStep step)
    {
        DomainException.Require(step.Practice != null || step.Job is not ("RimGovernor_TrainShooting" or "RimGovernor_Spar"), "practice requires an assigned mandate");
        if (step.Practice is { } practice)
            DomainException.Require(step.Kind == PawnActivityKind.Work && IdText.IsValidText(practice.Role) && IdText.IsValidText(practice.Beneficiary)
                && practice.RequirementIndex >= 0 && practice.DurationTicks > 0 && practice.DurationTicks <= GameTime.TicksPerDay
                && (practice.Skill == "Shooting" && step.Job == "RimGovernor_TrainShooting" && practice.Partner.Length == 0 && practice.PartnerTarget.Length == 0
                    || practice.Skill == "Melee" && step.Job == "RimGovernor_Spar" && IdText.IsValidText(practice.Partner) && IdText.IsValidText(practice.PartnerTarget)),
                "invalid selected practice");
    }
    public void Validate()
    {
        DomainException.Require(AuthorityGeneration > 0 && Orders is { Length: > 0 and <= 64 }, "activity batch requires authority and bounded orders");
        var pawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in Orders)
        {
            DomainException.Require(order != null && IdText.IsValidText(order.Pawn) && pawns.Add(order.Pawn)
                && IdText.IsValidText(order.Assignment) && IdText.IsValidText(order.Method)
                && order.ExpectedRevision >= 0 && order.Revision > order.ExpectedRevision
                && order.Steps != null && (order.Release ? order.Steps.Length == 0 : order.Steps.Length is > 0 and <= 64), "invalid activity assignment");
            foreach (var step in order.Steps)
            {
                DomainException.Require(step != null && Enum.IsDefined(step.Kind) && step.Deadline > 0
                    && (step.Kind is PawnActivityKind.Hold or PawnActivityKind.Idle or PawnActivityKind.Standby or PawnActivityKind.Approach || IdText.IsValidText(step.Target))
                    && (step.Kind != PawnActivityKind.Standby || order.Steps.Length == 1 && step.Target.Length == 0 && step.Count == 0
                        && step.WorkGiver.Length == 0 && step.Job.Length == 0 && step.WaitForFilth.Length == 0 && order.OutputDelivery == null && order.Task.Length == 0)
                    && (step.Kind != PawnActivityKind.Work || IdText.IsValidText(step.WorkGiver) && IdText.IsValidText(step.Job))
                    && (step.Kind is not (PawnActivityKind.Eat or PawnActivityKind.Deliver or PawnActivityKind.Supply) || step.Count > 0)
                    && (step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Approach
                        ? step.Destination is { X: >= 0, Z: >= 0 } && step.WorkGiver.Length == 0 && step.Job.Length == 0
                        : step.Kind == PawnActivityKind.Work && step.Job is "BuildRoof" or "Sow"
                            ? step.Destination is { X: >= 0, Z: >= 0 } && (step.Job != "Sow" || step.Count > 0) : step.Destination is null)
                    && step.NotBefore >= 0 && step.NotBefore < step.Deadline
                    && (step.Kind == PawnActivityKind.Supply || step.Kind == PawnActivityKind.Deliver && step.SupportDemand.Length != 0
                        ? IdText.IsValidText(step.Container) : step.Container.Length == 0)
                    && (step.SupportDemand.Length == 0 || (step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Work) && IdText.IsValidText(step.SupportDemand))
                    && (step.Medicine.Length == 0 || step.Kind == PawnActivityKind.Work && (step.Job is "TendPatient" or "DoBill") && IdText.IsValidText(step.Medicine)), "invalid activity step");
                ValidateSet(step);
                ValidatePractice(step);
                if (step.Kind == PawnActivityKind.Recipe || step.Kind == PawnActivityKind.Work && step.Job == "DoBill")
                {
                    var cooking = step.Recipe;
                    DomainException.Require(cooking != null && (IdText.IsValidText(cooking.Bill) || IdText.IsValidText(cooking.Stuff)) && IdText.IsValidText(cooking.Recipe)
                        && IdText.IsValidText(step.WorkGiver) && (step.Job.Length == 0 || step.Kind == PawnActivityKind.Work && step.Job == "DoBill") && step.Count == 0
                        && (cooking.Unfinished.Length != 0 ? cooking.Ingredients.Count == 0 : cooking.Ingredients.Count is > 0 and <= 16)
                        && cooking.Ingredients.All(i => i != null && IdText.IsValidText(i.Thing) && i.Count > 0)
                        && cooking.Ingredients.Select(i => i.Thing).Distinct(StringComparer.Ordinal).Count() == cooking.Ingredients.Count,
                        "invalid exact cooking activity");
                }
                else DomainException.Require(step.Recipe == null, "cooking arguments on another activity family");
            }
            if (order.OutputDelivery is { } output)
            {
                DomainException.Require(IdText.IsValidText(output.Resource) && output.Count > 0 && output.Deadline > 0
                    && output.AttemptsLeft is >= 0 and <= 3 && output.Origin is { X: >= 0, Z: >= 0 }
                    && output.Destination is { X: >= 0, Z: >= 0 }, "invalid output delivery obligation");
                if (output.Recipe is { } cook)
                    DomainException.Require(IdText.IsValidText(cook.Bench) && IdText.IsValidText(cook.Bill)
                        && IdText.IsValidText(cook.Recipe) && IdText.IsValidText(cook.WorkGiver) && IdText.IsValidText(cook.Product)
                        && cook.ProductCount > 0 && cook.Origin is { X: >= 0, Z: >= 0 } && cook.Destination is { X: >= 0, Z: >= 0 }, "invalid cooking continuation");
            }
        }
        DomainException.Require(Tasks.Count <= 32 && Tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() == Tasks.Count,
            "invalid selected activity task identities");
        var taskIds = Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var task in Tasks)
            DomainException.Require(IdText.IsValidText(task.Id) && Orders.Any(o => o.Pawn == task.Pawn && !o.Release)
                && task.Prerequisites.All(p => p != task.Id && taskIds.Contains(p)) && task.AttemptsLeft is >= 0 and <= 3
                && (!task.ProductiveWorkTicks.IsKnown || task.Step.Kind == PawnActivityKind.Recipe
                    && double.IsFinite(task.ProductiveWorkTicks.Value) && task.ProductiveWorkTicks.Value > 0)
                && task.Inputs.All(i => IdText.IsValidText(i.Resource) && IdText.IsValidText(i.Lot) && i.Count > 0
                    && i.Location is { X: >= 0, Z: >= 0 } && IdText.IsValidText(i.Provenance)
                    && i.Producers.All(p => p != task.Id && IdText.IsValidText(p)))
                && task.DepartTick >= 0 && task.Step.Deadline > task.DepartTick, "invalid selected activity obligation");
        foreach (var task in Tasks)
        {
            ValidateSet(task.Step); ValidatePractice(task.Step);
            DomainException.Require(task.PriorSteps.Count <= 8, "invalid selected prior steps");
            foreach (var prior in task.PriorSteps)
            {
                DomainException.Require(prior != null && prior.Kind == PawnActivityKind.Work && prior.Deadline > 0 && IdText.IsValidText(prior.Target)
                    && IdText.IsValidText(prior.WorkGiver) && IdText.IsValidText(prior.Job) && prior.NotBefore >= 0 && prior.NotBefore < prior.Deadline
                    && prior.Destination is null && prior.Recipe == null && prior.Practice == null && prior.Container.Length == 0
                    && prior.SupportDemand.Length == 0 && prior.Medicine.Length == 0, "invalid selected prior step");
                ValidateSet(prior);
            }
        }
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
