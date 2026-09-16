package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func comfortCensus(using bool) policy.ComfortObservation {
	people := []policy.PawnID{"pawn"}
	var users []policy.PawnID
	if using {
		users = people
	}
	return policy.ComfortObservation{People: people,
		Dining:     []policy.ComfortFacility{{ID: "chair", AccessibleTo: people, Users: users}},
		Recreation: []policy.ComfortFacility{{ID: "hoop", AccessibleTo: people, Users: users}}}
}

func TestRoutineComfortUseSurvivesRestartManualButNotReplacement(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "comfort.db")
	s := open(t, path)
	r := routineRequest()
	r.Facts.Comfort = domain.Known(comfortCensus(true))
	out := reviewRoutine(t, s, &r)
	history := out.Review.Comfort
	if routineGoal(t, out, policy.EnsureComfort).Goal.Need != domain.NeedRecovered || history.Dining.Facility != "chair" {
		t.Fatal(out)
	}
	s.Close()
	s = open(t, path)
	r.Facts.Comfort = domain.Known(comfortCensus(false))
	out = reviewRoutine(t, s, &r)
	if out.Review.Comfort != history || routineGoal(t, out, policy.EnsureComfort).Goal.Status != domain.GoalSatisfied {
		t.Fatal(out)
	}
	r.Enabled = false
	reviewRoutine(t, s, &r)
	r.Enabled = true
	r.Current.Native++
	r.Facts.Comfort = domain.Unknown[policy.ComfortObservation]()
	r.Facts.ComfortRecovered = domain.Known(true)
	out = reviewRoutine(t, s, &r)
	if out.Review.Comfort != history || routineGoal(t, out, policy.EnsureComfort).Goal.Need != domain.NeedUnknown {
		t.Fatal("unknown census replaced by aggregate", out)
	}
	replacement := comfortCensus(false)
	replacement.Dining[0].ID = "replacement-chair"
	r.Facts.Comfort = domain.Known(replacement)
	out = reviewRoutine(t, s, &r)
	if out.Review.Comfort != history || routineGoal(t, out, policy.EnsureComfort).Goal.Need != domain.NeedDeficit {
		t.Fatal("replacement inherited use", out)
	}
	replacement.Dining[0].Users = replacement.People
	r.Facts.Comfort = domain.Known(replacement)
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.EnsureComfort).Goal.Need != domain.NeedRecovered || out.Review.Comfort.Dining.Facility != "replacement-chair" {
		t.Fatal(out)
	}
}

func TestRoutineComfortWorldResetAndInvalidDisabledHistory(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"world", "rewind"} {
		t.Run(change, func(t *testing.T) {
			s := open(t, filepath.Join(t.TempDir(), "comfort.db"))
			r := routineRequest()
			r.Facts.Comfort = domain.Known(comfortCensus(true))
			reviewRoutine(t, s, &r)
			if change == "world" {
				r.Current.Load = "other"
			} else {
				r.Tick = 1
			}
			r.Facts.Comfort = domain.Known(comfortCensus(false))
			out := reviewRoutine(t, s, &r)
			if out.Review.Comfort != (policy.ComfortHistory{}) || routineGoal(t, out, policy.EnsureComfort).Goal.Need != domain.NeedDeficit {
				t.Fatal(out)
			}
			r.Enabled = false
			out = reviewRoutine(t, s, &r)
			out.Review.Comfort.Dining = policy.ComfortUse{Facility: "chair", Tick: out.Review.Tick + 1}
			data, err := json.Marshal(out.Review)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE routine_review SET payload=? WHERE singleton=1", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadRoutineReview(context.Background()); err == nil {
				t.Fatal("accepted future use in disabled history")
			}
		})
	}
}
