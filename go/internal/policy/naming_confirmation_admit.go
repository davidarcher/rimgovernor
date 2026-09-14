package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// NamingRefused mirrors ResearchProjectClaimed: the native validators
// (IsValidName/IsValidSecondName) refused the exact suggestions this action
// targets, re-checked immediately before dispatch.
const NamingRefused Reason = "naming_refused"

// ConfirmColonyNamesFacts is the fresh native naming preview
// EvaluateConfirmColonyNames re-checks immediately before dispatch. Accepted
// is the native validators' current verdict on the exact targeted
// suggestions; Unknown can never authorize a write.
type ConfirmColonyNamesFacts struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Accepted domain.Fact[bool]
}

type ConfirmColonyNamesRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       ConfirmColonyNamesFacts
}

// EvaluateConfirmColonyNames re-validates one already-observed naming
// confirmation immediately before dispatch, the same shape
// EvaluateResearchSelect uses. Admission proves the native validators still
// accept the exact targeted suggestions right now; it does not prove the
// native write will be accepted or that the dialog is actually gone (that is
// observed later, off the routine's own needs() queue).
func EvaluateConfirmColonyNames(r ConfirmColonyNamesRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	value, ok := r.Action.NamingConfirmation()
	canonical, err := domain.NewNamingConfirmationAction(r.Action.ID(), value)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || r.Current.Direction == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.Tick < r.MinimumTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.Tick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	accepted, known := f.Accepted.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !accepted {
		return refuse(NamingRefused)
	}
	return DraftDecision{Admitted: true}
}
