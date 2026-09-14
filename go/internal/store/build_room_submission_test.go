package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func buildRoomSubmissionRequest(t *testing.T, id string) BuildRoomSubmissionRequest {
	t.Helper()
	room, err := domain.NewRoomShell(domain.RoomBounds{X: 4, Z: 6, Width: 5, Height: 4}, "Wall", "Door", "WoodLog", domain.South, domain.ShelterRoom)
	if err != nil {
		t.Fatal(err)
	}
	return BuildRoomSubmissionRequest{RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0}, IntentID: "shelter-01", Room: room}
}

func TestBuildRoomSubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "build-room-submission.db")
	s := open(t, path)
	request := buildRoomSubmissionRequest(t, "request")
	first, created, err := s.SubmitBuildRoom(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	ids := first.ActionIDs()
	if len(ids) != len(request.Room.Placements()) || ids[0] != first.Action {
		t.Fatal("derived placement identities", ids)
	}
	replay, created, err := s.SubmitBuildRoom(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	other, err := domain.NewRoomShell(domain.RoomBounds{X: 4, Z: 6, Width: 5, Height: 4}, "Wall", "Door", "Steel", domain.North, domain.DefenseRoom)
	if err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*BuildRoomSubmissionRequest){
		func(v *BuildRoomSubmissionRequest) { v.World.Map = 1 },
		func(v *BuildRoomSubmissionRequest) { v.World.Load = "other" },
		func(v *BuildRoomSubmissionRequest) { v.World.Colony = "other" },
		func(v *BuildRoomSubmissionRequest) { v.IntentID = "shelter-02" },
		func(v *BuildRoomSubmissionRequest) { v.Room = other },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitBuildRoom(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitBuildRoom(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupBuildRoomSubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != len(ids) {
		t.Fatal(state, err)
	}
	// Every committed action is an ordinary building action, in expansion
	// order, pending exactly as any other freshly committed plan is.
	for i, action := range state.Spec.Actions() {
		building, ok := action.Building()
		if !ok || action.ID() != ids[i] || building != request.Room.Placements()[i] {
			t.Fatal("committed placement differs", i, action)
		}
		if state.Progress[i].View().Stage != domain.Pending {
			t.Fatal("committed placement is not pending", i)
		}
	}
}

// The intent is the durable handle a later cancellation or relocation slice
// resolves back to this plan, and it is scoped to one world.
func TestBuildRoomIntentResolvesWithinItsWorld(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "build-room-intent.db"))
	request := buildRoomSubmissionRequest(t, "request")
	first, _, err := s.SubmitBuildRoom(ctx, request)
	if err != nil {
		t.Fatal(err)
	}
	found, err := s.LookupBuildRoomIntent(ctx, request.World, request.IntentID)
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	if _, err := s.LookupBuildRoomIntent(ctx, World{Colony: "colony", Load: "load", Map: 1}, request.IntentID); !errors.Is(err, ErrNotFound) {
		t.Fatal("intent leaked across worlds", err)
	}
	if _, err := s.LookupBuildRoomIntent(ctx, request.World, "unknown"); !errors.Is(err, ErrNotFound) {
		t.Fatal("unknown intent resolved", err)
	}
	if _, err := s.LookupBuildRoomIntent(ctx, request.World, "not a valid intent"); err == nil {
		t.Fatal("invalid intent accepted")
	}
	if _, err := s.LookupBuildRoomIntent(ctx, World{}, request.IntentID); err == nil {
		t.Fatal("invalid world accepted")
	}
	// The same intent in the same world is one construction, whatever request
	// id carries it. Replacing it is the deferred relocation slice's job.
	reused := buildRoomSubmissionRequest(t, "second-request")
	if _, _, err := s.SubmitBuildRoom(ctx, reused); !errors.Is(err, ErrConflict) {
		t.Fatal("duplicate world-scoped intent accepted", err)
	}
	elsewhere := buildRoomSubmissionRequest(t, "other-world")
	elsewhere.World.Map = 1
	if _, _, err := s.SubmitBuildRoom(ctx, elsewhere); err != nil {
		t.Fatal("same intent in another world refused", err)
	}
}

func TestBuildRoomSubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "build-room-atomic.db"))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_build_room_submission BEFORE INSERT ON build_room_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitBuildRoom(ctx, buildRoomSubmissionRequest(t, "request")); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "build_room_submissions"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_build_room_submission"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitBuildRoom(ctx, buildRoomSubmissionRequest(t, "request")); err != nil || !created {
		t.Fatal(err)
	}
}

func TestBuildRoomSubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "build-room-validation.db"))
	ctx := context.Background()
	for _, change := range []func(*BuildRoomSubmissionRequest){
		func(v *BuildRoomSubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *BuildRoomSubmissionRequest) { v.World.Map = -1 },
		func(v *BuildRoomSubmissionRequest) { v.World.Load = "" },
		func(v *BuildRoomSubmissionRequest) { v.IntentID = "" },
		func(v *BuildRoomSubmissionRequest) { v.IntentID = "not a valid intent" },
		func(v *BuildRoomSubmissionRequest) { v.Room = domain.RoomShell{} },
	} {
		request := buildRoomSubmissionRequest(t, "request")
		change(&request)
		if _, _, err := s.SubmitBuildRoom(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupBuildRoomSubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Building and build room submissions share one submission-identity namespace;
// a request id used by one kind must not silently resolve as the other, and
// the generic lookupAnySubmission dispatch must reach both.
func TestBuildRoomSubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "build-room-namespace.db"))
	if _, _, err := s.SubmitBuilding(ctx, submissionRequest(t, "shared")); err != nil {
		t.Fatal(err)
	}
	room := buildRoomSubmissionRequest(t, "shared")
	if _, _, err := s.SubmitBuildRoom(ctx, room); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	room.RequestID = "room-only"
	if _, created, err := s.SubmitBuildRoom(ctx, room); err != nil || !created {
		t.Fatal(created, err)
	}
	if _, err := s.LookupSubmission(ctx, "room-only"); err == nil {
		t.Fatal("building lookup accepted a build room submission")
	}
	if _, err := s.LookupBuildRoomSubmission(ctx, "shared"); err == nil {
		t.Fatal("build room lookup accepted a building submission")
	}
}
