package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func recoveryServiceSubmissionRequest(t *testing.T, id string) RecoveryServiceSubmissionRequest {
	t.Helper()
	service, err := domain.NewRecoveryService("pawn-1", "thing-1", domain.RecoveryServiceRepair)
	if err != nil {
		t.Fatal(err)
	}
	return RecoveryServiceSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Service: service}
}

func TestRecoveryServiceSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "recovery-service-submission.db")
	s := open(t, path)
	request := recoveryServiceSubmissionRequest(t, "request")
	first, created, err := s.SubmitRecoveryService(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitRecoveryService(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*RecoveryServiceSubmissionRequest){
		func(v *RecoveryServiceSubmissionRequest) { v.World.Map = 1 },
		func(v *RecoveryServiceSubmissionRequest) { v.World.Load = "other" },
		func(v *RecoveryServiceSubmissionRequest) { v.World.Colony = "other" },
		func(v *RecoveryServiceSubmissionRequest) {
			v.Service, _ = domain.NewRecoveryService("other-pawn", "thing-1", domain.RecoveryServiceRepair)
		},
		func(v *RecoveryServiceSubmissionRequest) {
			v.Service, _ = domain.NewRecoveryService("pawn-1", "thing-1", domain.RecoveryServiceRefuel)
		},
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitRecoveryService(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitRecoveryService(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupRecoveryServiceSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestRecoveryServiceSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "recovery-service-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_recovery_service_submission BEFORE INSERT ON recovery_service_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitRecoveryService(ctx, recoveryServiceSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "recovery_service_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_recovery_service_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitRecoveryService(ctx, recoveryServiceSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestRecoveryServiceSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "recovery-service-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*RecoveryServiceSubmissionRequest){
		func(v *RecoveryServiceSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *RecoveryServiceSubmissionRequest) { v.World.Map = -1 },
		func(v *RecoveryServiceSubmissionRequest) { v.World.Load = "" },
		func(v *RecoveryServiceSubmissionRequest) { v.Service = domain.RecoveryService{} },
	} {
		request := recoveryServiceSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitRecoveryService(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupRecoveryServiceSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and recovery service submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestRecoveryServiceSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "recovery-service-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	service := recoveryServiceSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitRecoveryService(ctx, service); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	service.RequestID = "recovery-service-only"
	first, created, err := s.SubmitRecoveryService(ctx, service)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "recovery-service-only"); err == nil {
		t.Fatal("building lookup accepted a recovery service submission")
	}
}
