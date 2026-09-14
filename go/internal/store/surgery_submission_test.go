package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func surgerySubmissionRequest(t *testing.T, id string) SurgerySubmissionRequest {
	t.Helper()
	surgery, err := domain.NewSurgery("patient-1", "recipe-1", -1)
	if err != nil {
		t.Fatal(err)
	}
	return SurgerySubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Surgery: surgery}
}

func TestSurgerySubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "surgery-submission.db")
	s := open(t, path)
	request := surgerySubmissionRequest(t, "request")
	first, created, err := s.SubmitSurgery(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitSurgery(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*SurgerySubmissionRequest){
		func(v *SurgerySubmissionRequest) { v.World.Map = 1 },
		func(v *SurgerySubmissionRequest) { v.World.Load = "other" },
		func(v *SurgerySubmissionRequest) { v.World.Colony = "other" },
		func(v *SurgerySubmissionRequest) { v.Surgery, _ = domain.NewSurgery("other-patient", "recipe-1", -1) },
		func(v *SurgerySubmissionRequest) { v.Surgery, _ = domain.NewSurgery("patient-1", "recipe-1", 3) },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitSurgery(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitSurgery(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupSurgerySubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestSurgerySubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "surgery-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_surgery_submission BEFORE INSERT ON surgery_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitSurgery(ctx, surgerySubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "surgery_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_surgery_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitSurgery(ctx, surgerySubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestSurgerySubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "surgery-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*SurgerySubmissionRequest){
		func(v *SurgerySubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *SurgerySubmissionRequest) { v.World.Map = -1 },
		func(v *SurgerySubmissionRequest) { v.World.Load = "" },
		func(v *SurgerySubmissionRequest) { v.Surgery = domain.Surgery{} },
	} {
		request := surgerySubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitSurgery(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupSurgerySubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and surgery submissions share one submission-identity namespace; a
// request id used by one kind must not silently resolve as the other, and
// the generic lookupAnySubmission dispatch must reach both.
func TestSurgerySubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "surgery-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	surgery := surgerySubmissionRequest(t, "shared")
	if _, _, err := s.SubmitSurgery(ctx, surgery); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	surgery.RequestID = "surgery-only"
	first, created, err := s.SubmitSurgery(ctx, surgery)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "surgery-only"); err == nil {
		t.Fatal("building lookup accepted a surgery submission")
	}
}
