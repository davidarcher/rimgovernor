package buildingruntime

import (
	"context"
	"errors"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerCancelConstructionRequest() store.CancelConstructionSubmissionRequest {
	return store.CancelConstructionSubmissionRequest{RequestID: "cancel-submit", World: playerSubmission().World, IntentID: "shelter-01"}
}

// Cancellation is player-command-driven exactly as the room shell it withdraws
// is: it commits by itself, never acquires authority and never issues a native
// removal. With no placement yet dispatched there is nothing native to withdraw,
// which commits as an observed-absent record carrying no plan at all -- a
// successful outcome, and still replayable.
func TestPlayerCancelConstructionSubmissionObservedAbsentAndReplay(t *testing.T) {
	t.Parallel()
	p, db, s, worlds := playerFixture(t)
	if _, _, err := p.SubmitBuildRoom(context.Background(), playerBuildRoomRequest()); err != nil {
		t.Fatal(err)
	}
	q := playerCancelConstructionRequest()
	result, created, err := p.SubmitCancelConstruction(context.Background(), q)
	if err != nil || !created {
		t.Fatal(result, created, err)
	}
	if !result.ObservedAbsent || result.Plan != "" || result.Action != "" || len(result.Targets) != 0 {
		t.Fatal("unissued placements must cancel to nothing", result)
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitCancelConstruction(context.Background(), q)
	if err != nil || created || !reflect.DeepEqual(replay, result) || worlds.calls != 2 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.IntentID = "shelter-02"
	if _, _, err = p.SubmitCancelConstruction(context.Background(), changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	found, err := db.LookupCancelConstructionSubmission(context.Background(), q.RequestID)
	if err != nil || !reflect.DeepEqual(found, result) {
		t.Fatal(found, err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("submission changed native control")
	}
}

// An intent nobody submitted cannot be withdrawn, and a cancellation refused
// for any reason leaves no record behind.
func TestPlayerCancelConstructionRequiresASubmittedIntent(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	q := playerCancelConstructionRequest()
	if _, _, err := p.SubmitCancelConstruction(context.Background(), q); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	if _, err := db.LookupCancelConstructionSubmission(context.Background(), q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitBuildRoom(context.Background(), playerBuildRoomRequest()); err != nil {
		t.Fatal(err)
	}
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitCancelConstruction(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	worlds.world = q.World
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitCancelConstruction(context.Background(), q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
