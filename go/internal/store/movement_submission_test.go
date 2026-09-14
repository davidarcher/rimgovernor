package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func movementSubmissionRequest(id string) MovementSubmissionRequest {
	return MovementSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, Pawn: "pawn-1", Destination: domain.Cell{X: 3, Z: 4}}
}

func TestMovementSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "movement-submission.db")
	s := open(t, path)
	request := movementSubmissionRequest("request")
	first, created, err := s.SubmitMovement(ctx, request)
	if err != nil || !created || first.Plan == "" || first.DraftAction == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitMovement(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*MovementSubmissionRequest){
		func(v *MovementSubmissionRequest) { v.World.Map = 1 },
		func(v *MovementSubmissionRequest) { v.World.Load = "other" },
		func(v *MovementSubmissionRequest) { v.World.Colony = "other" },
		func(v *MovementSubmissionRequest) { v.Pawn = "other-pawn" },
		func(v *MovementSubmissionRequest) { v.Destination = domain.Cell{X: 5, Z: 6} },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitMovement(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitMovement(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupMovementSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 2 {
		t.Fatal(state, err)
	}
	actions := state.Spec.Actions()
	if actions[0].ID() != first.DraftAction || actions[1].ID() != first.Action {
		t.Fatal("movement plan action order or identity wrong", actions)
	}
	if state.Progress[0].View().Stage != domain.Pending || state.Progress[1].View().Stage != domain.Pending {
		t.Fatal(state)
	}
}

func TestMovementSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "movement-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_movement_submission BEFORE INSERT ON movement_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitMovement(ctx, movementSubmissionRequest("request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "movement_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_movement_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitMovement(ctx, movementSubmissionRequest("request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestMovementSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "movement-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*MovementSubmissionRequest){
		func(v *MovementSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *MovementSubmissionRequest) { v.World.Map = -1 },
		func(v *MovementSubmissionRequest) { v.World.Load = "" },
		func(v *MovementSubmissionRequest) { v.Pawn = "" },
		func(v *MovementSubmissionRequest) { v.Destination = domain.Cell{X: -1, Z: 0} },
		func(v *MovementSubmissionRequest) { v.Destination = domain.Cell{X: 0, Z: -1} },
	} {
		request := movementSubmissionRequest("request")
		change(&request)
		if _, _, err := s.SubmitMovement(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupMovementSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and movement submissions share one submission-identity namespace;
// a request id used by one kind must not silently resolve as the other, and
// the generic lookupAnySubmission dispatch must reach both.
func TestMovementSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "movement-namespace.db"))
	building := submissionRequest(t, "shared")
	if _, _, err := s.SubmitBuilding(ctx, building); err != nil {
		t.Fatal(err)
	}
	movement := movementSubmissionRequest("shared")
	if _, _, err := s.SubmitMovement(ctx, movement); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	movement.RequestID = "movement-only"
	first, created, err := s.SubmitMovement(ctx, movement)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if _, err := s.LookupSubmission(ctx, "movement-only"); err == nil {
		t.Fatal("building lookup accepted a movement submission")
	}
}
