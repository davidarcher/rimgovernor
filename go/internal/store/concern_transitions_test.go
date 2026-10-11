package store

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestConcernTransitionsRoundTripAndRetention(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "state.sqlite")
	s, err := Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	held := int64(20)
	cause := string(policy.CauseNoSpace)
	first := ConcernTransition{WallMS: 7, Tick: 100, Concern: "MaintainShelter", Verdict: "refused", Cause: cause, Subject: "bed"}
	second := ConcernTransition{WallMS: 8, Tick: 120, Concern: "MaintainShelter", Verdict: "admitted", PreviousKnown: true, Previous: cause, Held: &held}
	if err = s.RecordConcernTransitions(ctx, []ConcernTransition{first, second}); err != nil {
		t.Fatal(err)
	}
	got, err := s.ConcernTransitions(ctx)
	if err != nil || len(got) != 2 {
		t.Fatalf("rows %+v err %v", got, err)
	}
	if got[0].PreviousKnown || got[0].Held != nil || got[0].Subject != "bed" || got[0].Cause != cause {
		t.Fatalf("first: %+v", got[0])
	}
	if !got[1].PreviousKnown || got[1].Previous != cause || got[1].Held == nil || *got[1].Held != 20 || got[1].Cause != "" {
		t.Fatalf("second: %+v", got[1])
	}
	if got[0].Sequence >= got[1].Sequence {
		t.Fatalf("sequence not increasing: %+v", got)
	}

	// A process that does not own the store reads the same rows read-only.
	ro, err := ReadConcernTransitions(ctx, path)
	if err != nil || len(ro) != 2 || ro[1].Sequence != got[1].Sequence {
		t.Fatalf("read-only %+v err %v", ro, err)
	}
	if _, err = ReadConcernTransitions(ctx, filepath.Join(t.TempDir(), "absent.sqlite")); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("absent store: %v", err)
	}

	bad := first
	bad.Cause = "no site found"
	if err = s.RecordConcernTransitions(ctx, []ConcernTransition{bad}); err == nil {
		t.Fatal("free-text cause accepted")
	}

	batch := make([]ConcernTransition, ConcernTransitionRetention+10)
	for i := range batch {
		batch[i] = ConcernTransition{WallMS: int64(i), Tick: int64(i), Concern: "MaintainShelter", Verdict: "waiting", Cause: cause}
	}
	if err = s.RecordConcernTransitions(ctx, batch); err != nil {
		t.Fatal(err)
	}
	got, err = s.ConcernTransitions(ctx)
	if err != nil || len(got) != ConcernTransitionRetention {
		t.Fatalf("%d rows kept, want %d (err %v)", len(got), ConcernTransitionRetention, err)
	}
	if got[len(got)-1].Tick != int64(len(batch)-1) || got[0].Tick != 10 {
		t.Fatalf("kept the wrong end: first tick %d last tick %d", got[0].Tick, got[len(got)-1].Tick)
	}
}
