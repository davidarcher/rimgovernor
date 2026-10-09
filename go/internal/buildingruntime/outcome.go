package buildingruntime

import (
	"fmt"
	"slices"
	"strings"
	"unicode"
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

// RefusalKind is the closed set of reasons a step refuses or waits. An
// OutcomeRefused verdict carries one of the refusal kinds and an
// OutcomeWaiting verdict one of the wait kinds; there is no catch-all.
type RefusalKind string

// Refusal kinds.
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
	// RefusalSharedAdmission: the shared admission path turned the plan down;
	// the subject is the reason it gave, the detail its resource.
	RefusalSharedAdmission RefusalKind = "shared_admission_refused"
	// RefusalRetriesSpent: native refused the subject for good (the shared
	// refusal budget, budgetVerdict); the subject names what was retried and
	// the detail is native's own reason.
	RefusalRetriesSpent RefusalKind = "retry_budget_spent"
	// RefusalRockNotDug: planned rock still stands after its dig plan
	// settled; the subject names the dig, the detail the count.
	RefusalRockNotDug RefusalKind = "rock_not_dug"
	// RefusalSiteBlocked: the site the step works at cannot be used; the
	// subject names the site, the detail what blocks it.
	RefusalSiteBlocked RefusalKind = "site_blocked"
)

// Wait kinds: what an OutcomeWaiting goal waits on.
const (
	// WaitMethodUsed: the method already did its part and the goal waits for
	// that work to finish.
	WaitMethodUsed RefusalKind = "method_already_used"
	// WaitExistingWork: earlier work stands and the goal moves with it.
	WaitExistingWork RefusalKind = "already_working_on_it"
	// WaitBunksOpen: the initial shelter's indoor furnishing waits on its open
	// bunk rungs, which do not hold the ring itself.
	WaitBunksOpen RefusalKind = "shelter_bunks_open"
	// WaitBreachHeld: the shrine planner holds while every target shrine holds;
	// RoundsShrineResult.Hold carries the reason.
	WaitBreachHeld RefusalKind = "breach_held"
	// WaitComfortUse: native comfort use has to happen first.
	WaitComfortUse RefusalKind = "waiting_for_native_comfort_use"
	// WaitFacility: an existing facility needs a bill or upkeep, not a build.
	WaitFacility RefusalKind = "existing_facility_needs_bill_or_upkeep"
	// WaitHospitalConvert: a bed is being converted to a hospital bed.
	WaitHospitalConvert RefusalKind = "hospital_bed_convert_pending"
	// WaitSleepingUse: the beds provided have to be used before more are built.
	WaitSleepingUse RefusalKind = "sleeping_use_needed"
	// WaitSeparation defers a butcher bill while the separated butcher spot
	// build still owns the food-supply goal.
	WaitSeparation RefusalKind = "butcher_separation_pending"
	// WaitDialog: the choice dialog's own interactivity delay has not elapsed;
	// the next review re-reads it.
	WaitDialog RefusalKind = "dialog_not_interactive"
	// WaitClaim: the coordinator gave a claim the planner needs to a
	// higher-ranked proposal; the step row's proposal outcome names the claim.
	WaitClaim RefusalKind = "waiting_on_claim"
	// WaitFacilityAccess: a comfort facility stands but some colonists cannot
	// reach it; the subject names which (dining or recreation).
	WaitFacilityAccess RefusalKind = "existing_facility_access_blocked"
	// WaitRoomTemperature: native room temperature has to settle after the
	// last change before the goal judges it.
	WaitRoomTemperature RefusalKind = "waiting_for_native_temperature"
	// WaitRetryBudget: native refused the subject transiently and the world has
	// not changed since; the subject names it, the detail native's reason.
	WaitRetryBudget RefusalKind = "retry_budget_waiting"
)

var (
	refusalKinds = []RefusalKind{RefusalCollapsePending, RefusalNoWorker, RefusalAwaitingPlan, RefusalFieldUnavailable, RefusalNoSpace, RefusalSharedAdmission, RefusalRetriesSpent, RefusalRockNotDug, RefusalSiteBlocked}
	waitKinds    = []RefusalKind{WaitMethodUsed, WaitExistingWork, WaitBunksOpen, WaitBreachHeld, WaitComfortUse, WaitFacility, WaitHospitalConvert, WaitSleepingUse, WaitSeparation, WaitDialog, WaitClaim, WaitRoomTemperature, WaitFacilityAccess, WaitRetryBudget}
)

// Refusal says why an OutcomeRefused step stopped or what an OutcomeWaiting
// goal waits on: a kind, the thing it concerns and optional detail.
type Refusal struct {
	Kind    RefusalKind
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
func refuse(kind RefusalKind, subject, detail string) Verdict {
	return mustValid(Verdict{Outcome: OutcomeRefused, Refusal: Refusal{Kind: kind, Subject: subject, Detail: detail}})
}

// waitOn is a waiting Verdict; a kind outside the wait kinds panics.
func waitOn(kind RefusalKind) Verdict {
	return mustValid(Verdict{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: kind}})
}

// waitFor is a waiting Verdict that names what it concerns.
func waitFor(kind RefusalKind, subject string) Verdict {
	return mustValid(Verdict{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: kind, Subject: subject}})
}

func mustValid(v Verdict) Verdict {
	if err := v.Validate(); err != nil {
		panic(err)
	}
	return v
}

func fieldUnavailable(field string) Verdict { return refuse(RefusalFieldUnavailable, field, "") }
func noSpace(subject string) Verdict        { return refuse(RefusalNoSpace, subject, "") }

func rockNotDug(subject, detail string) Verdict { return refuse(RefusalRockNotDug, subject, detail) }
func noWorker(subject string) Verdict           { return refuse(RefusalNoWorker, subject, "") }
func siteBlocked(subject, detail string) Verdict {
	return refuse(RefusalSiteBlocked, subject, detail)
}
func collapsePending(subject string) Verdict {
	return refuse(RefusalCollapsePending, subject, "")
}
func awaitingPlan(subject, detail string) Verdict {
	return refuse(RefusalAwaitingPlan, subject, detail)
}

// claimHeld is the wait of a step whose claim another planner already holds
// this step (a bench, an animal); the subject names the claim.
func claimHeld(subject string) Verdict {
	return mustValid(Verdict{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: WaitClaim, Subject: subject}})
}

// retryBudgetWait is the wait of a subject native refused transiently in a
// world that has not changed since; the detail is native's reason.
func retryBudgetWait(subject, detail string) Verdict {
	return mustValid(Verdict{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: WaitRetryBudget, Subject: subject, Detail: detail}})
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
		if v.Refusal.Kind == WaitMethodUsed && v.Refusal.Subject == "" {
			return fmt.Errorf("method-used wait without the work it names: %+v", v.Refusal)
		}
	case OutcomeRefused:
		if !slices.Contains(refusalKinds, v.Refusal.Kind) {
			return fmt.Errorf("refusal without a known kind: %+v", v.Refusal)
		}
		if v.Refusal.Kind == RefusalSharedAdmission && v.Refusal.Subject == "" {
			return fmt.Errorf("shared admission refusal without the reason it names: %+v", v.Refusal)
		}
		if v.Refusal.Kind == RefusalRetriesSpent && v.Refusal.Subject == "" {
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
func (v Verdict) Is(kind RefusalKind) bool {
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

// Text is the one plain-English rendering of a verdict, for the launcher
// and the journal: each outcome and kind has a sentence template and the
// subject and detail fill it in.
func (v Verdict) Text() string {
	switch v.Outcome {
	case OutcomeAdmitted:
		return "plan admitted"
	case OutcomeNothingToDo:
		return "nothing to do right now"
	case OutcomeDisabled:
		return "this planner is switched off"
	case OutcomeNoReview:
		return "no current review to judge"
	case OutcomeExpired:
		return "the proposal went stale"
	case OutcomeOrdersSent:
		return "combat orders are running"
	case OutcomeHoldFallback:
		return "the hold line fell back to squad defense"
	case OutcomeWaiting, OutcomeRefused:
		return v.kindText()
	}
	return ""
}

// kindText is the sentence of a refusal or wait kind.
func (v Verdict) kindText() string {
	subject, detail := words(v.Refusal.Subject), words(v.Refusal.Detail)
	// aside puts the subject (and detail) in parentheses after the sentence.
	aside := func(sentence string) string {
		switch {
		case subject != "" && detail != "":
			return sentence + " (" + subject + ": " + detail + ")"
		case subject != "":
			return sentence + " (" + subject + ")"
		}
		return sentence
	}
	switch v.Refusal.Kind {
	case RefusalCollapsePending:
		if subject != "" {
			return "a collapse is pending at the " + subject
		}
		return "a collapse is pending"
	case RefusalNoWorker:
		return aside("no colonist free to do it")
	case RefusalAwaitingPlan:
		text := "waiting on an earlier plan"
		if subject != "" {
			text = "waiting on " + subject
		}
		if detail != "" {
			text += " (" + detail + ")"
		}
		return text
	case RefusalFieldUnavailable:
		if subject == "" {
			return "the game did not report a fact it needs"
		}
		return "the game did not report " + subject
	case RefusalNoSpace:
		return aside("no space found for it")
	case RefusalSharedAdmission:
		return aside("the shared admission check turned the plan down")
	case RefusalRetriesSpent:
		return aside("tried as often as it may")
	case RefusalRockNotDug:
		return aside("rock it needs dug is still standing")
	case RefusalSiteBlocked:
		if subject == "" {
			subject = "site"
		}
		if detail != "" {
			return "the " + subject + " is blocked (" + detail + ")"
		}
		return "the " + subject + " is blocked"
	case WaitMethodUsed:
		return aside("waiting for work it already started")
	case WaitExistingWork:
		return aside("already working on it")
	case WaitBunksOpen:
		return aside("waiting on the shelter's open bunks")
	case WaitBreachHeld:
		return aside("holding off on the ancient shrine breach")
	case WaitComfortUse:
		return aside("waiting for colonists to use the comfort already provided")
	case WaitFacility:
		return aside("an existing facility needs a bill or upkeep first")
	case WaitHospitalConvert:
		return aside("waiting for a bed to become a hospital bed")
	case WaitSleepingUse:
		return aside("waiting for colonists to use the beds provided")
	case WaitSeparation:
		return aside("waiting for the butcher spot to be built apart")
	case WaitDialog:
		return aside("waiting for the choice dialog to accept an answer")
	case WaitClaim:
		return aside("waiting on a claim held by a higher-ranked proposal")
	case WaitFacilityAccess:
		return aside("a facility stands but some colonists cannot reach it")
	case WaitRetryBudget:
		return aside("the game refused it and nothing has changed since")
	case WaitRoomTemperature:
		return aside("waiting for the room temperature to settle")
	}
	return ""
}

// words turns an identifier into words: "verified_space" reads "verified
// space" and "EnsureFoodSupply" reads "Ensure food supply".
func words(identifier string) string {
	var b strings.Builder
	var previous rune
	for _, r := range identifier {
		switch {
		case r == '_' || r == '-':
			b.WriteRune(' ')
		case unicode.IsUpper(r) && (unicode.IsLower(previous) || unicode.IsDigit(previous)):
			b.WriteRune(' ')
			b.WriteRune(unicode.ToLower(r))
		default:
			b.WriteRune(r)
		}
		previous = r
	}
	return b.String()
}

// skipsToPlacement reports a shell attempt that leaves the usual placement to
// go on: its method already used, no verified space, or a fact it needs unknown.
func (v Verdict) skipsToPlacement() bool {
	return v.Is(WaitMethodUsed) || v.Is(RefusalNoSpace) || v.Is(RefusalFieldUnavailable)
}

// The shared verdicts. Exits that need a more specific refusal build one
// with refuse and its helpers; none may be a catch-all.
var (
	BuildingReasonAdmitted     = Verdict{Outcome: OutcomeAdmitted}
	BuildingReasonDisabled     = Verdict{Outcome: OutcomeDisabled}
	BuildingReasonNoReview     = Verdict{Outcome: OutcomeNoReview}
	BuildingReasonNoDeficit    = Verdict{Outcome: OutcomeNothingToDo}
	BuildingReasonExpired      = Verdict{Outcome: OutcomeExpired}
	BuildingReasonExistingWork = waitOn(WaitExistingWork)
	// BuildingBunksOpen: the initial shelter's indoor furnishing waits on
	// its open bunk rungs, which do not hold the ring itself.
	BuildingBunksOpen          = waitOn(WaitBunksOpen)
	BuildingReasonHoldFallback = Verdict{Outcome: OutcomeHoldFallback}
	BuildingReasonCombatOrders = Verdict{Outcome: OutcomeOrdersSent}
	// BuildingReasonSeparation defers a butcher bill while the separated
	// butcher spot build still owns the food-supply goal.
	BuildingReasonSeparation = waitOn(WaitSeparation)
	// BuildingReasonNotInteractive: the choice dialog's own interactivity
	// delay has not elapsed; the next review re-reads it.
	BuildingReasonNotInteractive = waitOn(WaitDialog)
	// BuildingReasonWaiting is a proposal planner's result when the
	// coordinator gave a claim it needs to a higher-ranked proposal; the step
	// row's proposal outcome names the claim.
	BuildingReasonWaiting = waitOn(WaitClaim)
	// BuildingReasonHeld is the shrine planner's answer while every target
	// shrine holds; RoundsShrineResult.Hold carries the reason.
	BuildingReasonHeld               = waitOn(WaitBreachHeld)
	BuildingComfortWait              = waitOn(WaitComfortUse)
	BuildingTemperatureWait          = waitOn(WaitRoomTemperature)
	BuildingExistingFacility         = waitOn(WaitFacility)
	BuildingHospitalConvert          = waitOn(WaitHospitalConvert)
	BuildingSleepingUseNeeded        = waitOn(WaitSleepingUse)
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
