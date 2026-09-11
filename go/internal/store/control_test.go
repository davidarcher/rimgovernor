package store

import (
	"context"
	"errors"
	"fmt"
	"path/filepath"
	"sync"
	"testing"
)

func controlRequest(t *testing.T, s *Store) ControlRequest {
	t.Helper()
	v, _, e := s.SubmitBuilding(context.Background(), submissionRequest(t, "building"))
	if e != nil {
		t.Fatal(e)
	}
	return ControlRequest{RequestID: "acquire", Kind: AcquireControl, World: v.Request.World, Plan: v.Plan, Revision: v.Revision}
}
func TestControlReplayRestartAndHistoricalCompletion(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "c.db")
	s := open(t, path)
	if _, e := s.CurrentControl(ctx); !errors.Is(e, ErrNotFound) {
		t.Fatal(e)
	}
	q := controlRequest(t, s)
	first, created, e := s.BeginControl(ctx, q)
	if e != nil || !created || first.Direction != 1 || first.Phase != PendingControl {
		t.Fatal(first, e)
	}
	s.Close()
	s = open(t, path)
	got, created, e := s.BeginControl(ctx, q)
	if e != nil || created || got != first {
		t.Fatal(got, e)
	}
	changed := q
	changed.World.Map++
	if _, _, e = s.BeginControl(ctx, changed); !errors.Is(e, ErrConflict) {
		t.Fatal(e)
	}
	manual := ControlRequest{RequestID: "manual", Kind: ManualControl, World: q.World}
	last, created, e := s.BeginControl(ctx, manual)
	if e != nil || !created || last.Direction != 2 {
		t.Fatal(last, e)
	}
	completed, e := s.CompleteControl(ctx, q.RequestID, GrantedControl, 5)
	if e != nil || completed.Phase != GrantedControl {
		t.Fatal(completed, e)
	}
	if current, e := s.CurrentControl(ctx); e != nil || current != last {
		t.Fatal(current, e)
	}
	if replay, e := s.CompleteControl(ctx, q.RequestID, GrantedControl, 5); e != nil || replay != completed {
		t.Fatal(e)
	}
	if _, e = s.CompleteControl(ctx, q.RequestID, RefusedControl, 0); !errors.Is(e, ErrConflict) {
		t.Fatal(e)
	}
	if _, e = s.CompleteControl(ctx, manual.RequestID, DisabledControl, 0); e != nil {
		t.Fatal(e)
	}
	if old, created, e := s.BeginControl(ctx, q); e != nil || created || old != completed {
		t.Fatal(old, e)
	}
}
func TestControlConcurrentCASAndRollback(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "c.db")
	a := open(t, path)
	q := controlRequest(t, a)
	b := open(t, path)
	var wg sync.WaitGroup
	results := make(chan error, 2)
	for i, s := range []*Store{a, b} {
		wg.Add(1)
		go func(i int, s *Store) {
			defer wg.Done()
			r := q
			r.RequestID = fmt.Sprint("acquire", i)
			_, _, e := s.BeginControl(ctx, r)
			results <- e
		}(i, s)
	}
	wg.Wait()
	close(results)
	ok, conflicts := 0, 0
	for e := range results {
		if e == nil {
			ok++
		} else if errors.Is(e, ErrConflict) {
			conflicts++
		} else {
			t.Fatal(e)
		}
	}
	if ok != 1 || conflicts != 1 {
		t.Fatal(ok, conflicts)
	}
	if _, e := a.db.Exec("CREATE TRIGGER control_fail BEFORE INSERT ON control_intents BEGIN SELECT RAISE(ABORT,'fixture'); END"); e != nil {
		t.Fatal(e)
	}
	manual := ControlRequest{RequestID: "failed", Kind: ManualControl, World: q.World}
	if _, _, e := a.BeginControl(ctx, manual); e == nil {
		t.Fatal("expected failure")
	}
	current, e := a.CurrentControl(ctx)
	if e != nil || current.Direction != 1 {
		t.Fatal(current, e)
	}
	if _, e = a.LookupControl(ctx, "failed"); !errors.Is(e, ErrNotFound) {
		t.Fatal(e)
	}
}
func TestControlValidationCapacityOverflowAndCorruption(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "c.db"))
	q := controlRequest(t, s)
	for _, change := range []func(*ControlRequest){func(r *ControlRequest) { r.World.Map++ }, func(r *ControlRequest) { r.Revision++ }, func(r *ControlRequest) { r.Plan = "missing" }, func(r *ControlRequest) { r.ExpectedDirection = 1 }, func(r *ControlRequest) { r.Kind = "bad" }, func(r *ControlRequest) { r.RequestID = "" }} {
		bad := q
		change(&bad)
		if _, _, e := s.BeginControl(ctx, bad); e == nil {
			t.Fatal(bad)
		}
	}
	first, _, e := s.BeginControl(ctx, q)
	if e != nil {
		t.Fatal(e)
	}
	for _, phase := range []ControlPhase{PendingControl, DisabledControl, "bad"} {
		if _, e = s.CompleteControl(ctx, q.RequestID, phase, 0); e == nil {
			t.Fatal(phase)
		}
	}
	if _, e = s.CompleteControl(ctx, q.RequestID, GrantedControl, 0); e == nil {
		t.Fatal("zero grant")
	}
	if _, e = s.CompleteControl(ctx, q.RequestID, UncertainControl, 2); e == nil {
		t.Fatal("uncertain generation")
	}
	// Fill the bounded history atomically without performing thousands of separate fsyncs.
	_, e = s.db.Exec(`WITH RECURSIVE n(x) AS (SELECT 2 UNION ALL SELECT x+1 FROM n WHERE x<4096) INSERT INTO control_intents SELECT 'manual-'||x,'manual','colony','load',0,'','0','0',CAST(x AS TEXT),'pending','0' FROM n`)
	if e != nil {
		t.Fatal(e)
	}
	manual := ControlRequest{RequestID: "overflow", Kind: ManualControl, World: q.World}
	if _, _, e = s.BeginControl(ctx, manual); !errors.Is(e, ErrCapacity) {
		t.Fatal(e)
	}
	if replay, created, e := s.BeginControl(ctx, q); e != nil || created || replay != first {
		t.Fatal(replay, e)
	}
	if _, e = s.db.Exec("DELETE FROM control_intents WHERE request_id!=?", q.RequestID); e != nil {
		t.Fatal(e)
	}
	if _, e = s.db.Exec("UPDATE control_intents SET direction='18446744073709551615'"); e != nil {
		t.Fatal(e)
	}
	if _, _, e = s.BeginControl(ctx, manual); e == nil {
		t.Fatal("direction overflow")
	}
	for _, assignment := range []string{"direction='01'", "direction='18446744073709551616'", "phase='bad'", "native_generation='1'", "kind='bad'"} {
		if _, e = s.db.Exec("UPDATE control_intents SET direction='1',phase='pending',native_generation='0',kind='acquire'"); e != nil {
			t.Fatal(e)
		}
		if _, e = s.db.Exec("UPDATE control_intents SET " + assignment); e != nil {
			t.Fatal(e)
		}
		if _, e = s.LookupControl(ctx, q.RequestID); e == nil {
			t.Fatal(assignment)
		}
	}
}
func TestControlRejectSchemaFour(t *testing.T) {
	path := filepath.Join(t.TempDir(), "old.db")
	s := open(t, path)
	if _, e := s.db.Exec("PRAGMA user_version=4"); e != nil {
		t.Fatal(e)
	}
	s.Close()
	if reopened, e := Open(context.Background(), path); e == nil {
		reopened.Close()
		t.Fatal("schema4 accepted")
	}
}
