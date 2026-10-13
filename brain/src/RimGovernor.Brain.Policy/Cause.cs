namespace RimGovernor.Brain.Policy;

// Go: cause.go

/// <summary>
/// The closed set of reasons the bot explains itself with: why a concern did not advance, what it waits on,
/// which input it could not read. Wire values are the strings the sources already used (refusal and wait
/// kinds, the unread-input names, BlockedReason, admission Reason); a string two sources share (no_worker,
/// native_ineligible) is one Cause. Wording is the only place English for a Cause lives.
/// </summary>
public readonly partial record struct Cause(string Value) : IComparable<Cause>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool Equals(Cause other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(Cause other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    // Refusal kinds and the fixed blocked reasons.
    public static readonly Cause CollapsePending = new("collapse_pending");
    public static readonly Cause NoWorker = new("no_worker");
    public static readonly Cause AwaitingPlan = new("awaiting_plan");
    public static readonly Cause FieldUnavailable = new("field_unavailable");
    public static readonly Cause NoSpace = new("no_space");
    public static readonly Cause SharedAdmission = new("shared_admission_refused");
    public static readonly Cause RetriesSpent = new("retry_budget_spent");
    public static readonly Cause RockNotDug = new("rock_not_dug");
    public static readonly Cause SiteBlocked = new("site_blocked");
    public static readonly Cause NativeIneligible = new("native_ineligible");
    public static readonly Cause ReconcileWrite = new("reconcile_write");
    public static readonly Cause Cooldown = new("cooldown");
    public static readonly Cause NoMethod = new("no_method");
    public static readonly Cause HeldUnavailable = new("held:unavailable");
    public static readonly Cause HeldOptIn = new("held:opt-in");
    public static readonly Cause HeldNoSource = new("held:no_source");

    // Wait kinds.
    public static readonly Cause MethodUsed = new("method_already_used");
    public static readonly Cause ExistingWork = new("already_working_on_it");
    public static readonly Cause BunksOpen = new("shelter_bunks_open");
    public static readonly Cause BreachHeld = new("breach_held");
    public static readonly Cause ComfortUse = new("waiting_for_native_comfort_use");
    public static readonly Cause Facility = new("existing_facility_needs_bill_or_upkeep");
    public static readonly Cause HospitalConvert = new("hospital_bed_convert_pending");
    public static readonly Cause SleepingUse = new("sleeping_use_needed");
    public static readonly Cause Separation = new("butcher_separation_pending");
    public static readonly Cause Dialog = new("dialog_not_interactive");
    public static readonly Cause Claim = new("waiting_on_claim");
    public static readonly Cause FacilityAccess = new("existing_facility_access_blocked");
    public static readonly Cause RoomTemperature = new("waiting_for_native_temperature");
    public static readonly Cause RetryBudgetWait = new("retry_budget_waiting");

    // CombatOrders and HoldFallback are the fight outcomes, waits in all but name: nothing failed. They are
    // not wait kinds a Verdict may carry.
    public static readonly Cause CombatOrders = new("combat_orders");
    public static readonly Cause HoldFallback = new("hold_fallback");

    // Admission reasons that are not already a Cause above.
    public static readonly Cause NotReady = new("not_ready");
    public static readonly Cause AlreadyReserved = new("already_reserved");
    public static readonly Cause UnknownFacts = new("unknown_facts");
    public static readonly Cause StaleFacts = new("stale_facts");
    public static readonly Cause UnsafePlacement = new("unsafe_placement");
    public static readonly Cause UnsafeThreat = new("unsafe_threat");
    public static readonly Cause CriticalMedical = new("critical_medical");
    public static readonly Cause MaterialRequired = new("explicit_material_required");
    public static readonly Cause DependencyBlocked = new("dependency_incomplete");
    public static readonly Cause GeometryBlocked = new("geometry_conflict");
    public static readonly Cause InvalidHeld = new("held_reservation_unverifiable");
    public static readonly Cause ExcavationUnsupported = new("excavation_unsupported");
    public static readonly Cause ExcavationGeometryChanged = new("excavation_geometry_changed");
    public static readonly Cause UnsafeRoute = new("unsafe_route");
    public static readonly Cause RoofSupportRisk = new("roof_support_risk");
    public static readonly Cause StorageMissing = new("missing_storage");
    public static readonly Cause UrgentCompetingWork = new("urgent_competing_work");

    // Unread facts: the one input a planner did not read when it abstained from declaring. Every abstain
    // site picks one, so the ledger view can say which input was missing instead of only who abstained.
    public static readonly Cause UnreadReview = new("review");
    public static readonly Cause UnreadStock = new("stock");
    public static readonly Cause UnreadBenches = new("benches");
    public static readonly Cause UnreadBenchDef = new("bench_definition");
    public static readonly Cause UnreadBills = new("bills");
    public static readonly Cause UnreadRecipes = new("recipes");
    public static readonly Cause UnreadDefinitions = new("definitions");
    public static readonly Cause UnreadNativeRead = new("native_source");
    public static readonly Cause UnreadMedicine = new("medicine");
    public static readonly Cause UnreadArtists = new("artists");
    public static readonly Cause UnreadArtNeed = new("art_need");
    public static readonly Cause UnreadGearRecover = new("gear_recovery");
    public static readonly Cause UnreadGearNeeds = new("gear_replacements");
    public static readonly Cause UnreadGearDemand = new("gear_demand");
    public static readonly Cause UnreadArmoryTier = new("armory_tier");
    public static readonly Cause UnreadWeapons = new("weapon_demand");
    public static readonly Cause UnreadShells = new("shell_targets");
    public static readonly Cause UnreadFoodFacts = new("food_facts");
    public static readonly Cause UnreadFoodPlan = new("food_plan");
    public static readonly Cause UnreadFoodStorage = new("food_storage");
    public static readonly Cause UnreadBabyFeeding = new("baby_feeding");
    public static readonly Cause UnreadMechs = new("mech_gestation");
    public static readonly Cause UnreadWort = new("wort");
    public static readonly Cause UnreadQuestOffers = new("quest_offers");
    public static readonly Cause UnreadQuestAsker = new("quest_asker");
    public static readonly Cause UnreadSilverGap = new("silver_gap");
    public static readonly Cause UnreadWorkers = new("workers");
    public static readonly Cause UnreadTraderCash = new("trader_cash");
    public static readonly Cause UnreadBuyers = new("buyers");
    public static readonly Cause UnreadSurplus = new("surplus");
    public static readonly Cause UnreadRunways = new("runways");
    public static readonly Cause UnreadSupply = new("supply");
    public static readonly Cause UnreadSources = new("sources");

    /// <summary>
    /// Every Cause, in the order a view lists them: the refusal and blocked kinds, the wait kinds, the
    /// admission reasons, then the unread facts.
    /// </summary>
    public static readonly IReadOnlyList<Cause> All =
    [
        CollapsePending, NoWorker, AwaitingPlan, FieldUnavailable, NoSpace, SharedAdmission,
        RetriesSpent, RockNotDug, SiteBlocked, NativeIneligible, ReconcileWrite, Cooldown,
        NoMethod, HeldUnavailable, HeldOptIn, HeldNoSource,
        MethodUsed, ExistingWork, BunksOpen, BreachHeld, ComfortUse, Facility, HospitalConvert,
        SleepingUse, Separation, Dialog, Claim, FacilityAccess, RoomTemperature, RetryBudgetWait,
        CombatOrders, HoldFallback,
        NotReady, AlreadyReserved, UnknownFacts, StaleFacts, UnsafePlacement, UnsafeThreat,
        CriticalMedical, MaterialRequired, DependencyBlocked, GeometryBlocked, InvalidHeld,
        ExcavationUnsupported, ExcavationGeometryChanged, UnsafeRoute, RoofSupportRisk, StorageMissing,
        UrgentCompetingWork,
        UnreadReview, UnreadStock, UnreadBenches, UnreadBenchDef, UnreadBills, UnreadRecipes,
        UnreadDefinitions, UnreadNativeRead, UnreadMedicine, UnreadArtists, UnreadArtNeed,
        UnreadGearRecover, UnreadGearNeeds, UnreadGearDemand, UnreadArmoryTier, UnreadWeapons,
        UnreadShells, UnreadFoodFacts, UnreadFoodPlan, UnreadFoodStorage, UnreadBabyFeeding,
        UnreadMechs, UnreadWort, UnreadQuestOffers, UnreadQuestAsker, UnreadSilverGap,
        UnreadWorkers, UnreadTraderCash, UnreadBuyers, UnreadSurplus, UnreadRunways,
        UnreadSupply, UnreadSources,
    ];

    /// <summary>The causes an OutcomeWaiting verdict may carry: the goal waits on something that is not a failure.</summary>
    public static readonly IReadOnlyList<Cause> WaitCauses =
    [
        MethodUsed, ExistingWork, BunksOpen, BreachHeld, ComfortUse, Facility, HospitalConvert,
        SleepingUse, Separation, Dialog, Claim, FacilityAccess, RoomTemperature, RetryBudgetWait,
    ];

    /// <summary>A cause that is a wait rather than a refusal: a wait kind or one of the two fight outcomes.</summary>
    public bool Waiting() => this == CombatOrders || this == HoldFallback || WaitCauses.Contains(this);

    /// <summary>Throws for a Cause outside the closed set.</summary>
    public void Validate() => DomainException.Require(All.Contains(this), $"unknown cause \"{Value}\"");
}

public static partial class PolicyFunctions
{
    /// <summary>The Cause of an admission Reason. Reason and its held map are untouched; a test fails when a declared Reason is not a Cause.</summary>
    public static Cause CauseOfReason(Reason r) => new(r.Value);

    private static readonly BlockedReason[] _fixedBlocked =
    [
        BlockedReason.NoWorker,
        BlockedReason.NativeIneligible,
        BlockedReason.Reconciling,
        BlockedReason.Cooldown,
        BlockedReason.NoMethod,
        BlockedReason.HeldUnavailable,
        BlockedReason.HeldOptIn,
        BlockedReason.HeldNoSource,
    ];

    /// <summary>The Cause of a fixed BlockedReason; null for the prerequisite form.</summary>
    public static Cause? CauseOfBlocked(BlockedReason r) => _fixedBlocked.Contains(r) ? new Cause(r.Value) : null;

    /// <summary>Bounds the subject Wording folds into a sentence.</summary>
    public const int MaxSubjectLen = 40;

    /// <summary>
    /// The plain-language sentence for a cause, naming <paramref name="subject"/> when one is given. The
    /// subject is cut to MaxSubjectLen and reduced to printable ASCII.
    /// </summary>
    public static string Wording(Cause c, string subject)
    {
        var baseText = _causeWording.GetValueOrDefault(c, "The bot is held up for a reason it cannot name.");
        subject = BoundSubject(subject);
        if (subject.Length == 0)
        {
            return baseText;
        }

        return baseText.TrimEnd('.') + " (" + subject + ").";
    }

    /// <summary>Cuts a subject to MaxSubjectLen printable ASCII characters, the form a progress record persists and Wording folds in.</summary>
    public static string BoundSubject(string s)
    {
        var b = new System.Text.StringBuilder();
        foreach (var r in s)
        {
            if (b.Length >= MaxSubjectLen)
            {
                break;
            }

            if (r >= ' ' && r <= '~')
            {
                b.Append(r);
            }
        }

        return b.ToString().Trim();
    }

    private static string UnreadWording(string label) => "The bot could not read " + label + ".";

    private static readonly Dictionary<Cause, string> _causeWording = new()
    {
        [Cause.CollapsePending] = "A roof or tunnel collapse has to clear first.",
        [Cause.NoWorker] = "No colonist is able to do this work.",
        [Cause.AwaitingPlan] = "Another plan has to stand first.",
        [Cause.FieldUnavailable] = "A fact the bot needs is not known yet.",
        [Cause.NoSpace] = "There is no verified place to put it.",
        [Cause.SharedAdmission] = "The shared safety check turned the plan down.",
        [Cause.RetriesSpent] = "The game refused this for good, so the bot stopped retrying.",
        [Cause.RockNotDug] = "Rock still stands after the dig finished.",
        [Cause.SiteBlocked] = "The work site cannot be used.",
        [Cause.NativeIneligible] = "The game refuses the order.",
        [Cause.ReconcileWrite] = "A write's outcome is unknown, so the bot is checking before it retries.",
        [Cause.Cooldown] = "Every other way of doing this is cooling down.",
        [Cause.NoMethod] = "No way of doing this is open right now.",
        [Cause.HeldUnavailable] = "Held on purpose because the work is not available.",
        [Cause.HeldOptIn] = "Held on purpose until it is switched on.",
        [Cause.HeldNoSource] = "Held on purpose because the map has no source of the resource.",

        [Cause.MethodUsed] = "The bot already did its part and is waiting for the work to finish.",
        [Cause.ExistingWork] = "Earlier work is still standing and the goal moves with it.",
        [Cause.BunksOpen] = "The shelter's furnishing waits on its open bunks.",
        [Cause.BreachHeld] = "The shrine plan holds while every target shrine holds.",
        [Cause.ComfortUse] = "The colonists have to use the comfort first.",
        [Cause.Facility] = "An existing facility needs a bill or upkeep, not a build.",
        [Cause.HospitalConvert] = "A bed is being converted to a hospital bed.",
        [Cause.SleepingUse] = "The beds provided have to be used before more are built.",
        [Cause.Separation] = "A separate butcher spot is still being built.",
        [Cause.Dialog] = "A choice dialog is not ready to answer yet.",
        [Cause.Claim] = "A higher-priority plan holds something this one needs.",
        [Cause.FacilityAccess] = "A facility stands but some colonists cannot reach it.",
        [Cause.RoomTemperature] = "A temperature has to settle first: a room's, or the weather outdoors.",
        [Cause.RetryBudgetWait] = "The game refused this for now and nothing has changed since.",
        [Cause.CombatOrders] = "Combat orders are running.",
        [Cause.HoldFallback] = "The hold line fell back to squad defense.",

        [Cause.NotReady] = "The action is not ready yet.",
        [Cause.AlreadyReserved] = "Something else already reserved this.",
        [Cause.UnknownFacts] = "The bot does not know enough yet.",
        [Cause.StaleFacts] = "The bot's information is out of date.",
        [Cause.UnsafePlacement] = "The spot is not safe.",
        [Cause.UnsafeThreat] = "A threat makes this unsafe right now.",
        [Cause.CriticalMedical] = "A medical emergency comes first.",
        [Cause.MaterialRequired] = "It needs a material that was not chosen.",
        [Cause.DependencyBlocked] = "It waits on an earlier step that is not done.",
        [Cause.GeometryBlocked] = "It would overlap other planned work.",
        [Cause.InvalidHeld] = "A held reservation could not be verified.",
        [Cause.ExcavationUnsupported] = "This kind of digging is not supported.",
        [Cause.ExcavationGeometryChanged] = "The ground to dig changed since it was planned.",
        [Cause.UnsafeRoute] = "The route there is not safe.",
        [Cause.RoofSupportRisk] = "Digging here could weaken the roof.",
        [Cause.StorageMissing] = "There is nowhere to store it.",
        [Cause.UrgentCompetingWork] = "Urgent work is taking the colonists.",

        [Cause.UnreadReview] = UnreadWording("the Round's review"),
        [Cause.UnreadStock] = UnreadWording("the stock census"),
        [Cause.UnreadBenches] = UnreadWording("the bench readback"),
        [Cause.UnreadBenchDef] = UnreadWording("a bench's kind"),
        [Cause.UnreadBills] = UnreadWording("a bench's bills or recipes"),
        [Cause.UnreadRecipes] = UnreadWording("a recipe's facts"),
        [Cause.UnreadDefinitions] = UnreadWording("the game's definitions"),
        [Cause.UnreadNativeRead] = UnreadWording("something the game could not serve"),
        [Cause.UnreadMedicine] = UnreadWording("the medicine catalog"),
        [Cause.UnreadArtists] = UnreadWording("who the artists are"),
        [Cause.UnreadArtNeed] = UnreadWording("whether art is wanted"),
        [Cause.UnreadGearRecover] = UnreadWording("whether colonists have recovered"),
        [Cause.UnreadGearNeeds] = UnreadWording("what gear colonists need"),
        [Cause.UnreadGearDemand] = UnreadWording("the gear census"),
        [Cause.UnreadArmoryTier] = UnreadWording("the armory tier"),
        [Cause.UnreadWeapons] = UnreadWording("the weapon demand"),
        [Cause.UnreadShells] = UnreadWording("the mortar shell targets"),
        [Cause.UnreadFoodFacts] = UnreadWording("the food facts"),
        [Cause.UnreadFoodPlan] = UnreadWording("the food plan"),
        [Cause.UnreadFoodStorage] = UnreadWording("food storage"),
        [Cause.UnreadBabyFeeding] = UnreadWording("which babies need feeding"),
        [Cause.UnreadMechs] = UnreadWording("the mech gestation"),
        [Cause.UnreadWort] = UnreadWording("which wort makes beer"),
        [Cause.UnreadQuestOffers] = UnreadWording("the quest offers"),
        [Cause.UnreadQuestAsker] = UnreadWording("who a quest's asker is"),
        [Cause.UnreadSilverGap] = UnreadWording("the silver gap"),
        [Cause.UnreadWorkers] = UnreadWording("the workers' skills"),
        [Cause.UnreadTraderCash] = UnreadWording("what traders can pay"),
        [Cause.UnreadBuyers] = UnreadWording("which traders buy"),
        [Cause.UnreadSurplus] = UnreadWording("the gear surplus"),
        [Cause.UnreadRunways] = UnreadWording("the ingredient runways"),
        [Cause.UnreadSupply] = UnreadWording("the ingredient supply"),
        [Cause.UnreadSources] = UnreadWording("where ingredients come from"),
    };
}
