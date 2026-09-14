package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func bedAssignSubmissionRequest(t *testing.T, id string) BedAssignSubmissionRequest {
	t.Helper()
	previous, err := domain.KnownPreviousBed("bed-old")
	if err != nil {
		t.Fatal(err)
	}
	assign, err := domain.NewBedAssign("pawn-1", "bed-new", previous)
	if err != nil {
		t.Fatal(err)
	}
	return BedAssignSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Assign: assign}
}

func TestBedAssignSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "bed-assign-submission.db")
	s := open(t, path)
	request := bedAssignSubmissionRequest(t, "request")
	first, created, err := s.SubmitBedAssign(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitBedAssign(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*BedAssignSubmissionRequest){
		func(v *BedAssignSubmissionRequest) { v.World.Map = 1 },
		func(v *BedAssignSubmissionRequest) { v.World.Load = "other" },
		func(v *BedAssignSubmissionRequest) { v.World.Colony = "other" },
		func(v *BedAssignSubmissionRequest) {
			v.Assign, _ = domain.NewBedAssign("other-pawn", "bed-new", v.Assign.PreviousBed())
		},
		func(v *BedAssignSubmissionRequest) { v.Assign, _ = domain.NewBedAssign("pawn-1", "bed-new", domain.ClearPreviousBed()) },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitBedAssign(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitBedAssign(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupBedAssignSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
}

func TestBedAssignSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "bed-assign-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_bed_assign_submission BEFORE INSERT ON bed_assign_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitBedAssign(ctx, bedAssignSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "bed_assign_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_bed_assign_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitBedAssign(ctx, bedAssignSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestBedAssignSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "bed-assign-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*BedAssignSubmissionRequest){
		func(v *BedAssignSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *BedAssignSubmissionRequest) { v.World.Map = -1 },
		func(v *BedAssignSubmissionRequest) { v.World.Load = "" },
		func(v *BedAssignSubmissionRequest) { v.Assign = domain.BedAssign{} },
	} {
		request := bedAssignSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitBedAssign(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupBedAssignSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and bed-assign submissions share one submission-identity
// namespace; a request id used by one kind must not silently resolve as the
// other, and the generic lookupAnySubmission dispatch must reach both.
func TestBedAssignSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "bed-assign-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	assign := bedAssignSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitBedAssign(ctx, assign); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	assign.RequestID = "bed-assign-only"
	first, created, err := s.SubmitBedAssign(ctx, assign)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "bed-assign-only"); err == nil {
		t.Fatal("building lookup accepted a bed assign submission")
	}
}
