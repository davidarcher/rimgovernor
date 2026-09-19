package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HomeCoverageGeometryChanged is the home-coverage target blocker:
// the target's freshly observed shape no longer matches the shape the
// planner selected against, so the intended cells may have moved.
const HomeCoverageGeometryChanged Reason = "home_coverage_geometry_changed"

// HomeCoverageExcluded remains decodable in retained pre-autonomy histories.
// New Home coverage decisions never emit this reason.
const HomeCoverageExcluded Reason = "home_coverage_excluded"

type HomeCoverageFacts struct {
	Snapshot                     domain.GenerationSnapshot
	ObservationTick, PreviewTick domain.Tick
	CurrentShape                 domain.Fact[string]
	Revision                     domain.Fact[int64]
	Excluded                     domain.Fact[int64]
	Missing                      domain.Fact[int64]
	NativeCanTry                 domain.Fact[bool]
}

type HomeCoverageRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       HomeCoverageFacts
}

// EvaluateHomeCoverage re-validates one already-selected target/shape pair
// immediately before dispatch, the same shape EvaluateBedAssign uses.
// Admission proves eligibility now; it does not prove the extension will be
// accepted or observed (that is checked later).
func EvaluateHomeCoverage(r HomeCoverageRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	coverage, ok := r.Action.HomeCoverage()
	canonical, err := domain.NewHomeCoverageAction(r.Action.ID(), coverage)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	// The admission anchors on the preview tick, the inspection's one live
	// read; the pawn row may come from the step's fact cache up to the
	// planning tolerance behind it under a running window (#306, #323).
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PreviewTick < r.MinimumTick || !f.PreviewTick.FreshFor(f.ObservationTick) {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PreviewTick < v.Tick || (v.Stage == domain.Prepared && !sameWorld(v.Snapshot, r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	shape, known := f.CurrentShape.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if shape != coverage.Shape() {
		return refuse(HomeCoverageGeometryChanged)
	}
	for _, fact := range []domain.Fact[int64]{f.Excluded, f.Missing, f.Revision} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	excluded, _ := f.Excluded.Value()
	missing, _ := f.Missing.Value()
	revision, _ := f.Revision.Value()
	if excluded < 0 || missing < 0 || excluded > missing || revision < 0 {
		return refuse(UnknownFacts)
	}
	if missing == 0 {
		return refuse(NotReady)
	}
	eligible, known := f.NativeCanTry.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !eligible {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}
