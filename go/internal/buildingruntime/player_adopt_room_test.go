package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerAdoptionRequest(t *testing.T, id, intent string) store.RoomAdoptionSubmissionRequest {
	t.Helper()
	w := playerSubmission().World
	room, err := domain.NewRoomAdoption(domain.RoomBounds{X: 10, Z: 10, Width: 5, Height: 5}, domain.South, nil, domain.Cell{}, false)
	if err != nil {
		t.Fatal(err)
	}
	return store.RoomAdoptionSubmissionRequest{
		RequestID: id,
		IntentID:  intent,
		Adoption:  room,
		Evidence:  domain.AdoptionEvidence{RoomID: "native-room-1", Cells: 9, Role: "Bedroom", RoleLabel: "bedroom"},
		Snapshot:  domain.GenerationSnapshot{Colony: w.Colony, Load: w.Load, Map: w.Map, Plan: "plan", Direction: 1},
		Tick:      10,
	}
}

// Adopting a room records the player's claim and completes the shelter goal: it
// acquires no authority, issues no native command and commits no plan.
func TestPlayerRoomAdoptionCompletesAndReplays(t *testing.T) {
	t.Parallel()
	p, db, s, worlds := playerFixture(t)
	ctx := context.Background()
	q := playerAdoptionRequest(t, "adopt-one", "shelter-a")
	result, created, err := p.SubmitRoomAdoption(ctx, q)
	if err != nil || !created {
		t.Fatal(result, created, err)
	}
	g := result.State.Goal
	if g.Source != domain.PlayerGoal || g.Status != domain.GoalSatisfied || g.Need != domain.NeedRecovered || !g.RecoveryObserved {
		t.Fatal("adoption did not complete the shelter goal", g)
	}
	preferred, err := p.AdoptedShelter(ctx, q.World())
	if err != nil || preferred.Request.RequestID != q.RequestID {
		t.Fatal(preferred, err)
	}
	// A replay is answered from the journal without re-reading native identity.
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitRoomAdoption(ctx, q)
	if err != nil || created || replay.Goal != result.Goal || !replay.Request.Same(q) || worlds.calls != 1 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.IntentID = "shelter-b"
	if _, _, err = p.SubmitRoomAdoption(ctx, changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	worlds.err = nil
	found, err := p.LookupRoomAdoptionSubmission(ctx, q.RequestID)
	if err != nil || !found.Request.Same(q) {
		t.Fatal(found, err)
	}
	if _, err = db.LoadGoal(ctx, result.Goal); err != nil {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("room adoption changed native control")
	}
}

// A world that moved under the request is refused before anything is stored,
// the same gate every other player command applies.
func TestPlayerRoomAdoptionFreshWorld(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	ctx := context.Background()
	q := playerAdoptionRequest(t, "adopt-fresh", "shelter-a")
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitRoomAdoption(ctx, q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupRoomAdoptionSubmission(ctx, q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
}
