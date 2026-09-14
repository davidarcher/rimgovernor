package domain

import "errors"

// MoodReliefNeed names EnsureMood-*'s three ordinary native need-relief jobs
// (food/rest/joy). Mirrors policy.MoodNeed and bridge.MoodReliefNeed's
// string values; domain cannot import policy or bridge, so the three are
// kept in sync by convention and converted at the boundary.
type MoodReliefNeed string

const (
	MoodReliefFood MoodReliefNeed = "food"
	MoodReliefRest MoodReliefNeed = "rest"
	MoodReliefJoy  MoodReliefNeed = "joy"
)

// MoodReliefJob mirrors bridge.MoodReliefExpectedJob: either the pawn's
// exact current job load ID, or explicitly idle. It is one of the two
// dispatch-fencing values recorded at commit time (alongside
// ExpectedScheduleDef) so a committed plan captures exactly what was
// validated then; policy.EvaluateMoodRelief re-checks both immediately
// before dispatch. Deliberately holds JobID as a plain int32, not
// bridge.MoodReliefExpectedJob's *int32: domain.Action participates in
// value equality (NewPlan's canonical comparison, EvaluateMoodRelief's
// commit-vs-canonical check), and a pointer field would make two logically
// identical actions compare unequal. Idle=true canonically pins JobID to 0.
type MoodReliefJob struct {
	Idle  bool
	JobID int32
}

func (j MoodReliefJob) valid() bool {
	return j.JobID >= 0 && (!j.Idle || j.JobID == 0)
}

// MoodRelief is explicit intent to send one already-selected undrafted pawn
// to one already-selected native need-relief job (EnsureMood-*). Native
// eligibility, current job state, mental state and player-order status are
// established at inspection, not here. ExpectedJob and ExpectedScheduleDef
// are the two dispatch-fencing values buildingruntime's
// moodReliefDispatchFacts (job-ID and schedule-def fencing, ported from
// boundary.JobEvidenceExpectedJob/ExpectedScheduleDef) already decoded at
// commit time.
type MoodRelief struct {
	pawn                PawnID
	need                MoodReliefNeed
	expectedJob         MoodReliefJob
	expectedScheduleDef string
}

func NewMoodRelief(pawn PawnID, need MoodReliefNeed, job MoodReliefJob, scheduleDef string) (MoodRelief, error) {
	if !validID(string(pawn)) || !validID(scheduleDef) {
		return MoodRelief{}, errors.New("mood relief requires a valid pawn and schedule definition")
	}
	switch need {
	case MoodReliefFood, MoodReliefRest, MoodReliefJoy:
	default:
		return MoodRelief{}, errors.New("invalid mood relief need")
	}
	if !job.valid() {
		return MoodRelief{}, errors.New("invalid mood relief expected job")
	}
	return MoodRelief{pawn: pawn, need: need, expectedJob: job, expectedScheduleDef: scheduleDef}, nil
}

func (m MoodRelief) Pawn() PawnID                { return m.pawn }
func (m MoodRelief) Need() MoodReliefNeed        { return m.need }
func (m MoodRelief) ExpectedJob() MoodReliefJob  { return m.expectedJob }
func (m MoodRelief) ExpectedScheduleDef() string { return m.expectedScheduleDef }

const MoodReliefAction ActionKind = "mood_relief"

func NewMoodReliefAction(id ActionID, relief MoodRelief) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewMoodRelief(relief.pawn, relief.need, relief.expectedJob, relief.expectedScheduleDef); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: MoodReliefAction, moodRelief: relief}, nil
}

func (a Action) MoodRelief() (MoodRelief, bool) { return a.moodRelief, a.kind == MoodReliefAction }
