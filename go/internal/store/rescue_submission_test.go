package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rescueSubmissionRequest(t *testing.T, id string) RescueSubmissionRequest {
	t.Helper()
	rescue, err := domain.NewRescue("rescuer-1", "patient-1")
	if err != nil {
		t.Fatal(err)
	}
	return RescueSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Rescue: rescue}
}

func TestRescueSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "rescue-submission.db")
	s := open(t, path)
	request := rescueSubmissionRequest(t, "request")
	first, created, err := s.SubmitRescue(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitRescue(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*RescueSubmissionRequest){
		func(v *RescueSubmissionRequest) { v.World.Map = 1 },
		func(v *RescueSubmissionRequest) { v.World.Load = "other" },
		func(v *RescueSubmissionRequest) { v.World.Colony = "other" },
		func(v *RescueSubmissionRequest) { v.Rescue, _ = domain.NewRescue("other-rescuer", v.Rescue.Patient()) },
		func(v *RescueSubmissionRequest) { v.Rescue, _ = domain.NewRescue(v.Rescue.Rescuer(), "other-patient") },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitRescue(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitRescue(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupRescueSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestRescueSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "rescue-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_rescue_submission BEFORE INSERT ON rescue_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitRescue(ctx, rescueSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "rescue_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_rescue_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitRescue(ctx, rescueSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestRescueSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "rescue-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*RescueSubmissionRequest){
		func(v *RescueSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *RescueSubmissionRequest) { v.World.Map = -1 },
		func(v *RescueSubmissionRequest) { v.World.Load = "" },
		func(v *RescueSubmissionRequest) { v.Rescue = domain.Rescue{} },
	} {
		request := rescueSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitRescue(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupRescueSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and rescue submissions share one submission-identity namespace; a
// request id used by one kind must not silently resolve as the other, and
// the generic lookupAnySubmission dispatch must reach both.
func TestRescueSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "rescue-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	rescue := rescueSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitRescue(ctx, rescue); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	rescue.RequestID = "rescue-only"
	first, created, err := s.SubmitRescue(ctx, rescue)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "rescue-only"); err == nil {
		t.Fatal("building lookup accepted a rescue submission")
	}
}
