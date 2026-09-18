package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// ExcavationUnsupported: native counterfactual roof support reports that
// removing this cell would leave roof outside the installed support radius
// of any remaining holder, or collapse is already pending.
const ExcavationUnsupported Reason = "excavation_unsupported"

// ExcavationGeometryChanged: the cell no longer holds the expected visible
// rock, holds a protected structure, or native mining refuses it.
const ExcavationGeometryChanged Reason = "excavation_geometry_changed"

// ExcavationSupport is the native site-level counterfactual support answer.
type ExcavationSupport uint8

const (
	ExcavationSupportUnknown ExcavationSupport = iota
	ExcavationSupportSupported
	ExcavationSupportUnsupported
)

type ExcavationFacts struct {
	Snapshot        domain.GenerationSnapshot
	ObservationTick domain.Tick
	// Fogged is known false only when native saw the cell unfogged.
	Fogged domain.Fact[bool]
	// Definition is the visible rock definition at the cell, known empty when
	// the cell holds no rock.
	Definition domain.Fact[string]
	Eligible   domain.Fact[bool]
	Support    ExcavationSupport
	// WorkerAvailable is a mining-capable colonist that can currently reach
	// the excavation's access cell; AccessReachable is that cell itself.
	WorkerAvailable, AccessReachable domain.Fact[bool]
}

type ExcavationRequest struct {
	Action   domain.Action
	Progress domain.Progress
	Current  domain.GenerationSnapshot
	Facts    ExcavationFacts
}

// EvaluateExcavation re-validates one already-selected excavation cell
// immediately before dispatch, the same shape EvaluateWallRemoval uses.
// Admission proves the exact visible rock, native eligibility, roof support
// and worker access still hold; it does not prove pawns will finish mining.
func EvaluateExcavation(r ExcavationRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	excavation, ok := r.Action.Excavation()
	canonical, err := domain.NewExcavationAction(r.Action.ID(), excavation)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.ObservationTick < v.Tick || (v.Stage == domain.Prepared && !sameWorld(v.Snapshot, r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	fogged, known := f.Fogged.Value()
	if !known || fogged {
		return refuse(UnknownFacts)
	}
	definition, known := f.Definition.Value()
	eligible, eligibleKnown := f.Eligible.Value()
	if !known || !eligibleKnown {
		return refuse(UnknownFacts)
	}
	// A cell the pawns already cleared (a designation the controller no
	// longer tracks, finished between planning and dispatch) is adopted as
	// done: dispatch records the cleared evidence instead of designating.
	if definition == "" {
		return DraftDecision{Admitted: true}
	}
	if definition != excavation.Definition() || !eligible {
		return refuse(ExcavationGeometryChanged)
	}
	switch f.Support {
	case ExcavationSupportSupported:
	case ExcavationSupportUnsupported:
		return refuse(ExcavationUnsupported)
	default:
		return refuse(UnknownFacts)
	}
	access, known := f.AccessReachable.Value()
	worker, workerKnown := f.WorkerAvailable.Value()
	if !known || !workerKnown {
		return refuse(UnknownFacts)
	}
	if !access || !worker {
		return refuse(NotReady)
	}
	return DraftDecision{Admitted: true}
}
