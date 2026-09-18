package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// BedAssignPawnUnavailable mirrors RecoveryServicePawnUnavailable: the pawn
// is dead or downed and cannot be given a fresh bed assignment right now.
const BedAssignPawnUnavailable Reason = "bed_assign_pawn_unavailable"

// BedAssignPawnFacts describes the one already-selected pawn
// ReviewSleeping's routine planner chose. Unlike RecoveryServicePawnFacts,
// bed assignment does not dispatch a WorkGiver job for the pawn to run right
// now (it only changes ownership), so no queued-job or player-forced-order
// conflict facts are needed.
type BedAssignPawnFacts struct {
	Pawn          domain.PawnID
	SnapshotToken string
	Dead, Downed  domain.Fact[bool]
}

type BedAssignFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  BedAssignPawnFacts
	BedSnapshotToken      string
	NativeCanTry          domain.Fact[bool]
}

type BedAssignRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       BedAssignFacts
}

// EvaluateBedAssign re-validates one already-selected pawn/bed pair
// immediately before dispatch, the same shape EvaluateRecoveryService uses.
// Admission proves eligibility now; it does not prove the assignment will be
// accepted or observed (that is checked later).
func EvaluateBedAssign(r BedAssignRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	assign, ok := r.Action.BedAssign()
	canonical, err := domain.NewBedAssignAction(r.Action.ID(), assign)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PawnTick < r.MinimumTick || f.PreviewTick < f.PawnTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PawnTick < v.Tick || (v.Stage == domain.Prepared && !sameWorld(v.Snapshot, r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Pawn.Pawn != assign.Pawn() || !validToken(f.Pawn.SnapshotToken) || !validToken(f.BedSnapshotToken) {
		return refuse(UnknownFacts)
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.Dead, f.Pawn.Downed} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	dead, _ := f.Pawn.Dead.Value()
	downed, _ := f.Pawn.Downed.Value()
	if dead || downed {
		return refuse(BedAssignPawnUnavailable)
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
