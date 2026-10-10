package buildingruntime

import (
	"fmt"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Outcome is what a planner's step came to: the closed set every routine
// result reports. Routines branch on it (and, for a wait or a refusal, on the
// Refusal's kind); no outcome hides a second meaning.
type Outcome string

const (
	// OutcomeAdmitted: the step admitted a plan.
	OutcomeAdmitted Outcome = "admitted"
	// OutcomeNothingToDo: the goal has no active deficit.
	OutcomeNothingToDo Outcome = "nothing_to_do"
	// OutcomeDisabled: the planner is switched off in this runtime.
	OutcomeDisabled Outcome = "disabled"
	// OutcomeNoReview: there is no current rounds to judge.
	OutcomeNoReview Outcome = "no_current_review"
	// OutcomeExpired: the proposal went stale before it could be admitted.
	OutcomeExpired Outcome = "expired"
	// OutcomeOrdersSent: the fight's stop sent changed orders,
	// recorded as its plan's evidence.
	OutcomeOrdersSent Outcome = "combat_orders"
	// OutcomeHoldFallback: the fight's hold-the-line formation was re-formed
	// as squad defense because the raid crossed the line.
	OutcomeHoldFallback Outcome = "hold_fallback"
	// OutcomeWaiting: the goal waits on something that is not a failure; the
	// Refusal names what with a wait kind.
	OutcomeWaiting Outcome = "waiting"
	// OutcomeRefused: the step could not proceed; the Refusal says why.
	OutcomeRefused Outcome = "refused"
)

// The refusal causes and the wait causes: an OutcomeRefused verdict carries one
// of the first and an OutcomeWaiting verdict one of the second; there is no
// catch-all. English for them lives in policy.Wording.
var (
	refusalKinds = []policy.Cause{policy.CauseCollapsePending, policy.CauseNoWorker, policy.CauseAwaitingPlan, policy.CauseFieldUnavailable, policy.CauseNoSpace, policy.CauseSharedAdmission, policy.CauseRetriesSpent, policy.CauseRockNotDug, policy.CauseSiteBlocked}
	waitKinds    = policy.WaitCauses
)

// Refusal says why an OutcomeRefused step stopped or what an OutcomeWaiting
// goal waits on: a cause, the thing it concerns and optional detail. Detail is
// machine-only: it rides in String and never into persisted rows or wording.
type Refusal struct {
	Kind    policy.Cause
	Subject string
	Detail  string
}

// Verdict is the pair every routine result embeds: its Outcome and, when
// refused or waiting, the Refusal. The zero Verdict means the step reached no
// verdict (the caller keeps going).
type Verdict struct {
	Outcome Outcome
	Refusal Refusal
}

// refuse is a refused Verdict. A kind outside the closed set is a
// programming error and panics.
func refuse(kind policy.Cause, subject, detail string) Verdict {
	return mustValid(Verdict{Outcome: OutcomeRefused, Refusal: Refusal{Kind: kind, Subject: subject, Detail: detail}})
}

// waitOn is a waiting Verdict; a kind outside the wait kinds panics.
func waitOn(kind policy.Cause) Verdict {
	return mustValid(Verdict{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: kind}})
}

// waitFor is a waiting Verdict that names what it concerns.
func waitFor(kind policy.Cause, subject string) Verdict {
	return mustValid(Verdict{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: kind, Subject: subject}})
}

func mustValid(v Verdict) Verdict {
	if err := v.Validate(); err != nil {
		panic(err)
	}
	return v
}

func fieldUnavailable(field string) Verdict { return refuse(policy.CauseFieldUnavailable, field, "") }
func noSpace(subject string) Verdict        { return refuse(policy.CauseNoSpace, subject, "") }

func rockNotDug(subject, detail string) Verdict {
	return refuse(policy.CauseRockNotDug, subject, detail)
}
func noWorker(subject string) Verdict { return refuse(policy.CauseNoWorker, subject, "") }
func siteBlocked(subject, detail string) Verdict {
	return refuse(policy.CauseSiteBlocked, subject, detail)
}
func collapsePending(subject string) Verdict {
	return refuse(policy.CauseCollapsePending, subject, "")
}
func awaitingPlan(subject, detail string) Verdict {
	return refuse(policy.CauseAwaitingPlan, subject, detail)
}

// claimHeld is the wait of a step whose claim another planner already holds
// this step (a bench, an animal); the subject names the claim.
func claimHeld(subject string) Verdict {
	return mustValid(Verdict{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: policy.CauseClaim, Subject: subject}})
}

// retryBudgetWait is the wait of a subject native refused transiently in a
// world that has not changed since; the detail is native's reason.
func retryBudgetWait(subject, detail string) Verdict {
	return mustValid(Verdict{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: policy.CauseRetryBudgetWait, Subject: subject, Detail: detail}})
}

// awaitingMethod is the refusal for a policy method that names what the
// goal waits on (a power or temperature wait, a cooler's power).
func awaitingMethod[M ~string](method M) Verdict { return awaitingPlan(string(method), "") }

// researchWait is the refusal of a method that needs a native research
// project the census has not finished; EnsureResearch's roadmap is what gets
// there.
func researchWait(project string) Verdict { return awaitingPlan("research", project) }

// awaitingFoodPlan is the refusal of a method the food plan does not yet
// support: capacity names what the plan has to provide.
func awaitingFoodPlan(capacity string) Verdict { return awaitingPlan("food_plan", capacity) }

// Validate reports a verdict that is not one of the closed forms: an unknown
// outcome, a refusal or wait without a kind from its closed set, a refusal
// on an outcome that carries none, or a shared-admission refusal that names
// no reason.
func (v Verdict) Validate() error {
	switch v.Outcome {
	case "":
		if v != (Verdict{}) {
			return fmt.Errorf("verdict without an outcome: %+v", v)
		}
	case OutcomeAdmitted, OutcomeNothingToDo, OutcomeDisabled, OutcomeNoReview, OutcomeExpired, OutcomeOrdersSent, OutcomeHoldFallback:
		if v.Refusal != (Refusal{}) {
			return fmt.Errorf("%s verdict carries a refusal: %+v", v.Outcome, v.Refusal)
		}
	case OutcomeWaiting:
		if !slices.Contains(waitKinds, v.Refusal.Kind) {
			return fmt.Errorf("wait without a known kind: %+v", v.Refusal)
		}
		if v.Refusal.Kind == policy.CauseMethodUsed && v.Refusal.Subject == "" {
			return fmt.Errorf("method-used wait without the work it names: %+v", v.Refusal)
		}
	case OutcomeRefused:
		if !slices.Contains(refusalKinds, v.Refusal.Kind) {
			return fmt.Errorf("refusal without a known kind: %+v", v.Refusal)
		}
		if v.Refusal.Kind == policy.CauseSharedAdmission && v.Refusal.Subject == "" {
			return fmt.Errorf("shared admission refusal without the reason it names: %+v", v.Refusal)
		}
		if v.Refusal.Kind == policy.CauseRetriesSpent && v.Refusal.Subject == "" {
			return fmt.Errorf("retry budget refusal without the step it names: %+v", v.Refusal)
		}
	default:
		return fmt.Errorf("unknown outcome %q", v.Outcome)
	}
	return nil
}

// IsZero reports a step that reached no verdict.
func (v Verdict) IsZero() bool { return v == Verdict{} }

// Is reports a refusal or wait of kind.
func (v Verdict) Is(kind policy.Cause) bool {
	return (v.Outcome == OutcomeRefused || v.Outcome == OutcomeWaiting) && v.Refusal.Kind == kind
}

// String is the machine rendering of a verdict for the flight recorder, the
// timeline's reason= field and snapshot names, one token without
// spaces: a refusal or wait reads "kind", "kind:subject" or
// "kind:subject:detail"; any other verdict reads its outcome.
func (v Verdict) String() string {
	if v.Outcome != OutcomeRefused && v.Outcome != OutcomeWaiting {
		return string(v.Outcome)
	}
	parts := []string{string(v.Refusal.Kind)}
	if v.Refusal.Subject != "" {
		parts = append(parts, v.Refusal.Subject)
		if v.Refusal.Detail != "" {
			parts = append(parts, v.Refusal.Detail)
		}
	}
	return strings.Join(parts, ":")
}

// skipsToPlacement reports a shell attempt that leaves the usual placement to
// go on: its method already used, no verified space, or a fact it needs unknown.
func (v Verdict) skipsToPlacement() bool {
	return v.Is(policy.CauseMethodUsed) || v.Is(policy.CauseNoSpace) || v.Is(policy.CauseFieldUnavailable)
}

// The shared verdicts. Exits that need a more specific refusal build one
// with refuse and its helpers; none may be a catch-all.
var (
	BuildingReasonAdmitted     = Verdict{Outcome: OutcomeAdmitted}
	BuildingReasonDisabled     = Verdict{Outcome: OutcomeDisabled}
	BuildingReasonNoReview     = Verdict{Outcome: OutcomeNoReview}
	BuildingReasonNoDeficit    = Verdict{Outcome: OutcomeNothingToDo}
	BuildingReasonExpired      = Verdict{Outcome: OutcomeExpired}
	BuildingReasonExistingWork = waitOn(policy.CauseExistingWork)
	// BuildingBunksOpen: the initial shelter's indoor furnishing waits on
	// its open bunk rungs, which do not hold the ring itself.
	BuildingBunksOpen          = waitOn(policy.CauseBunksOpen)
	BuildingReasonHoldFallback = Verdict{Outcome: OutcomeHoldFallback}
	BuildingReasonCombatOrders = Verdict{Outcome: OutcomeOrdersSent}
	// BuildingReasonSeparation defers a butcher bill while the separated
	// butcher spot build still owns the food-supply goal.
	BuildingReasonSeparation = waitOn(policy.CauseSeparation)
	// BuildingReasonNotInteractive: the choice dialog's own interactivity
	// delay has not elapsed; the next review re-reads it.
	BuildingReasonNotInteractive = waitOn(policy.CauseDialog)
	// BuildingReasonWaiting is a proposal planner's result when the
	// coordinator gave a claim it needs to a higher-ranked proposal; the step
	// row's proposal outcome names the claim.
	BuildingReasonWaiting = waitOn(policy.CauseClaim)
	// BuildingReasonHeld is the shrine planner's answer while every target
	// shrine holds; RoundsShrineResult.Hold carries the reason.
	BuildingReasonHeld               = waitOn(policy.CauseBreachHeld)
	BuildingComfortWait              = waitOn(policy.CauseComfortUse)
	BuildingTemperatureWait          = waitOn(policy.CauseRoomTemperature)
	BuildingExistingFacility         = waitOn(policy.CauseFacility)
	BuildingHospitalConvert          = waitOn(policy.CauseHospitalConvert)
	BuildingSleepingUseNeeded        = waitOn(policy.CauseSleepingUse)
	BuildingReasonNoSquad            = noWorker("squad")
	BuildingShellBlocked             = awaitingPlan("earlier_shell", "")
	BuildingShelterPending           = awaitingPlan("initial_shelter", "")
	BuildingSuiteStock               = awaitingPlan("suite_materials", "walls")
	BuildingWorkshopUnavailable      = noWorker("workshop_bench")
	BuildingWorkshopResearch         = awaitingPlan("research", "workshop")
	BuildingResearchBench            = awaitingPlan("research_bench", "")
	BuildingResearchBenchUnavailable = awaitingPlan("research_bench", "unbuildable")
	BuildingHospitalUnavailable      = awaitingPlan("buildable_bed", "hospital")
	BuildingSleepingUnavailable      = awaitingPlan("buildable_bed", "")
	// BuildingNoLayoutPlan holds a site search that anchors on the layout
	// plan until one exists: nothing is sited on where the colonists stand.
	BuildingNoLayoutPlan  = awaitingPlan("layout_plan", "")
	BuildingNoWeaponBench = awaitingPlan("weapon_bench", "")
	// BuildingReasonDemand is a proposal planner's result when the step's
	// stock, less the quantities earlier proposals claimed and admitted
	// plans hold, does not cover a quantity it needs and no less urgent
	// commitment could be preempted to release it; the outcome's Demand is
	// the shortfall.
	BuildingReasonDemand    = awaitingPlan("stock", "unmet_demand")
	stoneShellUnstocked     = awaitingPlan("replacement_material", "")
	defensePerimeterNoStone = awaitingPlan("perimeter_stone", "")
)
