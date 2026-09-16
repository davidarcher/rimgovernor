package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MoodReliefPawnUnavailable mirrors WasteHaulerUnavailable: the pawn is
// dead, downed, occupied by a conflicting player order, or in a mental
// break -- any of which means the native RelieveNeed job cannot be issued
// to it right now, not that the underlying need itself has recovered.
const MoodReliefPawnUnavailable Reason = "mood_relief_pawn_unavailable"

// MoodReliefFencingStale names a refusal specific to this family: the two
// dispatch-fencing values (expected job, expected schedule def) committed
// at plan time no longer match a fresh native read. Unlike Waste's item
// existence, which is a simple presence fact, a stale expected-job or
// schedule-def would otherwise cause native to silently apply the relief
// order to the wrong in-flight job or timetable assignment.
const MoodReliefFencingStale Reason = "mood_relief_fencing_stale"

// MoodReliefPawnFacts describes the one already-selected undrafted
// candidate pawn immediately before dispatch. The planner has already
// chosen this pawn and need; EvaluateMoodRelief only re-validates the pair,
// including refreshing both dispatch-fencing values, immediately before
// dispatch -- mirroring WastePawnFacts's role for MaintainWaste.
type MoodReliefPawnFacts struct {
	Pawn                          domain.PawnID
	SnapshotToken                 string
	Dead, Downed, Drafted, Mental domain.Fact[bool]
	// ExpectedJob and ExpectedScheduleDef are freshly decoded (by
	// buildingruntime's moodReliefDispatchFacts) at inspection time, not
	// copied from the committed action: a stale value would otherwise let
	// this write land against whatever job or schedule assignment is
	// current now, rather than the one actually evaluated.
	ExpectedJob         domain.Fact[domain.MoodReliefJob]
	ExpectedScheduleDef domain.Fact[string]
}

type MoodReliefDispatchFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  MoodReliefPawnFacts
	NativeCanTry          domain.Fact[bool]
}

type MoodReliefDispatchRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       MoodReliefDispatchFacts
}

// EvaluateMoodRelief re-validates one already-selected pawn/need pair
// immediately before dispatch, mirroring EvaluateWaste's shape and
// discipline: admission proves eligibility now, not that the relief job
// will be issued, accepted or completed, and an unknown fact is never
// treated as recovery or eligibility.
func EvaluateMoodRelief(r MoodReliefDispatchRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	relief, ok := r.Action.MoodRelief()
	canonical, err := domain.NewMoodReliefAction(r.Action.ID(), relief)
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
	if f.PawnTick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Pawn.Pawn != relief.Pawn() || !validToken(f.Pawn.SnapshotToken) {
		return refuse(UnknownFacts)
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.Dead, f.Pawn.Downed, f.Pawn.Drafted, f.Pawn.Mental} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	dead, _ := f.Pawn.Dead.Value()
	downed, _ := f.Pawn.Downed.Value()
	drafted, _ := f.Pawn.Drafted.Value()
	mental, _ := f.Pawn.Mental.Value()
	if dead || downed {
		return refuse(MoodReliefPawnUnavailable)
	}
	if drafted || mental {
		return refuse(PlayerOrder)
	}
	job, jobKnown := f.Pawn.ExpectedJob.Value()
	scheduleDef, scheduleKnown := f.Pawn.ExpectedScheduleDef.Value()
	if !jobKnown || !scheduleKnown {
		return refuse(UnknownFacts)
	}
	if job != relief.ExpectedJob() || scheduleDef != relief.ExpectedScheduleDef() {
		return refuse(MoodReliefFencingStale)
	}
	nativeCanTry, known := f.NativeCanTry.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !nativeCanTry {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}
