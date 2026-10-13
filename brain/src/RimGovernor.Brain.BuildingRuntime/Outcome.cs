namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What a planner's step came to: the closed set every routine result reports. Routines branch on it (and, for a
/// wait or a refusal, on the refusal's kind); no outcome hides a second meaning.
/// </summary>
public enum Outcome
{
    /// <summary>The step reached no verdict (the caller keeps going).</summary>
    [WireName("")]
    None = 0,

    /// <summary>The step admitted a plan.</summary>
    Admitted,

    /// <summary>The goal has no active deficit.</summary>
    NothingToDo,

    /// <summary>The planner is switched off in this runtime.</summary>
    Disabled,

    /// <summary>There is no current rounds to judge.</summary>
    [WireName("no_current_review")]
    NoReview,

    /// <summary>The proposal went stale before it could be admitted.</summary>
    Expired,

    /// <summary>The fight's stop sent changed orders, recorded as its plan's evidence.</summary>
    [WireName("combat_orders")]
    OrdersSent,

    /// <summary>The fight's hold-the-line formation was re-formed as squad defense because the raid crossed the line.</summary>
    HoldFallback,

    /// <summary>The goal waits on something that is not a failure; the refusal names what with a wait kind.</summary>
    Waiting,

    /// <summary>The step could not proceed; the refusal says why.</summary>
    Refused,
}

/// <summary>
/// Says why an <see cref="Outcome.Refused"/> step stopped or what an <see cref="Outcome.Waiting"/> goal waits on: a
/// cause, the thing it concerns and optional detail. Detail is machine-only: it rides in
/// <see cref="Verdict.ToString"/> and never into persisted rows or wording.
/// </summary>
public readonly record struct Refusal(Cause Kind, string Subject = "", string Detail = "");

/// <summary>
/// The pair every routine result embeds: its <see cref="Outcome"/> and, when refused or waiting, the
/// <see cref="Refusal"/>. The default verdict means the step reached no verdict (the caller keeps going).
/// </summary>
public readonly record struct Verdict(Outcome Outcome, Refusal Refusal = default)
{
    // The refusal causes: an Outcome.Refused verdict carries one of these; there is no catch-all. English for
    // them lives in Wording.
    private static readonly HashSet<Cause> RefusalKinds =
    [
        Cause.CollapsePending, Cause.NoWorker, Cause.AwaitingPlan, Cause.FieldUnavailable, Cause.NoSpace,
        Cause.SharedAdmission, Cause.RetriesSpent, Cause.RockNotDug, Cause.SiteBlocked, Cause.HeldNoSource,
    ];

    /// <summary>A refused verdict. A kind outside the closed set is a programming error and throws.</summary>
    public static Verdict Refuse(Cause kind, string subject, string detail) =>
        MustValid(new Verdict(Outcome.Refused, new Refusal(kind, subject, detail)));

    /// <summary>A waiting verdict; a kind outside the wait kinds throws.</summary>
    public static Verdict WaitOn(Cause kind) => MustValid(new Verdict(Outcome.Waiting, new Refusal(kind)));

    /// <summary>A waiting verdict that names what it concerns.</summary>
    public static Verdict WaitFor(Cause kind, string subject) => MustValid(new Verdict(Outcome.Waiting, new Refusal(kind, subject)));

    private static Verdict MustValid(Verdict v)
    {
        v.Validate();
        return v;
    }

    public static Verdict FieldUnavailable(string field) => Refuse(Cause.FieldUnavailable, field, "");

    public static Verdict NoSpace(string subject) => Refuse(Cause.NoSpace, subject, "");

    public static Verdict RockNotDug(string subject, string detail) => Refuse(Cause.RockNotDug, subject, detail);

    public static Verdict NoWorker(string subject) => Refuse(Cause.NoWorker, subject, "");

    public static Verdict SiteBlocked(string subject, string detail) => Refuse(Cause.SiteBlocked, subject, detail);

    public static Verdict CollapsePending(string subject) => Refuse(Cause.CollapsePending, subject, "");

    public static Verdict AwaitingPlan(string subject, string detail) => Refuse(Cause.AwaitingPlan, subject, detail);

    /// <summary>
    /// The reasoned hold of resources whose source census is empty (no rock, tree or plant yields them): the
    /// subject names them, comma-joined.
    /// </summary>
    public static Verdict NoSource(params Resource[] resources) =>
        Refuse(Cause.HeldNoSource, string.Join(',', resources.Select(r => r.Value)), "");

    /// <summary>The wait of a step whose claim another planner already holds this step (a bench, an animal); the subject names the claim.</summary>
    public static Verdict ClaimHeld(string subject) => MustValid(new Verdict(Outcome.Waiting, new Refusal(Cause.Claim, subject)));

    /// <summary>The wait of a subject native refused transiently in a world that has not changed since; the detail is native's reason.</summary>
    public static Verdict RetryBudgetWait(string subject, string detail) =>
        MustValid(new Verdict(Outcome.Waiting, new Refusal(Cause.RetryBudgetWait, subject, detail)));

    /// <summary>The refusal for a policy method that names what the goal waits on (a power or temperature wait, a cooler's power).</summary>
    public static Verdict AwaitingMethod<TMethod>(TMethod method)
        where TMethod : struct, Enum => AwaitingPlan(Wire.Name(method), "");

    /// <summary>The refusal of a method that needs a native research project the census has not finished; EnsureResearch's roadmap is what gets there.</summary>
    public static Verdict ResearchWait(string project) => AwaitingPlan("research", project);

    /// <summary>The refusal of a method the food plan does not yet support: capacity names what the plan has to provide.</summary>
    public static Verdict AwaitingFoodPlan(string capacity) => AwaitingPlan("food_plan", capacity);

    /// <summary>
    /// Throws <see cref="DomainException"/> for a verdict that is not one of the closed forms: an unknown outcome,
    /// a refusal or wait without a kind from its closed set, a refusal on an outcome that carries none, or a
    /// shared-admission refusal that names no reason.
    /// </summary>
    public void Validate()
    {
        switch (Outcome)
        {
            case Outcome.None:
                DomainException.Require(this == default, $"verdict without an outcome: {this}");
                break;
            case Outcome.Admitted or Outcome.NothingToDo or Outcome.Disabled or Outcome.NoReview or Outcome.Expired or Outcome.OrdersSent or Outcome.HoldFallback:
                DomainException.Require(Refusal == default, $"{Wire.Name(Outcome)} verdict carries a refusal: {Refusal}");
                break;
            case Outcome.Waiting:
                DomainException.Require(PolicyFunctions.WaitCauses.Contains(Refusal.Kind), $"wait without a known kind: {Refusal}");
                DomainException.Require(Refusal.Kind != Cause.MethodUsed || Refusal.Subject.Length != 0, $"method-used wait without the work it names: {Refusal}");
                break;
            case Outcome.Refused:
                DomainException.Require(RefusalKinds.Contains(Refusal.Kind), $"refusal without a known kind: {Refusal}");
                DomainException.Require(Refusal.Kind != Cause.SharedAdmission || Refusal.Subject.Length != 0, $"shared admission refusal without the reason it names: {Refusal}");
                DomainException.Require(Refusal.Kind != Cause.RetriesSpent || Refusal.Subject.Length != 0, $"retry budget refusal without the step it names: {Refusal}");
                break;
            default:
                throw new DomainException($"unknown outcome {Outcome}");
        }
    }

    /// <summary>Whether the step reached no verdict.</summary>
    public bool IsZero => this == default;

    /// <summary>Whether this is a refusal or wait of <paramref name="kind"/>.</summary>
    public bool Is(Cause kind) => (Outcome is Outcome.Refused or Outcome.Waiting) && Refusal.Kind == kind;

    /// <summary>
    /// The machine rendering of a verdict for the OTLP log ring, the timeline's reason= field and snapshot
    /// names, one token without spaces: a refusal or wait reads "kind", "kind:subject" or "kind:subject:detail";
    /// any other verdict reads its outcome.
    /// </summary>
    public override string ToString()
    {
        if (Outcome is not (Outcome.Refused or Outcome.Waiting))
        {
            return Wire.Name(Outcome);
        }
        var parts = new List<string> { Wire.Name(Refusal.Kind) };
        if (Refusal.Subject.Length != 0)
        {
            parts.Add(Refusal.Subject);
            if (Refusal.Detail.Length != 0)
            {
                parts.Add(Refusal.Detail);
            }
        }
        return string.Join(':', parts);
    }

    /// <summary>A shell attempt that leaves the usual placement to go on: its method already used, no verified space, or a fact it needs unknown.</summary>
    internal bool SkipsToPlacement => Is(Cause.MethodUsed) || Is(Cause.NoSpace) || Is(Cause.FieldUnavailable);
}

/// <summary>
/// The shared verdicts (Go <c>BuildingReason*</c>). Exits that need a more specific refusal build one with
/// <see cref="Verdict.Refuse"/> and its helpers; none may be a catch-all.
/// </summary>
internal static partial class BuildingRuntimePackage
{
    public static readonly Verdict BuildingReasonAdmitted = new(Outcome.Admitted);
    public static readonly Verdict BuildingReasonDisabled = new(Outcome.Disabled);
    public static readonly Verdict BuildingReasonNoReview = new(Outcome.NoReview);
    public static readonly Verdict BuildingReasonNoDeficit = new(Outcome.NothingToDo);
    public static readonly Verdict BuildingReasonExpired = new(Outcome.Expired);
    public static readonly Verdict BuildingReasonExistingWork = Verdict.WaitOn(Cause.ExistingWork);

    /// <summary>The initial shelter's indoor furnishing waits on its open bunk rungs, which do not hold the ring itself.</summary>
    public static readonly Verdict BuildingReasonBunksOpen = Verdict.WaitOn(Cause.BunksOpen);

    public static readonly Verdict BuildingReasonHoldFallback = new(Outcome.HoldFallback);
    public static readonly Verdict BuildingReasonCombatOrders = new(Outcome.OrdersSent);

    /// <summary>Defers a butcher bill while the separated butcher spot build still owns the food-supply goal.</summary>
    public static readonly Verdict BuildingReasonSeparation = Verdict.WaitOn(Cause.Separation);

    /// <summary>The choice dialog's own interactivity delay has not elapsed; the next review re-reads it.</summary>
    public static readonly Verdict BuildingReasonNotInteractive = Verdict.WaitOn(Cause.Dialog);

    /// <summary>
    /// A proposal planner's result when the coordinator gave a claim it needs to a higher-ranked proposal; the
    /// step row's proposal outcome names the claim.
    /// </summary>
    public static readonly Verdict BuildingReasonWaiting = Verdict.WaitOn(Cause.Claim);

    /// <summary>The shrine planner's answer while every target shrine holds; the shrine result's hold carries the reason.</summary>
    public static readonly Verdict BuildingReasonHeld = Verdict.WaitOn(Cause.BreachHeld);

    public static readonly Verdict BuildingReasonComfortWait = Verdict.WaitOn(Cause.ComfortUse);
    public static readonly Verdict BuildingReasonTemperatureWait = Verdict.WaitOn(Cause.RoomTemperature);
    public static readonly Verdict BuildingReasonExistingFacility = Verdict.WaitOn(Cause.Facility);
    public static readonly Verdict BuildingReasonHospitalConvert = Verdict.WaitOn(Cause.HospitalConvert);
    public static readonly Verdict BuildingReasonSleepingUseNeeded = Verdict.WaitOn(Cause.SleepingUse);
    public static readonly Verdict BuildingReasonNoSquad = Verdict.NoWorker("squad");
    public static readonly Verdict BuildingReasonShellBlocked = Verdict.AwaitingPlan("earlier_shell", "");
    public static readonly Verdict BuildingReasonShelterPending = Verdict.AwaitingPlan("initial_shelter", "");
    public static readonly Verdict BuildingReasonThreatPending = Verdict.AwaitingPlan("raid", "");
    public static readonly Verdict BuildingReasonSuiteStock = Verdict.AwaitingPlan("suite_materials", "walls");
    public static readonly Verdict BuildingReasonWorkshopUnavailable = Verdict.NoWorker("workshop_bench");
    public static readonly Verdict BuildingReasonWorkshopResearch = Verdict.AwaitingPlan("research", "workshop");
    public static readonly Verdict BuildingReasonResearchBench = Verdict.AwaitingPlan("research_bench", "");
    public static readonly Verdict BuildingReasonResearchBenchUnavailable = Verdict.AwaitingPlan("research_bench", "unbuildable");
    public static readonly Verdict BuildingReasonHospitalUnavailable = Verdict.AwaitingPlan("buildable_bed", "hospital");
    public static readonly Verdict BuildingReasonSleepingUnavailable = Verdict.AwaitingPlan("buildable_bed", "");

    /// <summary>Holds a site search that anchors on the layout plan until one exists: nothing is sited on where the colonists stand.</summary>
    public static readonly Verdict BuildingReasonNoLayoutPlan = Verdict.AwaitingPlan("layout_plan", "");

    public static readonly Verdict BuildingReasonNoWeaponBench = Verdict.AwaitingPlan("weapon_bench", "");

    /// <summary>
    /// A proposal planner's result when the step's stock, less the quantities earlier proposals claimed and
    /// admitted plans hold, does not cover a quantity it needs and no less urgent commitment could be preempted
    /// to release it; the outcome's Demand is the shortfall.
    /// </summary>
    public static readonly Verdict BuildingReasonDemand = Verdict.AwaitingPlan("stock", "unmet_demand");

    internal static readonly Verdict BuildingReasonStoneShellUnstocked = Verdict.AwaitingPlan("replacement_material", "");
    internal static readonly Verdict BuildingReasonDefensePerimeterNoStone = Verdict.AwaitingPlan("perimeter_stone", "");
}
