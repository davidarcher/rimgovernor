package store

import (
	"context"
	"encoding/json"
	"testing"
)

// Bindings are validated against the stored review itself, not a second
// derivation from empty facts (#1763): a review that assessed fewer goals
// than the empty-facts universe (a conditionally assessed goal left out)
// still loads, while a binding naming no routine goal does not.
func TestRoundsBindingsValidateAgainstTheStoredReview(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := roundsRequest()
	out := reviewRounds(t, s, &r)
	if len(out.Review.Standards) < 2 {
		t.Fatal("review binds too few goals")
	}
	store := func(review Rounds) {
		t.Helper()
		data, err := json.Marshal(review)
		if err != nil {
			t.Fatal(err)
		}
		if _, err = s.db.Exec("UPDATE rounds SET payload=? WHERE singleton=1", data); err != nil {
			t.Fatal(err)
		}
	}
	extra := out.Review
	extra.Standards = append(append([]RoundsStandard{}, out.Review.Standards...), RoundsStandard{Concern: "InventedNeed", Standard: "invented-goal"})
	store(extra)
	if _, err := s.LoadRounds(context.Background()); err == nil {
		t.Fatal("a binding naming no routine goal loaded")
	}
	short := out.Review
	short.Standards = append([]RoundsStandard{}, out.Review.Standards[1:]...)
	store(short)
	if _, err := s.LoadRounds(context.Background()); err != nil {
		t.Fatal("a review that assessed one goal fewer failed to load:", err)
	}
}
