package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerBuildRoomRequest() store.BuildRoomSubmissionRequest {
	room, _ := domain.NewRoomShell(domain.RoomBounds{X: 2, Z: 3, Width: 4, Height: 5}, "Wall", "Door", "WoodLog", domain.South, domain.ShelterRoom)
	return store.BuildRoomSubmissionRequest{RequestID: "room-submit", World: playerSubmission().World, IntentID: "shelter-01", Room: room}
}

// A room shell is player-command-driven exactly as zone creation is: the
// submission commits the whole expanded plan by itself, never acquires
// authority and never issues a native placement. Unlike every other submission
// its plan holds many actions, one ordinary building action per perimeter cell.
func TestPlayerBuildRoomSubmissionCommitsExpandedPlan(t *testing.T) {
	t.Parallel()
	p, db, s, worlds := playerFixture(t)
	q := playerBuildRoomRequest()
	result, created, err := p.SubmitBuildRoom(context.Background(), q)
	if err != nil || !created {
		t.Fatal(result, created, err)
	}
	state, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	placements := q.Room.Placements()
	if len(state.Spec.Actions()) != len(placements) || len(placements) != 2*4+2*(5-2) {
		t.Fatal("expansion differs", len(state.Spec.Actions()), len(placements))
	}
	for i, action := range state.Spec.Actions() {
		building, ok := action.Building()
		if !ok || building != placements[i] || action.Kind() != domain.BuildingAction {
			t.Fatal("committed placement differs", i, action)
		}
		if state.Progress[i].View().Stage != domain.Pending || state.Progress[i].View().Attempt != 0 {
			t.Fatal("committed placement is not pending", i)
		}
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitBuildRoom(context.Background(), q)
	if err != nil || created || replay != result || worlds.calls != 1 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.Room, _ = domain.NewRoomShell(domain.RoomBounds{X: 2, Z: 3, Width: 4, Height: 5}, "Wall", "Door", "Steel", domain.North, domain.DefenseRoom)
	if _, _, err = p.SubmitBuildRoom(context.Background(), changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	building := playerSubmission()
	building.RequestID = q.RequestID
	if _, _, err = p.Submit(context.Background(), building); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("submission changed native control")
	}
}

func TestPlayerBuildRoomFreshWorldAndClosedPlayer(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	q := playerBuildRoomRequest()
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitBuildRoom(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupBuildRoomSubmission(context.Background(), q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world = q.World
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitBuildRoom(context.Background(), q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}

func TestPlayerBuildRoomSubmissionRequiresSeparateExplicitAcquire(t *testing.T) {
	t.Parallel()
	p, _, s, _ := playerFixture(t)
	q := playerBuildRoomRequest()
	submission, _, err := p.SubmitBuildRoom(context.Background(), q)
	if err != nil {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 {
		t.Fatal("implicit acquire")
	}
	record, err := p.Acquire(context.Background(), store.ControlRequest{RequestID: "acquire-room", Kind: store.AcquireControl, World: q.World, Plan: submission.Plan, Revision: submission.Revision})
	if err != nil || record.Phase != store.GrantedControl || s.acquires.Load() != 1 {
		t.Fatal(record, err)
	}
}
