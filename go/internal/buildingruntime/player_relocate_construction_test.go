package buildingruntime

import (
	"context"
	"errors"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerRelocateConstructionRequest() store.RelocateConstructionSubmissionRequest {
	room, _ := domain.NewRoomShell(domain.RoomBounds{X: 20, Z: 21, Width: 4, Height: 5}, "Wall", "Door", "WoodLog", domain.North, domain.ShelterRoom)
	return store.RelocateConstructionSubmissionRequest{RequestID: "relocate-submit", World: playerSubmission().World, IntentID: "shelter-01", Replacement: room}
}

// Relocation is player-command-driven exactly as the room shell it supersedes
// is: it commits its own plan, never acquires authority and never issues a
// native command. With no placement yet dispatched there is nothing to withdraw,
// so it takes the fast path and commits the replacement's placements alone.
func TestPlayerRelocateConstructionSubmissionFastPathAndReplay(t *testing.T) {
	t.Parallel()
	p, db, s, worlds := playerFixture(t)
	if _, _, err := p.SubmitBuildRoom(context.Background(), playerBuildRoomRequest()); err != nil {
		t.Fatal(err)
	}
	q := playerRelocateConstructionRequest()
	result, created, err := p.SubmitRelocateConstruction(context.Background(), q)
	if err != nil || !created {
		t.Fatal(result, created, err)
	}
	if !result.FastPath || result.CancelAction != "" || len(result.Targets) != 0 || result.Plan == "" || result.BuildAction == "" {
		t.Fatal("unissued construction must relocate without withdrawals", result)
	}
	if len(result.BuildActionIDs()) != len(q.Replacement.Placements()) {
		t.Fatal("committed placements do not match the replacement", result.BuildActionIDs())
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitRelocateConstruction(context.Background(), q)
	if err != nil || created || !reflect.DeepEqual(replay, result) || worlds.calls != 2 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.IntentID = "shelter-02"
	if _, _, err = p.SubmitRelocateConstruction(context.Background(), changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	found, err := db.LookupRelocateConstructionSubmission(context.Background(), q.RequestID)
	if err != nil || !reflect.DeepEqual(found, result) {
		t.Fatal(found, err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("submission changed native control")
	}
	// The intent now names the replacement, so a cancellation of it withdraws
	// where the construction stands rather than where it was first ordered.
	head, err := db.LookupConstructionIntent(context.Background(), q.World, q.IntentID)
	if err != nil || head.Plan != result.Plan || head.Room != q.Replacement {
		t.Fatal("intent did not follow its relocation", head, err)
	}
}

// A construction nobody submitted cannot be moved, and a relocation refused for
// any reason leaves no record behind.
func TestPlayerRelocateConstructionRequiresASubmittedIntent(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	q := playerRelocateConstructionRequest()
	if _, _, err := p.SubmitRelocateConstruction(context.Background(), q); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	if _, err := db.LookupRelocateConstructionSubmission(context.Background(), q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitBuildRoom(context.Background(), playerBuildRoomRequest()); err != nil {
		t.Fatal(err)
	}
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitRelocateConstruction(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	worlds.world = q.World
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitRelocateConstruction(context.Background(), q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
