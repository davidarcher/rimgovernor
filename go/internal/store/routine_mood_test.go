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
func TestRoutineMoodDurableLifecycleAndRetirement(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	p := moodPerson()
	id := policy.MoodGoal(p.ID)
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{p})
	out := reviewRoutine(t, s, &r)
	first := routineGoal(t, out, id)
	if first.Goal.Need != domain.NeedDeficit || first.Goal.Priority != 2 {
		t.Fatal(first)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded.Mood, out.Review.Mood) {
		t.Fatal(loaded, err)
	}
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{})
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, id).Goal.Need != domain.NeedUnknown {
		t.Fatal(out)
	}
	r.Enabled = false
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, id).Goal.Status != domain.GoalSuspended || !out.Review.Mood.States[0].Active {
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
	if routineGoal(t, out, id).Goal.Need != domain.NeedUnknown {
		t.Fatal("Manual recovered missing pawn")
	}
	p.Mood = domain.Known(.8)
	p.Food = domain.Known(.5)
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{p})
	out = reviewRoutine(t, s, &r)
	recovered := routineGoal(t, out, id)
	if recovered.Goal.Need != domain.NeedRecovered || recovered.Goal.Status != domain.GoalSatisfied {
		t.Fatal(recovered)
	}
	p.Mood = domain.Known(.1)
	r.Facts.MoodPawns = domain.Known([]policy.MoodPawn{p})
	out = reviewRoutine(t, s, &r)
	renewed := routineGoal(t, out, id)
	if renewed.Goal.Epoch <= recovered.Goal.Epoch {
		t.Fatal("deficit did not renew")
	}
	if _, err = s.CancelGoal(ctx, renewed.Goal.ID, renewed.Revision); err != nil {
		t.Fatal(err)
	}
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, id).Goal.Status != domain.GoalCancelled {
		t.Fatal("cancellation overwritten")
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
	if len(out.Review.Goals) != 43 {
		t.Fatal("old dynamic binding retained", len(out.Review.Goals))
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
	if len(out.Review.Goals) != 299 {
		t.Fatal(len(out.Review.Goals))
	}
	if _, err := s.LoadRoutineReview(context.Background()); err != nil {
		t.Fatal(err)
	}
}

func TestRoutineMoodRejectsCorruptHistoryAndProposals(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"proposal", "duplicate", "inactive", "binding"} {
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
				v.Goals[len(v.Goals)-1].Need = policy.MoodGoal("other")
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

// A mental break is a priority-1 mood goal, but it is not an emergency: it
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
	if g := routineGoal(t, out, policy.MoodGoal(p.ID)); g.Goal.Priority != 1 || g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
	}
	if g := routineGoal(t, out, policy.MaintainWood); g.Goal.Status == domain.GoalSuspended {
		t.Fatal(g)
	}
}
