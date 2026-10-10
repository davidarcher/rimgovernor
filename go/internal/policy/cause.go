package policy

import (
	"fmt"
	"slices"
	"strings"
)

// Cause is the closed set of reasons the bot explains itself with: why a
// concern did not advance, what it waits on, which input it could not read.
// Wire values are the strings the sources already used (refusal and wait
// kinds, UnreadFact, BlockedReason, admission Reason); a string two sources
// share (no_worker, native_ineligible) is one Cause. Wording is the only place
// English for a Cause lives.
type Cause string

// Refusal kinds and the fixed blocked reasons.
const (
	CauseCollapsePending  Cause = "collapse_pending"
	CauseNoWorker         Cause = "no_worker"
	CauseAwaitingPlan     Cause = "awaiting_plan"
	CauseFieldUnavailable Cause = "field_unavailable"
	CauseNoSpace          Cause = "no_space"
	CauseSharedAdmission  Cause = "shared_admission_refused"
	CauseRetriesSpent     Cause = "retry_budget_spent"
	CauseRockNotDug       Cause = "rock_not_dug"
	CauseSiteBlocked      Cause = "site_blocked"
	CauseNativeIneligible Cause = "native_ineligible"
	CauseReconcileWrite   Cause = "reconcile_write"
	CauseCooldown         Cause = "cooldown"
	CauseNoMethod         Cause = "no_method"
	CauseHeldUnavailable  Cause = "held:unavailable"
	CauseHeldOptIn        Cause = "held:opt-in"
)

// Wait kinds.
const (
	CauseMethodUsed      Cause = "method_already_used"
	CauseExistingWork    Cause = "already_working_on_it"
	CauseBunksOpen       Cause = "shelter_bunks_open"
	CauseBreachHeld      Cause = "breach_held"
	CauseComfortUse      Cause = "waiting_for_native_comfort_use"
	CauseFacility        Cause = "existing_facility_needs_bill_or_upkeep"
	CauseHospitalConvert Cause = "hospital_bed_convert_pending"
	CauseSleepingUse     Cause = "sleeping_use_needed"
	CauseSeparation      Cause = "butcher_separation_pending"
	CauseDialog          Cause = "dialog_not_interactive"
	CauseClaim           Cause = "waiting_on_claim"
	CauseFacilityAccess  Cause = "existing_facility_access_blocked"
	CauseRoomTemperature Cause = "waiting_for_native_temperature"
	CauseRetryBudgetWait Cause = "retry_budget_waiting"
)

// Admission reasons that are not already a Cause above.
const (
	CauseNotReady                  Cause = "not_ready"
	CauseAlreadyReserved           Cause = "already_reserved"
	CauseUnknownFacts              Cause = "unknown_facts"
	CauseStaleFacts                Cause = "stale_facts"
	CauseUnsafePlacement           Cause = "unsafe_placement"
	CauseUnsafeThreat              Cause = "unsafe_threat"
	CauseCriticalMedical           Cause = "critical_medical"
	CauseMaterialRequired          Cause = "explicit_material_required"
	CauseDependencyBlocked         Cause = "dependency_incomplete"
	CauseGeometryBlocked           Cause = "geometry_conflict"
	CauseInvalidHeld               Cause = "held_reservation_unverifiable"
	CauseExcavationUnsupported     Cause = "excavation_unsupported"
	CauseExcavationGeometryChanged Cause = "excavation_geometry_changed"
	CauseUnsafeRoute               Cause = "unsafe_route"
	CauseRoofSupportRisk           Cause = "roof_support_risk"
	CauseStorageMissing            Cause = "missing_storage"
	CauseUrgentCompetingWork       Cause = "urgent_competing_work"
)

// Causes is every Cause, in the order a view lists them: the refusal and
// blocked kinds, the wait kinds, the admission reasons, then the unread facts.
var Causes = slices.Concat([]Cause{
	CauseCollapsePending, CauseNoWorker, CauseAwaitingPlan, CauseFieldUnavailable, CauseNoSpace, CauseSharedAdmission,
	CauseRetriesSpent, CauseRockNotDug, CauseSiteBlocked, CauseNativeIneligible, CauseReconcileWrite, CauseCooldown,
	CauseNoMethod, CauseHeldUnavailable, CauseHeldOptIn,
	CauseMethodUsed, CauseExistingWork, CauseBunksOpen, CauseBreachHeld, CauseComfortUse, CauseFacility, CauseHospitalConvert,
	CauseSleepingUse, CauseSeparation, CauseDialog, CauseClaim, CauseFacilityAccess, CauseRoomTemperature, CauseRetryBudgetWait,
	CauseNotReady, CauseAlreadyReserved, CauseUnknownFacts, CauseStaleFacts, CauseUnsafePlacement, CauseUnsafeThreat,
	CauseCriticalMedical, CauseMaterialRequired, CauseDependencyBlocked, CauseGeometryBlocked, CauseInvalidHeld,
	CauseExcavationUnsupported, CauseExcavationGeometryChanged, CauseUnsafeRoute, CauseRoofSupportRisk, CauseStorageMissing,
	CauseUrgentCompetingWork,
}, unreadCauses())

func unreadCauses() []Cause {
	out := make([]Cause, len(UnreadFacts))
	for i, f := range UnreadFacts {
		out[i] = CauseOfUnread(f)
	}
	return out
}

// CauseOfReason is the Cause of an admission Reason. Reason and its HeldReason
// map are untouched; a test fails when a declared Reason is not a Cause.
func CauseOfReason(r Reason) Cause { return Cause(r) }

// CauseOfUnread is the Cause of an input a planner did not read.
func CauseOfUnread(f UnreadFact) Cause { return Cause(f) }

var fixedBlocked = []BlockedReason{BlockedNoWorker, BlockedNativeIneligible, BlockedReconciling, BlockedCooldown, BlockedNoMethod, HeldUnavailable, HeldOptIn}

// CauseOfBlocked is the Cause of a fixed BlockedReason; ok is false for the
// composed forms (planner:, waiting:, prerequisite:).
func CauseOfBlocked(r BlockedReason) (Cause, bool) {
	if !slices.Contains(fixedBlocked, r) {
		return "", false
	}
	return Cause(r), true
}

// Validate reports a Cause outside the closed set.
func (c Cause) Validate() error {
	if !slices.Contains(Causes, c) {
		return fmt.Errorf("unknown cause %q", string(c))
	}
	return nil
}

// MaxSubjectLen bounds the subject Wording folds into a sentence.
const MaxSubjectLen = 40

// Wording is the plain-language sentence for a cause, naming subject when one
// is given. The subject is cut to MaxSubjectLen and reduced to printable ASCII.
func Wording(c Cause, subject string) string {
	base, ok := causeWording[c]
	if !ok {
		base = "The bot is held up for a reason it cannot name."
	}
	subject = boundedSubject(subject)
	if subject == "" {
		return base
	}
	return strings.TrimSuffix(base, ".") + " (" + subject + ")."
}

func boundedSubject(s string) string {
	var b strings.Builder
	for _, r := range s {
		if b.Len() >= MaxSubjectLen {
			break
		}
		if r >= ' ' && r <= '~' {
			b.WriteRune(r)
		}
	}
	return strings.TrimSpace(b.String())
}

func unreadWording(label string) string { return "The bot could not read " + label + "." }

var causeWording = map[Cause]string{
	CauseCollapsePending:  "A roof or tunnel collapse has to clear first.",
	CauseNoWorker:         "No colonist is able to do this work.",
	CauseAwaitingPlan:     "Another plan has to stand first.",
	CauseFieldUnavailable: "A fact the bot needs is not known yet.",
	CauseNoSpace:          "There is no verified place to put it.",
	CauseSharedAdmission:  "The shared safety check turned the plan down.",
	CauseRetriesSpent:     "The game refused this for good, so the bot stopped retrying.",
	CauseRockNotDug:       "Rock still stands after the dig finished.",
	CauseSiteBlocked:      "The work site cannot be used.",
	CauseNativeIneligible: "The game refuses the order.",
	CauseReconcileWrite:   "A write's outcome is unknown, so the bot is checking before it retries.",
	CauseCooldown:         "Every other way of doing this is cooling down.",
	CauseNoMethod:         "No way of doing this is open right now.",
	CauseHeldUnavailable:  "Held on purpose because the work is not available.",
	CauseHeldOptIn:        "Held on purpose until it is switched on.",

	CauseMethodUsed:      "The bot already did its part and is waiting for the work to finish.",
	CauseExistingWork:    "Earlier work is still standing and the goal moves with it.",
	CauseBunksOpen:       "The shelter's furnishing waits on its open bunks.",
	CauseBreachHeld:      "The shrine plan holds while every target shrine holds.",
	CauseComfortUse:      "The colonists have to use the comfort first.",
	CauseFacility:        "An existing facility needs a bill or upkeep, not a build.",
	CauseHospitalConvert: "A bed is being converted to a hospital bed.",
	CauseSleepingUse:     "The beds provided have to be used before more are built.",
	CauseSeparation:      "A separate butcher spot is still being built.",
	CauseDialog:          "A choice dialog is not ready to answer yet.",
	CauseClaim:           "A higher-priority plan holds something this one needs.",
	CauseFacilityAccess:  "A facility stands but some colonists cannot reach it.",
	CauseRoomTemperature: "The room's temperature has to settle first.",
	CauseRetryBudgetWait: "The game refused this for now and nothing has changed since.",

	CauseNotReady:                  "The action is not ready yet.",
	CauseAlreadyReserved:           "Something else already reserved this.",
	CauseUnknownFacts:              "The bot does not know enough yet.",
	CauseStaleFacts:                "The bot's information is out of date.",
	CauseUnsafePlacement:           "The spot is not safe.",
	CauseUnsafeThreat:              "A threat makes this unsafe right now.",
	CauseCriticalMedical:           "A medical emergency comes first.",
	CauseMaterialRequired:          "It needs a material that was not chosen.",
	CauseDependencyBlocked:         "It waits on an earlier step that is not done.",
	CauseGeometryBlocked:           "It would overlap other planned work.",
	CauseInvalidHeld:               "A held reservation could not be verified.",
	CauseExcavationUnsupported:     "This kind of digging is not supported.",
	CauseExcavationGeometryChanged: "The ground to dig changed since it was planned.",
	CauseUnsafeRoute:               "The route there is not safe.",
	CauseRoofSupportRisk:           "Digging here could weaken the roof.",
	CauseStorageMissing:            "There is nowhere to store it.",
	CauseUrgentCompetingWork:       "Urgent work is taking the colonists.",

	CauseOfUnread(UnreadReview):      unreadWording("the Round's review"),
	CauseOfUnread(UnreadStock):       unreadWording("the stock census"),
	CauseOfUnread(UnreadBenches):     unreadWording("the bench readback"),
	CauseOfUnread(UnreadBenchDef):    unreadWording("a bench's kind"),
	CauseOfUnread(UnreadBills):       unreadWording("a bench's bills or recipes"),
	CauseOfUnread(UnreadRecipes):     unreadWording("a recipe's facts"),
	CauseOfUnread(UnreadDefinitions): unreadWording("the game's definitions"),
	CauseOfUnread(UnreadNativeRead):  unreadWording("something the game could not serve"),
	CauseOfUnread(UnreadMedicine):    unreadWording("the medicine catalog"),
	CauseOfUnread(UnreadArtists):     unreadWording("who the artists are"),
	CauseOfUnread(UnreadArtNeed):     unreadWording("whether art is wanted"),
	CauseOfUnread(UnreadGearRecover): unreadWording("whether colonists have recovered"),
	CauseOfUnread(UnreadGearNeeds):   unreadWording("what gear colonists need"),
	CauseOfUnread(UnreadGearDemand):  unreadWording("the gear census"),
	CauseOfUnread(UnreadArmoryTier):  unreadWording("the armory tier"),
	CauseOfUnread(UnreadWeapons):     unreadWording("the weapon demand"),
	CauseOfUnread(UnreadShells):      unreadWording("the mortar shell targets"),
	CauseOfUnread(UnreadFoodFacts):   unreadWording("the food facts"),
	CauseOfUnread(UnreadFoodPlan):    unreadWording("the food plan"),
	CauseOfUnread(UnreadFoodStorage): unreadWording("food storage"),
	CauseOfUnread(UnreadBabyFeeding): unreadWording("which babies need feeding"),
	CauseOfUnread(UnreadMechs):       unreadWording("the mech gestation"),
	CauseOfUnread(UnreadWort):        unreadWording("which wort makes beer"),
	CauseOfUnread(UnreadQuestOffers): unreadWording("the quest offers"),
	CauseOfUnread(UnreadQuestAsker):  unreadWording("who a quest's asker is"),
	CauseOfUnread(UnreadSilverGap):   unreadWording("the silver gap"),
	CauseOfUnread(UnreadWorkers):     unreadWording("the workers' skills"),
	CauseOfUnread(UnreadTraderCash):  unreadWording("what traders can pay"),
	CauseOfUnread(UnreadBuyers):      unreadWording("which traders buy"),
	CauseOfUnread(UnreadSurplus):     unreadWording("the gear surplus"),
	CauseOfUnread(UnreadRunways):     unreadWording("the ingredient runways"),
	CauseOfUnread(UnreadSupply):      unreadWording("the ingredient supply"),
	CauseOfUnread(UnreadSources):     unreadWording("where ingredients come from"),
}
