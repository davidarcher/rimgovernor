package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerTravelCaravanRequest(kind domain.TravelKind, destinationTile int32) store.TravelCaravanSubmissionRequest {
	travel, _ := domain.NewTravelCaravan("caravan", kind, destinationTile)
	return store.TravelCaravanSubmissionRequest{RequestID: "travel-caravan-submit", World: playerSubmission().World, Travel: travel}
}

// Caravan travel is player-command-driven, unlike the fixed-priority a-e
// routine families: submission alone commits a plan and never acquires
// authority or issues a native command, the same shape SubmitQuestAccept
// already proves. One submission surface covers both the interpreter's
// hold_caravan (TravelStop) and route_caravan (Move/Visit/ReturnHome)
// commands, since both compile to the same domain.TravelCaravan action.
func TestPlayerTravelCaravanSubmissionReplayAndSharedFamilyNamespace(t *testing.T) {
	t.Parallel()
	p, db, s, worlds := playerFixture(t)
	q := playerTravelCaravanRequest(domain.TravelStop, -1)
	result, created, err := p.SubmitTravelCaravan(context.Background(), q)
	if err != nil || !created {
		t.Fatal(result, created, err)
	}
	state, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	travel, ok := state.Spec.Actions()[0].TravelCaravan()
	if !ok || travel != q.Travel || state.Progress[0].View().Stage != domain.Pending || state.Progress[0].View().Attempt != 0 {
		t.Fatal(state)
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitTravelCaravan(context.Background(), q)
	if err != nil || created || replay != result || worlds.calls != 1 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.Travel, _ = domain.NewTravelCaravan("caravan", domain.TravelMove, 7)
	if _, _, err = p.SubmitTravelCaravan(context.Background(), changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	building := playerSubmission()
	building.RequestID = q.RequestID
	if _, _, err = p.Submit(context.Background(), building); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	worlds.err = nil
	building.RequestID = "building-submit"
	if _, _, err = p.Submit(context.Background(), building); err != nil {
		t.Fatal(err)
	}
	q.RequestID = building.RequestID
	if _, _, err = p.SubmitTravelCaravan(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("submission changed native control")
	}
}

// route_caravan's Move/Visit and ReturnHome kinds route through the same
// SubmitTravelCaravan surface as hold_caravan's Stop kind, distinguished
// only by TravelKind and destination tile.
func TestPlayerTravelCaravanSubmissionRouteKinds(t *testing.T) {
	t.Parallel()
	p, db, _, _ := playerFixture(t)
	for _, tc := range []struct {
		name            string
		kind            domain.TravelKind
		destinationTile int32
		requestID       string
	}{
		{"move", domain.TravelMove, 42, "route-move"},
		{"visit", domain.TravelVisit, 42, "route-visit"},
		{"return-home", domain.TravelReturnHome, -1, "route-return-home"},
	} {
		q := playerTravelCaravanRequest(tc.kind, tc.destinationTile)
		q.RequestID = tc.requestID
		result, created, err := p.SubmitTravelCaravan(context.Background(), q)
		if err != nil || !created {
			t.Fatal(tc.name, result, created, err)
		}
		state, err := db.LoadPlan(context.Background(), result.Plan)
		if err != nil {
			t.Fatal(tc.name, err)
		}
		travel, ok := state.Spec.Actions()[0].TravelCaravan()
		if !ok || travel.Kind() != tc.kind || travel.DestinationTile() != tc.destinationTile {
			t.Fatal(tc.name, travel)
		}
	}
}
func TestPlayerTravelCaravanFreshWorldAndClosedPlayer(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	q := playerTravelCaravanRequest(domain.TravelStop, -1)
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitTravelCaravan(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupTravelCaravanSubmission(context.Background(), q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world = q.World
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitTravelCaravan(context.Background(), q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
func TestPlayerTravelCaravanSubmissionRequiresSeparateExplicitAcquire(t *testing.T) {
	t.Parallel()
	p, _, s, _ := playerFixture(t)
	q := playerTravelCaravanRequest(domain.TravelStop, -1)
	submission, _, err := p.SubmitTravelCaravan(context.Background(), q)
	if err != nil {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 {
		t.Fatal("implicit acquire")
	}
	record, err := p.Acquire(context.Background(), store.ControlRequest{RequestID: "acquire-travel-caravan", Kind: store.AcquireControl, World: q.World, Plan: submission.Plan, Revision: submission.Revision})
	if err != nil || record.Phase != store.GrantedControl || s.acquires.Load() != 1 {
		t.Fatal(record, err)
	}
}
