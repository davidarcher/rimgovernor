// Package startuplabor is the startup-labor diagnosis (#639, epic #638):
// the bounded, structured evidence that tells planner starvation apart
// from a missing material, a missing worker, an admitted-but-unworked
// action and ordinary non-work activity, plus the idle-pawn tick
// accounting and the project-limit comparison the campaign child reuses.
//
// It is diagnosis only. Nothing here ranks, plans, dispatches or changes
// game speed or authority; every field is read from evidence the run
// already holds (the durable review record, plan progress and one bounded
// home/list_pawns sample per observation). Missing evidence is reported as
// missing -- a subject never infers a fact it did not observe.
package startuplabor

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Class is one diagnosis outcome for a goal at a review.
type Class string

const (
	// ClassSlotRefusal: the ranking saw the goal and did not select it.
	ClassSlotRefusal Class = "slot_refusal"
	// ClassMaterialBlocker: the open action is held on stock, a
	// reservation or spending policy.
	ClassMaterialBlocker Class = "material_blocker"
	// ClassWorkerBlocker: the goal's work has no pawn -- a refusal on
	// labor, or an open action held on an unavailable worker.
	ClassWorkerBlocker Class = "worker_blocker"
	// ClassUnresolvedAction: the action was dispatched and its effect is
	// still outstanding (admitted but unworked).
	ClassUnresolvedAction Class = "unresolved_action"
	// ClassNonWorkActivity: nothing blocks the goal; the observed pawns
	// are asleep, eating, recreating or medically resting.
	ClassNonWorkActivity Class = "non_work_activity"
	// ClassProgressing: the action is moving (prepared, dispatched with
	// its effect resolved, or completed).
	ClassProgressing Class = "progressing"
	// ClassUnknown: the evidence needed to classify was not observed.
	// Diagnosis.Missing names exactly what.
	ClassUnknown Class = "unknown"
)

// World keys a diagnosis to the colony and load it was read under; a
// subject from another world is never compared with this one.
type World struct {
	Colony, Load string
	Map          int
}

// Slot is the review's own ranking row for the goal (the durable
// RoutineDevelopmentRow), as the diagnosis reads it.
type Slot struct {
	Reason              policy.DevelopmentReason
	Bottleneck          policy.WorkType
	Score               float64
	Selected, Committed bool
	Idle                bool
}

// Subject is one goal at one review: what the ranking did with it, the
// open action bound to it (nil when the goal has none), and the labor
// observation that covers its work (nil when no sample was taken).
type Subject struct {
	World      World
	ReviewTick domain.Tick
	Goal       domain.GoalID
	Method     domain.MethodID
	Action     domain.ActionID
	// Slot is the ranking row, nil when the goal was not on the review.
	Slot *Slot
	// Progress is the open action's view, nil when the goal bound none.
	Progress *domain.ProgressView
	// Activity is what the pawns eligible for this goal's work were doing
	// at the nearest sample; nil means unobserved, not idle.
	Activity []Activity
	// ShelterBeds reports whether the shelter goal is waiting on the
	// shelter-beds rung; unknown on every other goal.
	ShelterBeds domain.Fact[bool]
}

// Diagnosis is the stable artifact row: the key, the class, the evidence
// that decided it and whatever was missing.
type Diagnosis struct {
	Colony, Load string
	Map          int
	ReviewTick   domain.Tick
	Goal         domain.GoalID
	Method       domain.MethodID
	Action       domain.ActionID
	Class        Class
	// Reason is the ranking's refusal reason when one decided the class.
	Reason policy.DevelopmentReason
	// Held are the fresh hold reasons on the open action, in the domain's
	// own order.
	Held []domain.HeldReason
	// Blocker is the single hold reason that decided a material or worker
	// class.
	Blocker domain.HeldReason
	// Stage is the open action's progress stage, empty when there is none.
	Stage domain.Stage
	// Unresolved is the open action's outstanding-effect flag.
	Unresolved bool
	// Selected, Committed, Idle come from the ranking row.
	Selected, Committed, SlotIdle bool
	// Bottleneck is the scarce work type the ranking named, if any.
	Bottleneck policy.WorkType
	// ShelterBeds: true/false when known, absent from the row otherwise.
	ShelterBeds *bool
	// Activities counts the observed pawn activities by kind; empty when
	// nothing was sampled.
	Activities map[Activity]int
	// Missing names every fact the classification wanted and did not have.
	Missing []string
}

// materialHolds are the hold reasons that mean "the stuff is not there or
// not ours to take".
var materialHolds = map[domain.HeldReason]bool{
	domain.HeldMaterialRequired:  true,
	domain.HeldInsufficientStock: true,
	domain.HeldSpendingBlocked:   true,
	domain.HeldAlreadyReserved:   true,
	domain.HeldInvalidHeld:       true,
	domain.HeldThingAbsent:       true,
	domain.HeldStorageMissing:    true,
}

// workerHolds are the hold reasons that mean "no pawn can take this".
var workerHolds = map[domain.HeldReason]bool{
	domain.HeldCleanerUnavailable:             true,
	domain.HeldDoctorUnavailable:              true,
	domain.HeldDraftOwnership:                 true,
	domain.HeldEquipPawnUnavailable:           true,
	domain.HeldGearReplacePawnUnavailable:     true,
	domain.HeldHaulerUnavailable:              true,
	domain.HeldOpenerUnavailable:              true,
	domain.HeldRecoveryServicePawnUnavailable: true,
	domain.HeldRepairerUnavailable:            true,
	domain.HeldRescuerUnavailable:             true,
	domain.HeldUrgentCompetingWork:            true,
}

// laborRefusals are the ranking reasons that are a worker shortage rather
// than an ordinary slot refusal.
var laborRefusals = map[policy.DevelopmentReason]bool{
	policy.DevelopmentNoWorkers: true,
	policy.DevelopmentLabor:     true,
	policy.DevelopmentLaborIdle: true,
}

// Diagnose classifies one subject. It reads only what the subject carries:
// an absent fact produces ClassUnknown with the fact named in Missing, and
// never a guess.
func Diagnose(s Subject) Diagnosis {
	d := Diagnosis{
		Colony: s.World.Colony, Load: s.World.Load, Map: s.World.Map,
		ReviewTick: s.ReviewTick, Goal: s.Goal, Method: s.Method, Action: s.Action,
	}
	if v, known := s.ShelterBeds.Value(); known {
		d.ShelterBeds = &v
	}
	if s.Slot != nil {
		d.Reason, d.Bottleneck = s.Slot.Reason, s.Slot.Bottleneck
		d.Selected, d.Committed, d.SlotIdle = s.Slot.Selected, s.Slot.Committed, s.Slot.Idle
	}
	if len(s.Activity) > 0 {
		d.Activities = map[Activity]int{}
		for _, a := range s.Activity {
			d.Activities[a]++
		}
	}
	if s.Progress != nil {
		d.Stage, d.Unresolved = s.Progress.Stage, s.Progress.Unresolved
		if held, ok := s.Progress.FreshHeldReason(); ok {
			d.Held = held
		}
	}
	d.Class = classify(s, &d)
	sort.Strings(d.Missing)
	return d
}

func classify(s Subject, d *Diagnosis) Class {
	if s.Slot == nil && s.Progress == nil {
		d.Missing = append(d.Missing, "development_row", "progress")
		return ClassUnknown
	}
	if s.Progress != nil {
		// An outstanding dispatch outranks every other reading: the
		// action was admitted and its effect has not come back.
		if s.Progress.Unresolved {
			return ClassUnresolvedAction
		}
		for _, r := range d.Held {
			if materialHolds[r] {
				d.Blocker = r
				return ClassMaterialBlocker
			}
		}
		for _, r := range d.Held {
			if workerHolds[r] {
				d.Blocker = r
				return ClassWorkerBlocker
			}
		}
		if len(d.Held) > 0 {
			// A held action with a reason in neither set is evidence we
			// hold but do not classify; say so rather than pick a side.
			d.Blocker = d.Held[0]
			d.Missing = append(d.Missing, "hold_reason_class")
			return ClassUnknown
		}
		switch s.Progress.Stage {
		case domain.Pending:
			// Pending with no hold: fall through to the ranking and the
			// pawn sample below.
		case "":
			d.Missing = append(d.Missing, "progress_stage")
			return ClassUnknown
		default:
			return ClassProgressing
		}
	}
	if s.Slot == nil {
		d.Missing = append(d.Missing, "development_row")
		return ClassUnknown
	}
	if !s.Slot.Selected {
		if s.Slot.Reason == "" {
			d.Missing = append(d.Missing, "development_reason")
			return ClassUnknown
		}
		if laborRefusals[s.Slot.Reason] {
			return ClassWorkerBlocker
		}
		return ClassSlotRefusal
	}
	// Selected, nothing held: only the pawn sample can say whether the
	// colony is busy elsewhere.
	if s.Activity == nil {
		d.Missing = append(d.Missing, "pawn_activity")
		return ClassUnknown
	}
	for _, a := range s.Activity {
		switch a {
		case ActivityUnknown:
			d.Missing = append(d.Missing, "pawn_activity")
			return ClassUnknown
		case ActivityWork:
			return ClassProgressing
		case ActivityIdle:
			// An idle pawn beside a selected, unheld goal is the
			// starvation this epic is after: no blocker names itself.
			d.Missing = append(d.Missing, "blocker")
			return ClassUnknown
		}
	}
	return ClassNonWorkActivity
}

// Row renders a diagnosis for the report artifact. Absent facts are absent
// keys, never zero values standing in for evidence.
func (d Diagnosis) Row() map[string]any {
	row := map[string]any{
		"colony": d.Colony, "load": d.Load, "map": d.Map,
		"review_tick": d.ReviewTick, "goal": d.Goal, "class": d.Class,
		"selected": d.Selected, "committed": d.Committed,
	}
	if d.Method != "" {
		row["method"] = d.Method
	}
	if d.Action != "" {
		row["action"] = d.Action
	}
	if d.Reason != "" {
		row["reason"] = d.Reason
	}
	if d.Bottleneck != "" {
		row["bottleneck"] = d.Bottleneck
	}
	if d.Stage != "" {
		row["stage"] = d.Stage
	}
	if d.Unresolved {
		row["unresolved"] = true
	}
	if d.SlotIdle {
		row["slot_idle"] = true
	}
	if len(d.Held) > 0 {
		row["held"] = d.Held
	}
	if d.Blocker != "" {
		row["blocker"] = d.Blocker
	}
	if d.ShelterBeds != nil {
		row["shelter_beds"] = *d.ShelterBeds
	}
	if len(d.Activities) > 0 {
		activities := map[string]int{}
		for a, n := range d.Activities {
			activities[string(a)] = n
		}
		row["activities"] = activities
	}
	if len(d.Missing) > 0 {
		row["missing"] = d.Missing
	}
	return row
}
