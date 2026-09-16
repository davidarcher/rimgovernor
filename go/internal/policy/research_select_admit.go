package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ResearchProjectClaimed mirrors the shape of other NativeIneligible-style
// refusals: the native current research project is no longer empty, so a new
// SelectResearch would silently replace a player or prior selection instead
// of ever being issued blind.
const ResearchProjectClaimed Reason = "research_project_claimed"

// ResearchSelectFacts is the fresh native research state EvaluateResearchSelect
// re-checks immediately before dispatch. Current is the native "current
// project" defName, empty meaning no project is selected; Unknown can never
// authorize a write that would silently replace player research.
type ResearchSelectFacts struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Current  domain.Fact[string]
}

type ResearchSelectRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       ResearchSelectFacts
}

// EvaluateResearchSelect re-validates one already-queued project selection
// immediately before dispatch, the same shape EvaluateGearReplace uses.
// Admission proves the native project slot is still empty right now; it does
// not prove the native write will be accepted or that research subsequently
// completes (that is observed later, off the routine's own needs() queue).
func EvaluateResearchSelect(r ResearchSelectRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	value, ok := r.Action.ResearchSelect()
	canonical, err := domain.NewResearchSelectAction(r.Action.ID(), value)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.Tick < r.MinimumTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.Tick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	current, known := f.Current.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if current != "" {
		return refuse(ResearchProjectClaimed)
	}
	return DraftDecision{Admitted: true}
}
