package store

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"path/filepath"
	"sync"
	"testing"
)

func submissionRequest(t *testing.T, id string) SubmissionRequest {
	t.Helper()
	b, err := domain.NewBuilding("Wall", domain.Cell{X: 0, Z: 2}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	return SubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Building: b}
}
func TestSubmissionReplayConflictAndReopen(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "submission.db")
	s := open(t, path)
	request := submissionRequest(t, "request")
	first, created, err := s.SubmitBuilding(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitBuilding(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*SubmissionRequest){func(v *SubmissionRequest) { v.World.Map = 1 }, func(v *SubmissionRequest) { v.World.Load = "other" }, func(v *SubmissionRequest) { v.World.Colony = "other" }, func(v *SubmissionRequest) {
		v.Building, _ = domain.NewBuilding("Door", v.Building.Cell(), v.Building.Rotation(), v.Building.Stuff())
	}, func(v *SubmissionRequest) {
		v.Building, _ = domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 2}, domain.North, "WoodLog")
	}, func(v *SubmissionRequest) {
		v.Building, _ = domain.NewBuilding("Wall", v.Building.Cell(), domain.East, "WoodLog")
	}, func(v *SubmissionRequest) {
		v.Building, _ = domain.NewBuilding("Wall", v.Building.Cell(), domain.North, "")
	}} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitBuilding(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitBuilding(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}
func TestSubmissionAtomicFailure(t *testing.T) {
	s := open(t, filepath.Join(t.TempDir(), "atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_submission BEFORE INSERT ON building_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitBuilding(ctx, submissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "building_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitBuilding(ctx, submissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}
func TestConcurrentSubmissionAcrossConnections(t *testing.T) {
	path := filepath.Join(t.TempDir(), "concurrent.db")
	one := open(t, path)
	two := open(t, path)
	request := submissionRequest(t, "same")
	type outcome struct {
		value   Submission
		created bool
		err     error
	}
	results := make(chan outcome, 16)
	var start sync.WaitGroup
	start.Add(1)
	for i := 0; i < 16; i++ {
		s := one
		if i%2 != 0 {
			s = two
		}
		go func() {
			start.Wait()
			v, c, e := s.SubmitBuilding(context.Background(), request)
			results <- outcome{v, c, e}
		}()
	}
	start.Done()
	var first Submission
	created := 0
	for i := 0; i < 16; i++ {
		out := <-results
		if out.err != nil {
			t.Fatal(out.err)
		}
		if i == 0 {
			first = out.value
		}
		if out.value != first {
			t.Fatal("duplicate request created different identities")
		}
		if out.created {
			created++
		}
	}
	if created != 1 {
		t.Fatal(created)
	}
}
func TestSubmissionCapacityPreservesReplay(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "capacity.db"))
	request := submissionRequest(t, "retained")
	first, _, err := s.SubmitBuilding(ctx, request)
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 255; i++ {
		spec, err := domain.NewPlan(domain.PlanID(fmt.Sprintf("plan-%d", i)), 1, nil)
		if err != nil {
			t.Fatal(err)
		}
		if err = s.CreatePlan(ctx, spec); err != nil {
			t.Fatal(err)
		}
	}
	replay, created, err := s.SubmitBuilding(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("full catalog broke replay", err)
	}
	request.RequestID = "new"
	if _, _, err = s.SubmitBuilding(ctx, request); !errors.Is(err, ErrCapacity) {
		t.Fatal(err)
	}
	spec, _ := domain.NewPlan("overflow", 1, nil)
	if err = s.CreatePlan(ctx, spec); !errors.Is(err, ErrCapacity) {
		t.Fatal("direct create bypassed capacity", err)
	}
	if _, err = s.LookupSubmission(ctx, "new"); !errors.Is(err, ErrNotFound) {
		t.Fatal("overflow persisted intent", err)
	}
}
func TestSubmissionValidationAndOldSchemaRejected(t *testing.T) {
	s := open(t, filepath.Join(t.TempDir(), "validation.db"))
	ctx := context.Background()
	for _, change := range []func(*SubmissionRequest){func(v *SubmissionRequest) { v.RequestID = "bad\x00id" }, func(v *SubmissionRequest) { v.World.Map = -1 }, func(v *SubmissionRequest) { v.World.Load = "" }, func(v *SubmissionRequest) { v.Building = domain.Building{} }} {
		request := submissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitBuilding(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
	path := filepath.Join(t.TempDir(), "old.db")
	old := open(t, path)
	if _, err := old.db.Exec("PRAGMA user_version=3"); err != nil {
		t.Fatal(err)
	}
	old.Close()
	if reopened, err := Open(ctx, path); err == nil {
		reopened.Close()
		t.Fatal("old schema silently migrated")
	}
}
