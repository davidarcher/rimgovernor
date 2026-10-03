package buildingruntime

import (
	"fmt"
	"strings"
)

// Outcome is what a planner's step came to: the closed set every routine
// result reports.
type Outcome string

const (
	// OutcomeAdmitted: the step admitted a plan.
	OutcomeAdmitted Outcome = "admitted"
	// OutcomeNothingToDo: no deficit, or the planner is switched off.
	OutcomeNothingToDo Outcome = "nothing_to_do"
	// OutcomeInProgress: earlier work stands and the goal waits on it.
	OutcomeInProgress Outcome = "in_progress"
	// OutcomeRefused: the step could not proceed; the Refusal says why.
	OutcomeRefused Outcome = "refused"
)

// RefusalKind is the closed set of reasons a step refuses. A refusal always
// has one; there is no catch-all.
type RefusalKind string

const (
	// RefusalCollapsePending: a roof or tunnel collapse must clear first.
	RefusalCollapsePending RefusalKind = "collapse_pending"
	// RefusalNoWorker: no colonist or squad can do the work.
	RefusalNoWorker RefusalKind = "no_worker"
	// RefusalAwaitingPlan: another plan (food, research, a shell) must stand
	// first; the subject names it.
	RefusalAwaitingPlan RefusalKind = "awaiting_plan"
	// RefusalFieldUnavailable: a fact the step reads is unknown; the subject
	// names the field.
	RefusalFieldUnavailable RefusalKind = "field_unavailable"
	// RefusalNoSpace: no verified placement exists.
	RefusalNoSpace RefusalKind = "no_space"
	// RefusalSharedAdmission: the shared admission path turned the plan down.
	RefusalSharedAdmission RefusalKind = "shared_admission_refused"
)

var refusalKinds = []RefusalKind{RefusalCollapsePending, RefusalNoWorker, RefusalAwaitingPlan, RefusalFieldUnavailable, RefusalNoSpace, RefusalSharedAdmission}

// Refusal says why an OutcomeRefused step stopped: a kind, the thing it
// concerns and optional detail.
type Refusal struct {
	Kind    RefusalKind
	Subject string
	Detail  string
}

// Cause names which standing situation an OutcomeNothingToDo or
// OutcomeInProgress verdict reports; it keeps those verdicts distinguishable
// for the routines that branch on them. Admitted and refused verdicts carry
// none.
type Cause string

// Verdict is the pair every routine result embeds: its Outcome and, when
// refused, the Refusal. The zero Verdict means the step reached no verdict
// (the caller keeps going).
type Verdict struct {
	Outcome Outcome
	Cause   Cause
	Refusal Refusal
}

// refuse is a refused Verdict. A kind outside the closed set is a
// programming error and panics.
func refuse(kind RefusalKind, subject, detail string) Verdict {
	v := Verdict{Outcome: OutcomeRefused, Refusal: Refusal{Kind: kind, Subject: subject, Detail: detail}}
	if err := v.Validate(); err != nil {
		panic(err)
	}
	return v
}

func fieldUnavailable(field string) Verdict { return refuse(RefusalFieldUnavailable, field, "") }
func noSpace(subject string) Verdict        { return refuse(RefusalNoSpace, subject, "") }
func noWorker(subject string) Verdict       { return refuse(RefusalNoWorker, subject, "") }
func collapsePending(subject string) Verdict {
	return refuse(RefusalCollapsePending, subject, "")
}
func awaitingPlan(subject, detail string) Verdict {
	return refuse(RefusalAwaitingPlan, subject, detail)
}

// awaitingMethod is the refusal for a policy method that names what the
// goal waits on (a power or temperature wait, a cooler's power).
func awaitingMethod[M ~string](method M) Verdict { return awaitingPlan(string(method), "") }

// researchWait is the refusal of a method that needs a native research
// project the census has not finished; EnsureResearch's roadmap is what gets
// there (#230).
func researchWait(project string) Verdict { return awaitingPlan("research", project) }

// Validate reports a verdict that is not one of the closed forms: an unknown
// outcome, a refusal without a kind from the closed set, or a refusal or
// cause on an outcome that carries none.
func (v Verdict) Validate() error {
	switch v.Outcome {
	case "":
		if v != (Verdict{}) {
			return fmt.Errorf("verdict without an outcome: %+v", v)
		}
	case OutcomeAdmitted:
		if v.Cause != "" || v.Refusal != (Refusal{}) {
			return fmt.Errorf("admitted verdict carries %+v", v)
		}
	case OutcomeNothingToDo, OutcomeInProgress:
		if v.Refusal != (Refusal{}) {
			return fmt.Errorf("%s verdict carries a refusal: %+v", v.Outcome, v.Refusal)
		}
	case OutcomeRefused:
		if v.Cause != "" {
			return fmt.Errorf("refused verdict carries a cause: %+v", v)
		}
		known := false
		for _, kind := range refusalKinds {
			known = known || v.Refusal.Kind == kind
		}
		if !known {
			return fmt.Errorf("refusal without a known kind: %+v", v.Refusal)
		}
	default:
		return fmt.Errorf("unknown outcome %q", v.Outcome)
	}
	return nil
}

// IsZero reports a step that reached no verdict.
func (v Verdict) IsZero() bool { return v == Verdict{} }

// Is reports a refusal of kind.
func (v Verdict) Is(kind RefusalKind) bool {
	return v.Outcome == OutcomeRefused && v.Refusal.Kind == kind
}

// String is the one rendering of a verdict, for the service log, the dashboard
// timeline and the status strip: a refusal reads "kind", "kind:subject" or
// "kind:subject:detail"; any other verdict reads its cause, or its outcome.
func (v Verdict) String() string {
	switch {
	case v.Outcome == OutcomeRefused:
		parts := []string{string(v.Refusal.Kind)}
		if v.Refusal.Subject != "" {
			parts = append(parts, v.Refusal.Subject)
			if v.Refusal.Detail != "" {
				parts = append(parts, v.Refusal.Detail)
			}
		}
		return strings.Join(parts, ":")
	case v.Cause != "":
		return string(v.Cause)
	}
	return string(v.Outcome)
}

// awaitingFoodPlan is the refusal of a method the food plan does not yet
// support: capacity names what the plan has to provide.
func awaitingFoodPlan(capacity string) Verdict { return awaitingPlan("food_plan", capacity) }

// skipsToPlacement reports a shell attempt that leaves the usual placement to
// go on: its method already used, no verified space, or a fact it needs unknown.
func (v Verdict) skipsToPlacement() bool {
	return v == BuildingReasonUsed || v == BuildingReasonNoSpace || v.Is(RefusalFieldUnavailable)
}

// standing is a nothing-to-do or in-progress verdict named by cause.
func standing(outcome Outcome, cause Cause) Verdict { return Verdict{Outcome: outcome, Cause: cause} }

// The shared verdicts. Exits that need a more specific refusal build one
// with refuse and its helpers; none may be a catch-all.
var (
	BuildingReasonAdmitted     = Verdict{Outcome: OutcomeAdmitted}
	BuildingReasonDisabled     = standing(OutcomeNothingToDo, "disabled")
	BuildingReasonNoReview     = standing(OutcomeNothingToDo, "no_current_review")
	BuildingReasonNoDeficit    = standing(OutcomeNothingToDo, "no_active_deficit")
	BuildingReasonExpired      = standing(OutcomeNothingToDo, "expired")
	BuildingReasonExistingWork = standing(OutcomeInProgress, "existing_work")
	BuildingReasonUsed         = standing(OutcomeInProgress, "method_already_used")
	// BuildingBunksOpen: the initial shelter's indoor furnishing waits on
	// its open bunk rungs, which do not hold the ring itself (#641).
	BuildingBunksOpen = standing(OutcomeInProgress, "shelter_bunks_open")
	// BuildingReasonHoldFallback: the fight's hold-the-line formation was
	// re-formed as squad defense because the raid crossed the line (#118).
	BuildingReasonHoldFallback = standing(OutcomeInProgress, "hold_fallback")
	// BuildingReasonCombatOrders: the fight's stop sent changed orders
	// (#852), recorded as its plan's evidence.
	BuildingReasonCombatOrders = standing(OutcomeInProgress, "combat_orders")
	// BuildingReasonSeparation defers a butcher bill while the separated
	// butcher spot build still owns the food-supply goal.
	BuildingReasonSeparation = standing(OutcomeInProgress, "butcher_separation_pending")
	// BuildingReasonNotInteractive: the choice dialog's own interactivity
	// delay has not elapsed; the next review re-reads it.
	BuildingReasonNotInteractive = standing(OutcomeInProgress, "dialog_not_interactive")
	// BuildingReasonWaiting is a migrated planner's result when the
	// coordinator gave a claim it needs to a higher-ranked proposal; the step
	// row's proposal outcome names the claim.
	BuildingReasonWaiting = standing(OutcomeInProgress, "waiting_on_claim")
	// BuildingReasonHeld is the shrine planner's answer while every target
	// shrine holds; RoutineShrineResult.Hold carries the reason.
	BuildingReasonHeld               = standing(OutcomeInProgress, "breach_held")
	BuildingComfortWait              = standing(OutcomeInProgress, "waiting_for_native_comfort_use")
	BuildingExistingFacility         = standing(OutcomeInProgress, "existing_facility_needs_bill_or_upkeep")
	BuildingHospitalConvert          = standing(OutcomeInProgress, "hospital_bed_convert_pending")
	BuildingSleepingUseNeeded        = standing(OutcomeInProgress, "sleeping_use_needed")
	BuildingReasonNoSpace            = noSpace("verified_space")
	BuildingReasonRefused            = refuse(RefusalSharedAdmission, "", "")
	BuildingReasonExhausted          = refuse(RefusalSharedAdmission, "retry_bound", "exhausted")
	BuildingReasonNoSquad            = noWorker("squad")
	BuildingShellBlocked             = awaitingPlan("earlier_shell", "")
	BuildingShelterPending           = awaitingPlan("initial_shelter", "")
	BuildingExcavationBlocked        = collapsePending("ore_tunnel")
	BuildingSuiteStock               = awaitingPlan("suite_materials", "")
	BuildingWorkshopUnavailable      = noWorker("workshop_bench")
	BuildingWorkshopResearch         = awaitingPlan("research", "workshop")
	BuildingResearchBench            = awaitingPlan("research_bench", "")
	BuildingResearchBenchUnavailable = awaitingPlan("research_bench", "unbuildable")
	BuildingHospitalUnavailable      = noSpace("hospital_bed")
	BuildingSleepingUnavailable      = noSpace("sleeping_bed")
	BuildingNoWeaponBench            = awaitingPlan("weapon_bench", "")
	// BuildingReasonDemand is a migrated planner's result when the step's
	// stock, less the quantities earlier proposals claimed and admitted
	// plans hold, does not cover a quantity it needs and no less urgent
	// commitment could be preempted to release it; the outcome's Demand is
	// the shortfall.
	BuildingReasonDemand    = awaitingPlan("stock", "unmet_demand")
	stoneShellUnstocked     = awaitingPlan("replacement_material", "")
	defensePerimeterNoStone = awaitingPlan("perimeter_stone", "")
)
