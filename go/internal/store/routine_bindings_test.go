package store

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
)

// A stored review whose goal bindings do not match its assessments fails to
// load with the missing and the unexpected goal ids in the message (#1762).
func TestIncompleteRoutineBindingsNameTheGoals(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	out := reviewRoutine(t, s, &r)
	if len(out.Review.Goals) < 2 {
		t.Fatal("review binds too few goals")
	}
	store := func(review RoutineReview) {
		t.Helper()
		data, err := json.Marshal(review)
		if err != nil {
			t.Fatal(err)
		}
		if _, err = s.db.Exec("UPDATE routine_review SET payload=? WHERE singleton=1", data); err != nil {
			t.Fatal(err)
		}
	}
	extra := out.Review
	extra.Goals = append(append([]RoutineGoal{}, out.Review.Goals...), RoutineGoal{Need: "InventedNeed", Goal: "invented-goal"})
	store(extra)
	if _, err := s.LoadRoutineReview(context.Background()); err == nil || !strings.Contains(err.Error(), "unexpected [InventedNeed]") || !strings.Contains(err.Error(), "missing []") {
		t.Fatal("extra binding error:", err)
	}
	dropped := out.Review.Goals[0].Need
	short := out.Review
	short.Goals = append([]RoutineGoal{}, out.Review.Goals[1:]...)
	store(short)
	if _, err := s.LoadRoutineReview(context.Background()); err == nil || !strings.Contains(err.Error(), "missing ["+string(dropped)+"]") || !strings.Contains(err.Error(), "unexpected []") {
		t.Fatal("missing binding error:", err)
	}
}
