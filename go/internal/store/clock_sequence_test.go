package store

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"testing"
)

var clockTestLabels = struct {
	sync.Mutex
	ids map[ControllerSessionID]map[string]string
}{ids: make(map[ControllerSessionID]map[string]string)}

// Labels exist only in test setup; all calls into Store use canonical IDs.
func clockTestID(t *testing.T, s *Store, label string) string {
	t.Helper()
	ns, err := s.Identity(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	clockTestLabels.Lock()
	defer clockTestLabels.Unlock()
	labels := clockTestLabels.ids[ns]
	if labels == nil {
		labels = make(map[string]string)
		clockTestLabels.ids[ns] = labels
	}
	if id, ok := labels[label]; ok {
		return id
	}
	state, err := s.ReadClockSequence(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	id, err := state.NextRequestID()
	if err != nil {
		t.Fatal(err)
	}
	labels[label] = id
	return id
}

func TestClockSequenceAllocationReplayAndReopen(t *testing.T) {
	ctx := context.Background()
	s, path := fixture(t)
	state, err := s.ReadClockSequence(ctx)
	if err != nil || state.LastAllocated != 0 {
		t.Fatal(state, err)
	}
	id, err := state.NextRequestID()
	if err != nil {
		t.Fatal(err)
	}
	intent := clockIntent(id)
	intent.Key = "logical-window"
	v, created, err := s.PrepareClock(ctx, intent)
	if err != nil || !created {
		t.Fatal(v, err)
	}
	for n := uint64(2); n <= 12; n++ {
		next, _ := ClockRequestID(state.Namespace, n)
		if _, _, err = s.PrepareClock(ctx, clockIntent(next)); err != nil {
			t.Fatal(err)
		}
	}
	all, err := s.LoadClockAttempts(ctx, 20)
	if err != nil || len(all) != 12 {
		t.Fatal(err)
	}
	for i, v := range all {
		want, _ := ClockRequestID(state.Namespace, uint64(i+1))
		if v.Intent.RequestID != want {
			t.Fatal("lexical ordering", i)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err := s.PrepareClock(ctx, intent)
	if err != nil || created || replay.Intent.Key != "logical-window" {
		t.Fatal(replay, created, err)
	}
	intent.Key = "changed"
	if _, _, err = s.PrepareClock(ctx, intent); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	next, err := s.ReadClockSequence(ctx)
	if err != nil || next.LastAllocated != 12 {
		t.Fatal(next, err)
	}
	for _, bad := range []string{"start", "clock-" + string(state.Namespace) + "-01", "clock-" + string(state.Namespace) + "-0", "clock-" + string(state.Namespace) + "-18446744073709551616"} {
		if _, _, err = s.PrepareClock(ctx, clockIntent(bad)); err == nil {
			t.Fatal(bad)
		}
	}
	skip, _ := ClockRequestID(state.Namespace, 14)
	if _, _, err = s.PrepareClock(ctx, clockIntent(skip)); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err = (ClockSequenceState{Namespace: state.Namespace, LastAllocated: ^uint64(0)}).NextRequestID(); err == nil {
		t.Fatal("overflow")
	}
}
func TestClockSequenceConcurrentCASAndRollback(t *testing.T) {
	ctx := context.Background()
	s, path := fixture(t)
	other := open(t, path)
	state, err := s.ReadClockSequence(ctx)
	if err != nil {
		t.Fatal(err)
	}
	id, _ := state.NextRequestID()
	results := make(chan error, 2)
	for i, db := range []*Store{s, other} {
		go func(i int, db *Store) {
			in := clockIntent(id)
			in.Key = fmt.Sprint(i)
			_, _, err := db.PrepareClock(ctx, in)
			results <- err
		}(i, db)
	}
	success, conflicts := 0, 0
	for range 2 {
		err := <-results
		if err == nil {
			success++
		} else if errors.Is(err, ErrConflict) {
			conflicts++
		} else {
			t.Fatal(err)
		}
	}
	if success != 1 || conflicts != 1 {
		t.Fatal(success, conflicts)
	}
	if _, err = s.db.Exec("CREATE TRIGGER fail_sequence BEFORE UPDATE ON clock_sequence BEGIN SELECT RAISE(ABORT,'test'); END"); err != nil {
		t.Fatal(err)
	}
	next, _ := ClockRequestID(state.Namespace, 2)
	if _, _, err = s.PrepareClock(ctx, clockIntent(next)); err == nil {
		t.Fatal("rollback missing")
	}
	got, err := s.ReadClockSequence(ctx)
	if err != nil || got.LastAllocated != 1 {
		t.Fatal(got, err)
	}
	if _, err = s.LookupClockAttempt(ctx, next); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
}
func TestClockSequenceRetirementAndCorruption(t *testing.T) {
	for _, kind := range []string{"retired", "missing-pinned", "missing-live", "extra-row", "noncanonical"} {
		t.Run(kind, func(t *testing.T) {
			ctx := context.Background()
			s, path := fixture(t)
			state, err := s.ReadClockSequence(ctx)
			if err != nil {
				t.Fatal(err)
			}
			first, _ := ClockRequestID(state.Namespace, 1)
			second, _ := ClockRequestID(state.Namespace, 2)
			for _, id := range []string{first, second} {
				if _, _, err = s.PrepareClock(ctx, clockIntent(id)); err != nil {
					t.Fatal(err)
				}
			}
			tx, err := s.begin(ctx)
			if err != nil {
				t.Fatal(err)
			}
			head := clockSequenceHead{LastAllocated: 2, RetiredThrough: 2, Retained: []uint64{1}}
			if _, err = tx.Exec("DELETE FROM clock_attempts WHERE request_id=?", second); err != nil {
				t.Fatal(err)
			}
			if err = saveClockSequence(ctx, tx, head); err != nil {
				t.Fatal(err)
			}
			if err = tx.Commit(); err != nil {
				t.Fatal(err)
			}
			switch kind {
			case "retired":
				if _, _, err = s.PrepareClock(ctx, clockIntent(second)); !errors.Is(err, ErrRetired) {
					t.Fatal(err)
				}
				if _, created, err := s.PrepareClock(ctx, clockIntent(first)); err != nil || created {
					t.Fatal(created, err)
				}
				if err = s.Close(); err != nil {
					t.Fatal(err)
				}
				s = open(t, path)
				got, err := s.ReadClockSequence(ctx)
				if err != nil || got.RetiredThrough != 2 {
					t.Fatal(got, err)
				}
				return
			case "missing-pinned":
				_, err = s.db.Exec("DELETE FROM clock_attempts")
			case "missing-live":
				b, _ := json.Marshal(clockSequenceHead{LastAllocated: 2, Retained: []uint64{1}})
				_, err = s.db.Exec("UPDATE clock_sequence SET payload=?", b)
			case "extra-row":
				b, _ := json.Marshal(clockSequenceHead{LastAllocated: 2, RetiredThrough: 2, Retained: []uint64{}})
				_, err = s.db.Exec("UPDATE clock_sequence SET payload=?", b)
			case "noncanonical":
				_, err = s.db.Exec("UPDATE clock_sequence SET payload=CAST('{\"LastAllocated\":2,\"RetiredThrough\":2,\"Retained\":[1],\"unknown\":0}' AS BLOB)")
			}
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.ReadClockSequence(ctx); err == nil {
				t.Fatal("corrupt sequence accepted")
			}
			if _, err = s.LoadClockAttempts(ctx, 4096); err == nil {
				t.Fatal("corrupt catalog accepted")
			}
		})
	}
}
