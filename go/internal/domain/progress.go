package domain

import "errors"

type Stage string

const (
	Pending             Stage = "pending"
	Prepared            Stage = "prepared"
	Dispatched          Stage = "dispatched"
	AwaitingObservation Stage = "awaiting_observation"
	Completed           Stage = "completed"
	Unsuccessful        Stage = "unsuccessful"
	Cancelled           Stage = "cancelled"
)

type Receipt string

const (
	ReceiptAccepted Receipt = "accepted"
	ReceiptRefused  Receipt = "refused"
	ReceiptUnknown  Receipt = "unknown"
)

type Effect string

const (
	EffectUnknown   Effect = "unknown"
	EffectPending   Effect = "pending"
	EffectCompleted Effect = "completed"
	// EffectAbsent requires a complete inspection proving no order or effect remains.
	// Partial/truncated reads must report EffectUnknown.
	EffectAbsent       Effect = "absent"
	EffectUnsuccessful Effect = "unsuccessful"
)

type ObservationCausality string

// AfterDispatch asserts a native inspection correlated to this exact attempt,
// causally after dispatch. Equal game ticks alone cannot establish this ordering.
const AfterDispatch ObservationCausality = "after_dispatch"

type UnsuccessfulReason string

const (
	NativeFailure      UnsuccessfulReason = "native_failure"
	NativeCancelled    UnsuccessfulReason = "cancelled"
	NativeInterrupted  UnsuccessfulReason = "interrupted"
	NativeExpired      UnsuccessfulReason = "expired"
	TargetDead         UnsuccessfulReason = "target_dead"
	OutcomeNotAchieved UnsuccessfulReason = "outcome_not_achieved"
)

func (r UnsuccessfulReason) valid() bool {
	switch r {
	case NativeFailure, NativeCancelled, NativeInterrupted, NativeExpired, TargetDead, OutcomeNotAchieved:
		return true
	}
	return false
}

type Observation struct {
	Action             ActionID
	Attempt            AttemptID
	Snapshot           GenerationSnapshot
	Tick               Tick
	Effect             Effect
	Causality          ObservationCausality `json:",omitempty"`
	UnsuccessfulReason UnsuccessfulReason   `json:",omitempty"`
}
type ProgressView struct {
	Action             ActionID
	Attempt            AttemptID
	Plan               PlanID
	Revision           PlanRevision
	Stage              Stage
	Snapshot           GenerationSnapshot
	Tick               Tick
	Unresolved         bool
	Receipt            Fact[Receipt]
	Effect             Fact[Effect]
	UnsuccessfulReason Fact[UnsuccessfulReason]
}

// Progress transitions return a new value; failed transitions preserve the original.
// One orchestrator owns each action and durably records Prepared and Dispatched
// before invoking a native mutation. This package does not perform persistence.
type Progress struct{ view ProgressView }

func NewProgress(plan PlanSpec, action ActionID) (Progress, error) {
	for _, a := range plan.actions {
		if a.id == action {
			return Progress{ProgressView{Action: action, Plan: plan.id, Revision: plan.revision, Stage: Pending}}, nil
		}
	}
	return Progress{}, errors.New("action is not in plan")
}
func (p Progress) View() ProgressView { return p.view }
func (p Progress) Prepare(snapshot GenerationSnapshot, tick Tick) (Progress, error) {
	if p.view.Stage != Pending || p.view.Unresolved {
		return p, errors.New("action is not ready")
	}
	if err := snapshot.Validate(); err != nil {
		return p, err
	}
	if snapshot.Plan != p.view.Plan || snapshot.Revision != p.view.Revision || tick < p.view.Tick {
		return p, errors.New("stale plan or tick")
	}
	p.view.Stage, p.view.Snapshot, p.view.Tick = Prepared, snapshot, tick
	return p, nil
}
func (p Progress) MarkDispatched(current GenerationSnapshot, tick Tick) (Progress, error) {
	if p.view.Stage != Prepared || !p.view.Snapshot.Matches(current) || tick < p.view.Tick {
		return p, errors.New("dispatch requires current prepared authority")
	}
	if p.view.Attempt == ^AttemptID(0) {
		return p, errors.New("dispatch attempt identity exhausted")
	}
	p.view.Attempt++
	p.view.Stage, p.view.Unresolved, p.view.Tick = Dispatched, true, tick
	p.view.Receipt, p.view.Effect = Unknown[Receipt](), Unknown[Effect]()
	p.view.UnsuccessfulReason = Unknown[UnsuccessfulReason]()
	return p, nil
}
func (p Progress) RecordReceipt(attempt AttemptID, receipt Receipt) (Progress, error) {
	if attempt == 0 || attempt != p.view.Attempt {
		return p, errors.New("receipt belongs to a different dispatch attempt")
	}
	if p.view.Stage != Dispatched && !(p.view.Stage == Cancelled && p.view.Unresolved) {
		return p, errors.New("receipt requires dispatched action")
	}
	switch receipt {
	case ReceiptAccepted, ReceiptRefused, ReceiptUnknown:
	default:
		return p, errors.New("invalid receipt")
	}
	if _, known := p.view.Receipt.Value(); known {
		return p, errors.New("receipt already recorded")
	}
	p.view.Receipt = Known(receipt)
	if p.view.Stage != Cancelled {
		p.view.Stage = AwaitingObservation
	}
	return p, nil
}
func (p Progress) Cancel() (Progress, error) {
	if p.view.Stage == "" || p.view.Stage == Completed || p.view.Stage == Unsuccessful {
		return p, errors.New("cannot cancel this action")
	}
	p.view.Stage = Cancelled
	return p, nil
}

// Observe accepts read evidence under current authority, even after direction
// changes, but never attributes effects across colony/map/load changes.
// Terminal evidence must come from a complete native attempt-correlated inspection;
// the executor validates that boundary evidence regardless of tick distance.
func (p Progress) Observe(observation Observation, current GenerationSnapshot) (Progress, error) {
	if !p.view.Unresolved {
		return p, errors.New("no dispatched effect to observe")
	}
	if err := current.Validate(); err != nil {
		return p, err
	}
	if observation.Causality != "" && observation.Causality != AfterDispatch {
		return p, errors.New("invalid observation causality")
	}
	if observation.Action != p.view.Action || observation.Attempt == 0 || observation.Attempt != p.view.Attempt || !observation.Snapshot.Matches(current) || !p.view.Snapshot.sameWorld(current) || observation.Tick < p.view.Tick || observation.Tick == p.view.Tick && observation.Causality != AfterDispatch {
		return p, errors.New("stale or differently scoped observation")
	}
	switch observation.Effect {
	case EffectUnknown, EffectPending, EffectCompleted, EffectAbsent, EffectUnsuccessful:
	default:
		return p, errors.New("invalid observed effect")
	}
	if observation.Effect == EffectUnsuccessful {
		if !observation.UnsuccessfulReason.valid() {
			return p, errors.New("invalid unsuccessful reason")
		}
	} else if observation.UnsuccessfulReason != "" {
		return p, errors.New("unsuccessful reason requires unsuccessful effect")
	}
	p.view.Tick, p.view.Effect = observation.Tick, Known(observation.Effect)
	if observation.Effect == EffectUnsuccessful {
		p.view.UnsuccessfulReason = Known(observation.UnsuccessfulReason)
	}
	if observation.Effect == EffectCompleted || observation.Effect == EffectAbsent || observation.Effect == EffectUnsuccessful {
		p.view.Unresolved = false
		if p.view.Stage != Cancelled {
			if observation.Effect == EffectCompleted {
				p.view.Stage = Completed
			} else if observation.Effect == EffectUnsuccessful {
				p.view.Stage = Unsuccessful
			} else if p.view.Snapshot.Matches(current) {
				p.view.Stage = Pending
			} else {
				p.view.Stage = Cancelled
			}
		}
	}
	return p, nil
}
