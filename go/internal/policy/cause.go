package policy

import (
	"fmt"
	"slices"
	"strings"
)

// Cause is the closed set of reasons the bot explains itself with: why a
// concern did not advance, what it waits on, which input it could not read.
// Wire values are the strings the sources already used (refusal and wait
// kinds, the unread-input names, BlockedReason, admission Reason); a string two sources
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
	// CauseCombatOrders and CauseHoldFallback are the fight outcomes, waits in
	// all but name: nothing failed. They are not wait kinds a Verdict may carry.
	CauseCombatOrders Cause = "combat_orders"
	CauseHoldFallback Cause = "hold_fallback"
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

// Unread facts: the one input a planner did not read when it abstained from
// declaring. Every abstain site picks one, so the ledger view can say which
// input was missing instead of only who abstained.
const (
	CauseUnreadReview      Cause = "review"
	CauseUnreadStock       Cause = "stock"
	CauseUnreadBenches     Cause = "benches"
	CauseUnreadBenchDef    Cause = "bench_definition"
	CauseUnreadBills       Cause = "bills"
	CauseUnreadRecipes     Cause = "recipes"
	CauseUnreadDefinitions Cause = "definitions"
	CauseUnreadNativeRead  Cause = "native_source"
	CauseUnreadMedicine    Cause = "medicine"
	CauseUnreadArtists     Cause = "artists"
	CauseUnreadArtNeed     Cause = "art_need"
	CauseUnreadGearRecover Cause = "gear_recovery"
	CauseUnreadGearNeeds   Cause = "gear_replacements"
	CauseUnreadGearDemand  Cause = "gear_demand"
	CauseUnreadArmoryTier  Cause = "armory_tier"
	CauseUnreadWeapons     Cause = "weapon_demand"
	CauseUnreadShells      Cause = "shell_targets"
	CauseUnreadFoodFacts   Cause = "food_facts"
	CauseUnreadFoodPlan    Cause = "food_plan"
	CauseUnreadFoodStorage Cause = "food_storage"
	CauseUnreadBabyFeeding Cause = "baby_feeding"
	CauseUnreadMechs       Cause = "mech_gestation"
	CauseUnreadWort        Cause = "wort"
	CauseUnreadQuestOffers Cause = "quest_offers"
	CauseUnreadQuestAsker  Cause = "quest_asker"
	CauseUnreadSilverGap   Cause = "silver_gap"
	CauseUnreadWorkers     Cause = "workers"
	CauseUnreadTraderCash  Cause = "trader_cash"
	CauseUnreadBuyers      Cause = "buyers"
	CauseUnreadSurplus     Cause = "surplus"
	CauseUnreadRunways     Cause = "runways"
	CauseUnreadSupply      Cause = "supply"
	CauseUnreadSources     Cause = "sources"
)

// Causes is every Cause, in the order a view lists them: the refusal and
// blocked kinds, the wait kinds, the admission reasons, then the unread facts.
var Causes = []Cause{
	CauseCollapsePending, CauseNoWorker, CauseAwaitingPlan, CauseFieldUnavailable, CauseNoSpace, CauseSharedAdmission,
	CauseRetriesSpent, CauseRockNotDug, CauseSiteBlocked, CauseNativeIneligible, CauseReconcileWrite, CauseCooldown,
	CauseNoMethod, CauseHeldUnavailable, CauseHeldOptIn,
	CauseMethodUsed, CauseExistingWork, CauseBunksOpen, CauseBreachHeld, CauseComfortUse, CauseFacility, CauseHospitalConvert,
	CauseSleepingUse, CauseSeparation, CauseDialog, CauseClaim, CauseFacilityAccess, CauseRoomTemperature, CauseRetryBudgetWait,
	CauseCombatOrders, CauseHoldFallback,
	CauseNotReady, CauseAlreadyReserved, CauseUnknownFacts, CauseStaleFacts, CauseUnsafePlacement, CauseUnsafeThreat,
	CauseCriticalMedical, CauseMaterialRequired, CauseDependencyBlocked, CauseGeometryBlocked, CauseInvalidHeld,
	CauseExcavationUnsupported, CauseExcavationGeometryChanged, CauseUnsafeRoute, CauseRoofSupportRisk, CauseStorageMissing,
	CauseUrgentCompetingWork,
	CauseUnreadReview, CauseUnreadStock, CauseUnreadBenches, CauseUnreadBenchDef, CauseUnreadBills, CauseUnreadRecipes,
	CauseUnreadDefinitions, CauseUnreadNativeRead, CauseUnreadMedicine, CauseUnreadArtists, CauseUnreadArtNeed,
	CauseUnreadGearRecover, CauseUnreadGearNeeds, CauseUnreadGearDemand, CauseUnreadArmoryTier, CauseUnreadWeapons,
	CauseUnreadShells, CauseUnreadFoodFacts, CauseUnreadFoodPlan, CauseUnreadFoodStorage, CauseUnreadBabyFeeding,
	CauseUnreadMechs, CauseUnreadWort, CauseUnreadQuestOffers, CauseUnreadQuestAsker, CauseUnreadSilverGap,
	CauseUnreadWorkers, CauseUnreadTraderCash, CauseUnreadBuyers, CauseUnreadSurplus, CauseUnreadRunways,
	CauseUnreadSupply, CauseUnreadSources,
}

// CauseOfReason is the Cause of an admission Reason. Reason and its HeldReason
// map are untouched; a test fails when a declared Reason is not a Cause.
func CauseOfReason(r Reason) Cause { return Cause(r) }

var fixedBlocked = []BlockedReason{BlockedNoWorker, BlockedNativeIneligible, BlockedReconciling, BlockedCooldown, BlockedNoMethod, HeldUnavailable, HeldOptIn}

// CauseOfBlocked is the Cause of a fixed BlockedReason; ok is false for the
// prerequisite form.
func CauseOfBlocked(r BlockedReason) (Cause, bool) {
	if !slices.Contains(fixedBlocked, r) {
		return "", false
	}
	return Cause(r), true
}

// WaitCauses are the causes an OutcomeWaiting verdict may carry: the goal
// waits on something that is not a failure.
var WaitCauses = []Cause{
	CauseMethodUsed, CauseExistingWork, CauseBunksOpen, CauseBreachHeld, CauseComfortUse, CauseFacility, CauseHospitalConvert,
	CauseSleepingUse, CauseSeparation, CauseDialog, CauseClaim, CauseFacilityAccess, CauseRoomTemperature, CauseRetryBudgetWait,
}

// Waiting reports a cause that is a wait rather than a refusal: a wait kind or
// one of the two fight outcomes.
func (c Cause) Waiting() bool {
	return c == CauseCombatOrders || c == CauseHoldFallback || slices.Contains(WaitCauses, c)
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
	subject = BoundSubject(subject)
	if subject == "" {
		return base
	}
	return strings.TrimSuffix(base, ".") + " (" + subject + ")."
}

// BoundSubject cuts a subject to MaxSubjectLen printable ASCII characters,
// the form a progress record persists and Wording folds in.
func BoundSubject(s string) string {
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
	CauseCombatOrders:    "Combat orders are running.",
	CauseHoldFallback:    "The hold line fell back to squad defense.",

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

	CauseUnreadReview:      unreadWording("the Round's review"),
	CauseUnreadStock:       unreadWording("the stock census"),
	CauseUnreadBenches:     unreadWording("the bench readback"),
	CauseUnreadBenchDef:    unreadWording("a bench's kind"),
	CauseUnreadBills:       unreadWording("a bench's bills or recipes"),
	CauseUnreadRecipes:     unreadWording("a recipe's facts"),
	CauseUnreadDefinitions: unreadWording("the game's definitions"),
	CauseUnreadNativeRead:  unreadWording("something the game could not serve"),
	CauseUnreadMedicine:    unreadWording("the medicine catalog"),
	CauseUnreadArtists:     unreadWording("who the artists are"),
	CauseUnreadArtNeed:     unreadWording("whether art is wanted"),
	CauseUnreadGearRecover: unreadWording("whether colonists have recovered"),
	CauseUnreadGearNeeds:   unreadWording("what gear colonists need"),
	CauseUnreadGearDemand:  unreadWording("the gear census"),
	CauseUnreadArmoryTier:  unreadWording("the armory tier"),
	CauseUnreadWeapons:     unreadWording("the weapon demand"),
	CauseUnreadShells:      unreadWording("the mortar shell targets"),
	CauseUnreadFoodFacts:   unreadWording("the food facts"),
	CauseUnreadFoodPlan:    unreadWording("the food plan"),
	CauseUnreadFoodStorage: unreadWording("food storage"),
	CauseUnreadBabyFeeding: unreadWording("which babies need feeding"),
	CauseUnreadMechs:       unreadWording("the mech gestation"),
	CauseUnreadWort:        unreadWording("which wort makes beer"),
	CauseUnreadQuestOffers: unreadWording("the quest offers"),
	CauseUnreadQuestAsker:  unreadWording("who a quest's asker is"),
	CauseUnreadSilverGap:   unreadWording("the silver gap"),
	CauseUnreadWorkers:     unreadWording("the workers' skills"),
	CauseUnreadTraderCash:  unreadWording("what traders can pay"),
	CauseUnreadBuyers:      unreadWording("which traders buy"),
	CauseUnreadSurplus:     unreadWording("the gear surplus"),
	CauseUnreadRunways:     unreadWording("the ingredient runways"),
	CauseUnreadSupply:      unreadWording("the ingredient supply"),
	CauseUnreadSources:     unreadWording("where ingredients come from"),
}
