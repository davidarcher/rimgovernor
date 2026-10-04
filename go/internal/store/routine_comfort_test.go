package store

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// basicComfortProvided is EnsureComfort's basic phase met (reachable
// seat and recreation, variety known), so its ranked phase decides.
func basicComfortProvided() domain.Fact[policy.ComfortObservation] {
	v := comfortCensus(false)
	v.Recreation[0].Kind = "Dexterity"
	v.Joy = &policy.RecreationCensus{Kinds: []string{"Dexterity"}}
	return domain.Known(v)
}

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

func TestRecreationMaintenanceAssessmentSurvivesJournalRead(t *testing.T) {
	s := open(t, memoryPath(t))
	r := routineRequest()
	v := comfortCensus(false)
	v.Recreation[0].Kind = "Dexterity"
	v.Joy = &policy.RecreationCensus{Kinds: []string{"Dexterity"}, Pawns: []policy.JoyTolerance{{Pawn: "pawn", Tolerance: []float64{.4}, Bored: []bool{true}}}}
	r.Facts.Comfort = domain.Known(comfortCensus(true)) // the ranked phase met: variety alone decides
	r.Facts.BasicComfort = domain.Known(v)
	out := reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.EnsureComfort).Standard.Finding != domain.FindingUnmet {
		t.Fatal("variety not persisted as a deficit")
	}
	// Loading and reviewing again traverses Rounds's optional-goal
	// validation: this goal can be foothold or maintenance in the same save.
	out = reviewRoutine(t, s, &r)
	row := developmentRow(t, out.Review, policy.EnsureComfort)
	if row.Goal != policy.EnsureComfort {
		t.Fatal(row)
	}
	v.Joy.Pawns[0].Bored[0] = false
	r.Facts.BasicComfort = domain.Known(v)
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.EnsureComfort).Standard.Finding != domain.FindingMet {
		t.Fatal("fresh native evidence did not recover variety")
	}
}

func TestRoutineComfortUseSurvivesRestartManualButNotReplacement(t *testing.T) {
	t.Parallel()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	r.Facts.BasicComfort = basicComfortProvided()
	r.Facts.Comfort = domain.Known(comfortCensus(true))
	out := reviewRoutine(t, s, &r)
	history := out.Review.Comfort
	if routineGoal(t, out, policy.EnsureComfort).Standard.Finding != domain.FindingMet || history.Dining.Facility != "chair" {
		t.Fatal(out)
	}
	s.Close()
	s = open(t, path)
	r.Facts.Comfort = domain.Known(comfortCensus(false))
	out = reviewRoutine(t, s, &r)
	if out.Review.Comfort != history || routineGoal(t, out, policy.EnsureComfort).Standard.Status != domain.StandardSettled {
		t.Fatal(out)
	}
	r.Enabled = false
	reviewRoutine(t, s, &r)
	r.Enabled = true
	r.Current.Native++
	r.Facts.Comfort = domain.Unknown[policy.ComfortObservation]()
	r.Facts.ComfortRecovered = domain.Known(true)
	out = reviewRoutine(t, s, &r)
	if out.Review.Comfort != history || routineGoal(t, out, policy.EnsureComfort).Standard.Finding != domain.FindingUnclear {
		t.Fatal("unknown census replaced by aggregate", routineGoal(t, out, policy.EnsureComfort).Standard, out.Review.Latches.Comfort)
	}
	replacement := comfortCensus(false)
	replacement.Dining[0].ID = "replacement-chair"
	r.Facts.Comfort = domain.Known(replacement)
	out = reviewRoutine(t, s, &r)
	if out.Review.Comfort != history || routineGoal(t, out, policy.EnsureComfort).Standard.Finding != domain.FindingUnmet {
		t.Fatal("replacement inherited use", out)
	}
	replacement.Dining[0].Users = replacement.People
	r.Facts.Comfort = domain.Known(replacement)
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.EnsureComfort).Standard.Finding != domain.FindingMet || out.Review.Comfort.Dining.Facility != "replacement-chair" {
		t.Fatal(out)
	}
}

func TestRoutineComfortWorldResetAndInvalidDisabledHistory(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"world", "rewind"} {
		t.Run(change, func(t *testing.T) {
			s := open(t, memoryPath(t))
			r := routineRequest()
			r.Facts.BasicComfort = basicComfortProvided()
			r.Facts.Comfort = domain.Known(comfortCensus(true))
			reviewRoutine(t, s, &r)
			if change == "world" {
				r.Current.Load = "other"
			} else {
				r.Tick = 1
			}
			r.Facts.Comfort = domain.Known(comfortCensus(false))
			out := reviewRoutine(t, s, &r)
			if out.Review.Comfort != (policy.ComfortHistory{}) || routineGoal(t, out, policy.EnsureComfort).Standard.Finding != domain.FindingUnmet {
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
			if _, err = s.LoadRounds(context.Background()); err == nil {
				t.Fatal("accepted future use in disabled history")
			}
		})
	}
}
