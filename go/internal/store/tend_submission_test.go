package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func tendSubmissionRequest(t *testing.T, id string) TendSubmissionRequest {
	t.Helper()
	tend, err := domain.NewTend("doctor-1", "patient-1")
	if err != nil {
		t.Fatal(err)
	}
	return TendSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Tend: tend}
}

func TestTendSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "tend-submission.db")
	s := open(t, path)
	request := tendSubmissionRequest(t, "request")
	first, created, err := s.SubmitTend(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitTend(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*TendSubmissionRequest){
		func(v *TendSubmissionRequest) { v.World.Map = 1 },
		func(v *TendSubmissionRequest) { v.World.Load = "other" },
		func(v *TendSubmissionRequest) { v.World.Colony = "other" },
		func(v *TendSubmissionRequest) { v.Tend, _ = domain.NewTend("other-doctor", v.Tend.Patient()) },
		func(v *TendSubmissionRequest) { v.Tend, _ = domain.NewTend(v.Tend.Doctor(), "other-patient") },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitTend(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitTend(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupTendSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestTendSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "tend-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_tend_submission BEFORE INSERT ON tend_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitTend(ctx, tendSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "tend_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_tend_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitTend(ctx, tendSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestTendSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "tend-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*TendSubmissionRequest){
		func(v *TendSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *TendSubmissionRequest) { v.World.Map = -1 },
		func(v *TendSubmissionRequest) { v.World.Load = "" },
		func(v *TendSubmissionRequest) { v.Tend = domain.Tend{} },
	} {
		request := tendSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitTend(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupTendSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and tend submissions share one submission-identity namespace; a
// request id used by one kind must not silently resolve as the other, and
// the generic lookupAnySubmission dispatch must reach both.
func TestTendSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "tend-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	tend := tendSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitTend(ctx, tend); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	tend.RequestID = "tend-only"
	first, created, err := s.SubmitTend(ctx, tend)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "tend-only"); err == nil {
		t.Fatal("building lookup accepted a tend submission")
	}
}
