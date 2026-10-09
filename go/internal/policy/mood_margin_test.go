package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestMoodEntryAndRecoveryMargins(t *testing.T) {
	threshold := .3 // a variable: the review adds at run time, so constant folding would differ in the last bit
	drone := []MoodThought{{Def: PsychicDroneThought, Offset: -12}}
	cases := []struct {
		name   string
		mood   float64
		active bool // prior history
		mental bool
		drone  bool
		want   bool
	}{
		{"above entry", threshold + moodEntryMargin + .01, false, false, false, false},
		{"at entry", threshold + moodEntryMargin, false, false, false, true},
		{"below entry", threshold + moodEntryMargin - .01, false, false, false, true},
		{"at threshold", threshold, false, false, false, true},
		{"retained at recovery", threshold + moodRecoveryMargin, true, false, false, true},
		{"retained between", threshold + moodEntryMargin + .02, true, false, false, true},
		{"recovered above recovery", threshold + moodRecoveryMargin + .01, true, false, false, false},
		{"inactive between not entered", threshold + moodEntryMargin + .02, false, false, false, false},
		{"drone widens entry to its offset", threshold + .12, false, false, true, true},
		{"drone does not extend recovery", threshold + moodRecoveryMargin + .01, true, false, true, false},
		{"mental break regardless of mood", .95, false, true, false, true},
	}
	for _, c := range cases {
		p := moodPawn()
		p.Food = domain.Known(.8)
		p.Mood = domain.Known(c.mood)
		p.Threshold = domain.Known(threshold)
		p.Mental = domain.Known(c.mental)
		if c.drone {
			p.Thoughts = domain.Known(drone)
		}
		var prior MoodHistory
		if c.active {
			s := MoodState{Pawn: p, Active: true}
			s.Pawn.Mood = domain.Known(threshold)
			prior = MoodHistory{States: []MoodState{s}}
		}
		h := moodReview(t, p, prior)
		got := len(h.States) == 1 && h.States[0].Active
		if got != c.want {
			t.Errorf("%s: active=%v want %v (%v)", c.name, got, c.want, h)
		}
	}
}
