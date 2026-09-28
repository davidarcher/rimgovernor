package domain

import "errors"

// MoodReliefNeed names EnsureMood's three ordinary native need-relief jobs
// (food/rest/joy). Mirrors policy.MoodNeed's string values; domain cannot
// import policy, so they are kept in sync by convention and converted at
// the boundary.
type MoodReliefNeed string

const (
	MoodReliefFood MoodReliefNeed = "food"
	MoodReliefRest MoodReliefNeed = "rest"
	MoodReliefJoy  MoodReliefNeed = "joy"
)

// MoodRelief is explicit intent to send one already-selected undrafted pawn
// to one ordinary native need-relief job (EnsureMood). Native checks the
// pawn's eligibility, current job, timetable and need live when it applies
// the intent.
type MoodRelief struct {
	pawn PawnID
	need MoodReliefNeed
}

func NewMoodRelief(pawn PawnID, need MoodReliefNeed) (MoodRelief, error) {
	if !validID(string(pawn)) {
		return MoodRelief{}, errors.New("mood relief requires a valid pawn")
	}
	switch need {
	case MoodReliefFood, MoodReliefRest, MoodReliefJoy:
	default:
		return MoodRelief{}, errors.New("invalid mood relief need")
	}
	return MoodRelief{pawn: pawn, need: need}, nil
}

func (m MoodRelief) Pawn() PawnID         { return m.pawn }
func (m MoodRelief) Need() MoodReliefNeed { return m.need }

const MoodReliefAction ActionKind = "mood_relief"

func NewMoodReliefAction(id ActionID, relief MoodRelief) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewMoodRelief(relief.pawn, relief.need); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: MoodReliefAction, moodRelief: relief}, nil
}

func (a Action) MoodRelief() (MoodRelief, bool) { return a.moodRelief, a.kind == MoodReliefAction }
