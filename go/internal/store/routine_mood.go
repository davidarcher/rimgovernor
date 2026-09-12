package store

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Optional persisted values distinguish unknown from observed zero/false.
// Domain Fact deliberately has no serialization contract.
type RoutineMood struct{ States []RoutineMoodState }
type RoutineMoodState struct {
	Pawn                        RoutineMoodPawn
	Active, Missing, MentalRisk bool
	Causes                      []RoutineMoodCause
}
type RoutineMoodPawn struct {
	ID                                          policy.PawnID
	Mood, Threshold, Target, Food, Rest, Joy    *float64
	Mental, Dead, Downed, Drafted, PlayerForced *bool
}
type RoutineMoodCause struct {
	Need  policy.MoodNeed
	Level *float64
}
type RoutineMoodMethod struct {
	Pawn                     policy.PawnID
	Need                     policy.MoodNeed
	Reason                   policy.MoodMethodReason
	Target                   float64
	NeedBenefit, MoodBenefit *float64
}

func moodValue[T any](f domain.Fact[T]) *T {
	v, k := f.Value()
	if !k {
		return nil
	}
	return &v
}
func moodFact[T any](v *T) domain.Fact[T] {
	if v == nil {
		return domain.Unknown[T]()
	}
	return domain.Known(*v)
}
func (r RoutineReview) moodHistory() policy.MoodHistory {
	h := policy.MoodHistory{}
	if r.Mood == nil {
		return h
	}
	for _, s := range r.Mood.States {
		p := s.Pawn
		row := policy.MoodState{Pawn: policy.MoodPawn{ID: p.ID, Mood: moodFact(p.Mood), Threshold: moodFact(p.Threshold), Target: moodFact(p.Target), Food: moodFact(p.Food), Rest: moodFact(p.Rest), Joy: moodFact(p.Joy), Mental: moodFact(p.Mental), Dead: moodFact(p.Dead), Downed: moodFact(p.Downed), Drafted: moodFact(p.Drafted), PlayerForced: moodFact(p.PlayerForced)}, Active: s.Active, Missing: s.Missing, MentalRisk: s.MentalRisk}
		for _, c := range s.Causes {
			row.Causes = append(row.Causes, policy.MoodCause{Need: c.Need, Level: moodFact(c.Level)})
		}
		h.States = append(h.States, row)
	}
	return h
}
func moodRecord(h policy.MoodHistory) *RoutineMood {
	if len(h.States) == 0 {
		return nil
	}
	r := &RoutineMood{}
	for _, s := range h.States {
		p := s.Pawn
		row := RoutineMoodState{Pawn: RoutineMoodPawn{ID: p.ID, Mood: moodValue(p.Mood), Threshold: moodValue(p.Threshold), Target: moodValue(p.Target), Food: moodValue(p.Food), Rest: moodValue(p.Rest), Joy: moodValue(p.Joy), Mental: moodValue(p.Mental), Dead: moodValue(p.Dead), Downed: moodValue(p.Downed), Drafted: moodValue(p.Drafted), PlayerForced: moodValue(p.PlayerForced)}, Active: s.Active, Missing: s.Missing, MentalRisk: s.MentalRisk}
		for _, c := range s.Causes {
			row.Causes = append(row.Causes, RoutineMoodCause{Need: c.Need, Level: moodValue(c.Level)})
		}
		r.States = append(r.States, row)
	}
	return r
}
func moodProposals(h policy.MoodHistory) ([]RoutineMoodMethod, error) {
	var result []RoutineMoodMethod
	for _, state := range h.States {
		p, err := policy.SelectMoodMethod(state, nil)
		if err != nil {
			return nil, err
		}
		result = append(result, RoutineMoodMethod{Pawn: p.Pawn, Need: p.Need, Reason: p.Reason, Target: p.Target, NeedBenefit: moodValue(p.NeedBenefit), MoodBenefit: moodValue(p.MoodBenefit)})
	}
	return result, nil
}
