package store

import (
	"context"
	"encoding/json"
	"fmt"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func moodPerson() policy.MoodPawn {
	return policy.MoodPawn{ID: "pawn", Mood: domain.Known(.2), Threshold: domain.Known(.3), Food: domain.Known(.1), Rest: domain.Known(.8), Joy: domain.Known(.8), Mental: domain.Known(false), Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), PlayerForced: domain.Known(false)}
}

// moodIncident is the review's EnsureMood binding for pawn.
func moodIncident(r RoutineReviewResult, pawn policy.PawnID) (RoutineIncident, bool) {
	for _, b := range r.Review.SubjectIncidents(policy.EnsureMood) {
		if b.Subject == domain.PawnID(pawn) {
			return b, true
		}
	}
	return RoutineIncident{}, false
}

// A pawn's mood is an EnsureMood incident keyed by the pawn (#1078): it
// opens on a deficit, stays open while the pawn is unobserved, closes on
// recovery and the next deficit opens a new occurrence.
func TestRoutineMoodDurableLifecycleAndRetirement(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	p := moodPerson()
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{p})
	out := reviewRoutine(t, s, &r)
	for _, b := range out.Review.Goals {
		if b.Need == policy.EnsureMood {
			t.Fatal("mood filed a goal row")
		}
	}
	first, ok := moodIncident(out, p.ID)
	if !ok || first.Need != domain.NeedDeficit {
		t.Fatal(out.Review.Incidents)
	}
	if state, err := s.LoadIncident(ctx, first.Incident); err != nil || state.Incident.Priority != 2 || state.Incident.Subject != "pawn" {
		t.Fatal(state.Incident, err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded.Mood, out.Review.Mood) {
		t.Fatal(loaded, err)
	}
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{})
	out = reviewRoutine(t, s, &r)
	if b, ok := moodIncident(out, p.ID); !ok || b.Incident != first.Incident || b.Need != domain.NeedUnknown {
		t.Fatal("unobserved pawn closed its occurrence", out.Review.Incidents)
	}
	r.Enabled = false
	out = reviewRoutine(t, s, &r)
	if _, ok := moodIncident(out, p.ID); !ok || !out.Review.Mood.States[0].Active {
		t.Fatal(out)
	}
	s.Close()
	s = open(t, path)
	if _, err = s.LoadRoutineReview(ctx); err != nil {
		t.Fatal(err)
	}
	r.Enabled = true
	r.Current.Native++
	out = reviewRoutine(t, s, &r)
	if b, ok := moodIncident(out, p.ID); !ok || b.Need != domain.NeedUnknown {
		t.Fatal("Manual recovered missing pawn")
	}
	p.Mood = domain.Known(.8)
	p.Food = domain.Known(.5)
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{p})
	out = reviewRoutine(t, s, &r)
	if _, ok := moodIncident(out, p.ID); ok {
		t.Fatal("recovered mood kept its occurrence", out.Review.Incidents)
	}
	p.Mood = domain.Known(.1)
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{p})
	out = reviewRoutine(t, s, &r)
	if renewed, ok := moodIncident(out, p.ID); !ok || renewed.Incident == first.Incident {
		t.Fatal("deficit did not open a new occurrence", out.Review.Incidents)
	}
	p.Mood = domain.Known(.9)
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{p})
	out = reviewRoutine(t, s, &r)
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{})
	out = reviewRoutine(t, s, &r)
	if out.Review.Mood != nil {
		t.Fatal("inactive departed pawn retained")
	}
	if _, err = s.LoadRoutineReview(ctx); err != nil {
		t.Fatal("retired dynamic binding corrupt", err)
	}
}

func TestRoutineMoodWorldResetWhileDisabled(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{moodPerson()})
	reviewRoutine(t, s, &r)
	r.Enabled = false
	r.Current.Load = "new-load"
	out := reviewRoutine(t, s, &r)
	if out.Review.Mood != nil {
		t.Fatal("world history leaked")
	}
	if _, err := s.LoadRoutineReview(context.Background()); err != nil {
		t.Fatal(err)
	}
	r.Enabled = true
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{})
	out = reviewRoutine(t, s, &r)
	if len(out.Review.Incidents) != 0 {
		t.Fatal("old occurrence retained", out.Review.Incidents)
	}
}

func TestRoutineMoodCompleteBoundedCohort(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := routineRequest()
	rows := make([]policy.MoodPawn, 256)
	for i := range rows {
		rows[i] = moodPerson()
		rows[i].ID = policy.PawnID(fmt.Sprintf("pawn-%03d", i))
	}
	r.Facts.MoodPawns = domain.Known(rows)
	out := reviewRoutine(t, s, &r)
	if len(out.Review.Goals) != 45 || len(out.Review.SubjectIncidents(policy.EnsureMood)) != 256 {
		t.Fatal(len(out.Review.Goals), len(out.Review.Incidents))
	}
	if _, err := s.LoadRoutineReview(context.Background()); err != nil {
		t.Fatal(err)
	}
}

func TestRoutineMoodRejectsCorruptHistoryAndProposals(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"proposal", "duplicate", "inactive", "binding", "subject"} {
		t.Run(change, func(t *testing.T) {
			s := open(t, memoryPath(t))
			r := routineRequest()
			r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{moodPerson()})
			out := reviewRoutine(t, s, &r)
			v := out.Review
			switch change {
			case "proposal":
				v.MoodMethods[0].Need = policy.MoodJoy
			case "duplicate":
				v.Mood.States = append(v.Mood.States, v.Mood.States[0])
			case "inactive":
				v.Mood.States[0].Active = false
			case "binding":
				v.Incidents = append(v.Incidents, v.Incidents[0])
			case "subject":
				v.Incidents[0].Subject = "other"
			}
			data, err := json.Marshal(v)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE routine_review SET payload=? WHERE singleton=1", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadRoutineReview(context.Background()); err == nil {
				t.Fatal("corrupt mood accepted")
			}
		})
	}
}

// A mental break is a priority-1 mood incident, but it is not an emergency: it
// clears only as ticks pass, so suspending the other goals would leave the
// clock with no work and never let it end.
func TestRoutineMentalBreakDoesNotSuspendOtherGoals(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := routineRequest()
	p := moodPerson()
	p.Mental = domain.Known(true)
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{p})
	out := reviewRoutine(t, s, &r)
	if b, ok := moodIncident(out, p.ID); !ok || b.Need != domain.NeedDeficit || len(out.Review.Emergency) != 0 {
		t.Fatal(out.Review.Incidents, out.Review.Emergency)
	}
	if g := routineGoal(t, out, policy.MaintainResource); out.Review.Veto(g.Goal) != "" {
		t.Fatal(g)
	}
}
