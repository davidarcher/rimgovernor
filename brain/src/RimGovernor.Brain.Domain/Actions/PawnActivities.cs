namespace RimGovernor.Brain.Domain;

public enum PawnActivityKind { Work, Eat, Sleep, Hold, Recreation, Deliver, Recipe, Approach, Supply }

public sealed record PawnActivityPractice(string Skill, string Partner, string PartnerTarget, int DurationTicks);

public sealed record PawnActivityIngredient(string Thing, int Count);
public sealed record PawnActivityRecipe(string Bill, string Recipe, EquatableArray<PawnActivityIngredient> Ingredients)
{
    public string Resource { get; init; } = "";
    public string Stuff { get; init; } = "";
    public string Unfinished { get; init; } = "";
}

/// <summary>An authorized step, not a native-priority request. Deadlines are absolute game ticks.</summary>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(PawnActivityStep), "target")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(CellActivityStep), "cell")]
public record PawnActivityStep(PawnActivityKind Kind, string Target, string WorkGiver,
    string Job, int Count, long Deadline, string WaitForFilth = "")
{
    public CellActivityStep At(Cell destination) => new(this, destination);
    public PawnActivityPractice? Practice { get; init; }
    public string Medicine { get; init; } = "";
    public long NotBefore { get; init; }
    public string Container { get; init; } = "";
    public string SupportDemand { get; init; } = "";
    /// <summary>A remote Supply step (#3046): native carries a full load, <see cref="Target"/> then the <see cref="Collect"/>
    /// stacks up to the pawn's carry capacity; the site takes <see cref="Count"/> and vanilla drops the surplus beside it.</summary>
    public bool FullLoad { get; init; }
    /// <summary>The further same-def stacks a <see cref="FullLoad"/> Supply step picks up after <see cref="Target"/>, in order (#3046).</summary>
    public EquatableArray<string> Collect { get; init; }
}

/// <summary>An exact cell-targeted step: delivery or approach.</summary>
public sealed record CellActivityStep : PawnActivityStep
{
    public CellActivityStep(PawnActivityStep step, Cell destination) : base(step) => Destination = destination;
    [System.Text.Json.Serialization.JsonConstructor]
    public CellActivityStep(PawnActivityKind kind, string target, string workGiver, string job, int count, long deadline,
        Cell destination, string waitForFilth = "") : base(kind, target, workGiver, job, count, deadline, waitForFilth) => Destination = destination;
    [System.Text.Json.Serialization.JsonRequired]
    public Cell Destination { get; init; }
}

/// <summary>Selected remaining intent owned by this Method. No forecast or mutable progress flags.</summary>
public sealed record SelectedActivityInput(string Resource, string Lot, int Count, Cell Location, string Provenance)
{
    public bool Observed { get; init; }
    public EquatableArray<string> Producers { get; init; }
}

/// <summary>A selected task and its physical dependencies.</summary>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SelectedActivityTask), "task")]

public record SelectedActivityTask(string Id, string Pawn, EquatableArray<string> Prerequisites,
    PawnActivityStep Step, Cell Location, long DepartTick)
{
    public string Resource { get; init; } = "";
    /// <summary>The exact input/approach location; construction Supply approaches its source stack rather than its site.</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public Cell Source { get; init; } = Location;
    public string SiteDefinition { get; init; } = "";
    public string SupportDemand { get; init; } = "";
    public bool CompletionMustBeObserved { get; init; }
    public EquatableArray<SelectedActivityInput> Inputs { get; init; }
    /// <summary>The tick the selected schedule ends the task (its allocation's end), so a stack claim holds from
    /// <see cref="DepartTick"/> to here (#3626); 0 when not selected by a schedule, and the claim then holds to the step's deadline.</summary>
    public long UseUntilTick { get; init; }

    /// <summary>The <see cref="UseUntilTick"/> of a task scheduled to depart <paramref name="depart"/> and end <paramref name="end"/>
    /// ticks after <paramref name="tick"/>: ticks are whole, as <see cref="DepartTick"/> rounds up, and a use holds at least one.</summary>
    public static long UseUntil(long tick, double depart, double end) =>
        checked(tick + Math.Max((long)Math.Ceiling(end), (long)Math.Ceiling(depart) + 1));
}

public sealed record ActivityOutputDelivery(string Resource, Cell Origin, Cell Destination, int Count, long Deadline);

/// <summary>The concrete premise an exact intervention answers (forecast-contracts "Intervention justification").</summary>
public enum InterventionPurposeKind
{
    /// <summary>A pawn's need at or below its native threshold without observed native service.</summary>
    [WireName("need_shortfall")]
    NeedShortfall,

    /// <summary>The food owner's credible supply risk before harm.</summary>
    [WireName("food_risk")]
    FoodRisk,

    /// <summary>A closed calibration window's production stall that exact orders can answer.</summary>
    [WireName("production_stall")]
    ProductionStall,

    /// <summary>An assigned capability requirement unmet now or lapsing within the horizon.</summary>
    [WireName("capability_practice")]
    CapabilityPractice,

    /// <summary>A placed construction site's observed stall: no native work targeted it over a whole progress window.</summary>
    [WireName("construction_stall")]
    ConstructionStall,

    /// <summary>An observed tend demand no colonist's current job served over a whole care window.</summary>
    [WireName("unserved_care")]
    UnservedCare,
}

/// <summary>
/// What an exact intervention order is for, carried with the order so continuation and the next review re-evaluate it
/// rather than its elapsed ticks. <see cref="Kind"/> fixes the completion and cancellation semantics (pawn-activities
/// contract, "Intervention purpose"): a need shortfall completes when <see cref="SubjectPawn"/>'s <see cref="Subject"/> need
/// reads above its threshold and is cancelled when native service of it is observed; a food risk completes when the food
/// owner reviews no response needed and is cancelled when no consumer remains; a production stall completes when the
/// reconciled calibration holds no stall of the <see cref="Subject"/> product and is cancelled when the standing stall's
/// cause is one orders cannot answer; a construction stall completes when the <see cref="Subject"/> site's observed progress
/// changes after the stall closed or the site is no longer read; an unserved care completes when the <see cref="Subject"/> tend
/// demand is no longer read (the patient needs no tend now); a capability practice completes when the baseline projects no missing XP for the
/// <see cref="Subject"/> requirement and is cancelled when the requirement is withdrawn. <see cref="EvidenceTick"/> is the
/// tick of the observation that justified it: admission refuses a same-aim reissue whose evidence is not newer than the
/// end of an earlier same-aim intervention.
/// </summary>
public sealed record InterventionPurpose(InterventionPurposeKind Kind, string Subject, string SubjectPawn, long EvidenceTick)
{
    /// <summary>The purpose's aim without its evidence: what two interventions must share to duplicate one another.</summary>
    public string Aim => Wire.Name(Kind) + ":" + Subject + (SubjectPawn.Length == 0 ? "" : ":" + SubjectPawn);

    public bool SameAim(InterventionPurpose other) => Kind == other.Kind && Subject == other.Subject && SubjectPawn == other.SubjectPawn;

    /// <summary>Need shortfalls and capability practice name the pawn they serve; food risk, production and construction stalls are colony-wide.</summary>
    internal bool Valid() => Wire.IsDefined(Kind) && IdText.IsValidText(Subject) && EvidenceTick >= 0
        && (Kind is InterventionPurposeKind.NeedShortfall or InterventionPurposeKind.CapabilityPractice
            ? IdText.IsValidText(SubjectPawn) : SubjectPawn.Length == 0);
}

public sealed record PawnActivityOrder(string Pawn, string Assignment, string Method, long ExpectedRevision,
    long Revision, bool Release, PawnActivityStep[] Steps)
{
    /// <summary>A bounded obligation, never inventory credit or a predicted output identity.</summary>
    public ActivityOutputDelivery? OutputDelivery { get; init; }
    public string Task { get; init; } = "";
    public bool Preparing { get; init; }
    /// <summary>The bounded purpose of an exact intervention order, required on the non-release orders of a Method named with
    /// <see cref="PawnActivities.InterventionMethodPrefix"/>. Only a justified intervention gives one; routine orders carry none.</summary>
    public InterventionPurpose? Purpose { get; init; }
}

/// <summary>One atomic execution-table edit under the existing world/authority.</summary>
public sealed record PawnActivities(ulong AuthorityGeneration, PawnActivityOrder[] Orders) : IActionPayload
{
    /// <summary>The MethodId prefix of the shared comparison's exact interventions (ColonyReview's coupled Method).</summary>
    public const string InterventionMethodPrefix = "coupled-";
    public EquatableArray<SelectedActivityTask> Tasks { get; init; }
    public static bool Intervention(PawnActivityOrder order) => order.Method.StartsWith(InterventionMethodPrefix, StringComparison.Ordinal);
    public static bool SameOrder(PawnActivityOrder a, PawnActivityOrder b) => a.Pawn == b.Pawn && a.Assignment == b.Assignment
        && a.Method == b.Method && a.ExpectedRevision == b.ExpectedRevision && a.Revision == b.Revision
        && a.Release == b.Release && a.OutputDelivery == b.OutputDelivery && a.Task == b.Task && a.Preparing == b.Preparing
        && a.Purpose == b.Purpose && a.Steps.SequenceEqual(b.Steps);
    /// <summary>The exact Work a justified intervention installs (task 5.6 of native-led-labor-execution): selected practice,
    /// food harvest, a stalled site's frame finish and an unserved tend. Every other job (removal, roofing, sowing, research,
    /// surgery, repair, fire-fighting) is native work under settings and designations, never an exact order.</summary>
    public static bool ExactWork(string job) => job is "RimGovernor_TrainShooting" or "RimGovernor_Spar" or "Harvest" or "FinishFrame" or "TendPatient";
    private static void ValidatePractice(PawnActivityStep step)
    {
        DomainException.Require(step.Practice != null || step.Job is not ("RimGovernor_TrainShooting" or "RimGovernor_Spar"), "a practice job requires a selected practice");
        if (step.Practice is { } practice)
            DomainException.Require(step.Kind == PawnActivityKind.Work && practice.DurationTicks > 0 && practice.DurationTicks <= GameTime.TicksPerDay
                && (practice.Skill == "Shooting" && step.Job == "RimGovernor_TrainShooting" && practice.Partner.Length == 0 && practice.PartnerTarget.Length == 0
                    || practice.Skill == "Melee" && step.Job == "RimGovernor_Spar" && IdText.IsValidText(practice.Partner) && IdText.IsValidText(practice.PartnerTarget)),
                "invalid selected practice");
    }
    private static bool ValidDestination(PawnActivityStep step) => step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Approach
        ? step is CellActivityStep { Destination: { X: >= 0, Z: >= 0 } } && step.WorkGiver.Length == 0 && step.Job.Length == 0
        : step is not CellActivityStep;
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
            DomainException.Require(order.Purpose is { } purpose ? purpose.Valid() : order.Release || !Intervention(order),
                "an exact intervention order needs a valid purpose");
            foreach (var step in order.Steps)
            {
                DomainException.Require(step != null && Enum.IsDefined(step.Kind) && step.Deadline > 0
                    && (step.Kind is PawnActivityKind.Hold or PawnActivityKind.Approach || IdText.IsValidText(step.Target))
                    && (step.Kind != PawnActivityKind.Work || IdText.IsValidText(step.WorkGiver) && IdText.IsValidText(step.Job))
                    && (step.Kind is not (PawnActivityKind.Eat or PawnActivityKind.Deliver or PawnActivityKind.Supply) || step.Count > 0)
                    && ValidDestination(step)
                    && step.NotBefore >= 0 && step.NotBefore < step.Deadline
                    && (step.Kind == PawnActivityKind.Supply || step.Kind == PawnActivityKind.Deliver && step.SupportDemand.Length != 0
                        ? IdText.IsValidText(step.Container) : step.Container.Length == 0)
                    && (step.SupportDemand.Length == 0 || (step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Work) && IdText.IsValidText(step.SupportDemand))
                    && (step.Medicine.Length == 0 || step.Kind == PawnActivityKind.Work && step.Job == "TendPatient" && IdText.IsValidText(step.Medicine)), "invalid activity step");
                DomainException.Require(step.Kind != PawnActivityKind.Work || ExactWork(step.Job), "routine work runs as native work, never an exact order");
                ValidatePractice(step);
                DomainException.Require((!step.FullLoad || step.Kind == PawnActivityKind.Supply)
                    && (step.Collect.Count == 0 || step.FullLoad && step.Collect.Count <= 16 && step.Collect.All(IdText.IsValidText)
                        && step.Collect.Distinct(StringComparer.Ordinal).Count() == step.Collect.Count && !step.Collect.Contains(step.Target)), "invalid full-load supply step");
                // Bill work runs through native bills and work settings (task 5.3): a Recipe kind names a comparison binding, never a step.
                DomainException.Require(step.Kind != PawnActivityKind.Recipe, "a bill step runs through native bills and work settings");
            }
            if (order.OutputDelivery is { } output)
            {
                DomainException.Require(IdText.IsValidText(output.Resource) && output.Count > 0 && output.Deadline > 0
                    && output.Origin is { X: >= 0, Z: >= 0 }
                    && output.Destination is { X: >= 0, Z: >= 0 }, "invalid output delivery obligation");
            }
        }
        DomainException.Require(Tasks.Count <= 32 && Tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() == Tasks.Count,
            "invalid selected activity task identities");
        var taskIds = Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var task in Tasks)
            DomainException.Require(IdText.IsValidText(task.Id) && Orders.Any(o => o.Pawn == task.Pawn && !o.Release)
                && task.Prerequisites.All(p => p != task.Id && taskIds.Contains(p))
                && task.Inputs.All(i => IdText.IsValidText(i.Resource) && IdText.IsValidText(i.Lot) && i.Count > 0
                    && i.Location is { X: >= 0, Z: >= 0 } && IdText.IsValidText(i.Provenance)
                    && i.Producers.All(p => p != task.Id && IdText.IsValidText(p)))
                && task.Source is { X: >= 0, Z: >= 0 } && task.Location is { X: >= 0, Z: >= 0 }
                && task.DepartTick >= 0 && task.Step.Deadline > task.DepartTick, "invalid selected activity obligation");
        foreach (var task in Tasks)
        {
            ValidatePractice(task.Step);
            DomainException.Require(task.Step.Kind != PawnActivityKind.Work || ExactWork(task.Step.Job), "routine work runs as native work, never an exact order");
            DomainException.Require(ValidDestination(task.Step), "invalid selected activity destination");
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
