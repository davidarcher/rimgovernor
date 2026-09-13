package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerCaravanDepartureRequest() store.CaravanDepartureSubmissionRequest {
	departure, _ := domain.NewCaravanDeparture([]domain.PawnID{"pawn"}, nil, 7)
	return store.CaravanDepartureSubmissionRequest{RequestID: "caravan-submit", World: playerSubmission().World, Departure: departure}
}

// Caravan departure is player-command-driven, unlike the fixed-priority a-e
// routine families: submission alone commits a plan and never acquires
// authority or issues a native command, the same shape SubmitDraft/Submit
// already prove.
func TestPlayerCaravanDepartureSubmissionReplayAndSharedFamilyNamespace(t *testing.T) {
	t.Parallel()
	p, db, s, worlds := playerFixture(t)
	q := playerCaravanDepartureRequest()
	result, created, err := p.SubmitCaravanDeparture(context.Background(), q)
	if err != nil || !created {
		t.Fatal(result, created, err)
	}
	state, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	departure, ok := state.Spec.Actions()[0].CaravanDeparture()
	if !ok || departure != q.Departure || state.Progress[0].View().Stage != domain.Pending || state.Progress[0].View().Attempt != 0 {
		t.Fatal(state)
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitCaravanDeparture(context.Background(), q)
	if err != nil || created || replay != result || worlds.calls != 1 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.Departure, _ = domain.NewCaravanDeparture([]domain.PawnID{"other-pawn"}, nil, 7)
	if _, _, err = p.SubmitCaravanDeparture(context.Background(), changed); !errors.Is(err, store.ErrConflict) {
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
	if _, _, err = p.SubmitCaravanDeparture(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("submission changed native control")
	}
}
func TestPlayerCaravanDepartureFreshWorldAndClosedPlayer(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	q := playerCaravanDepartureRequest()
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitCaravanDeparture(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupCaravanDepartureSubmission(context.Background(), q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world = q.World
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitCaravanDeparture(context.Background(), q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
func TestPlayerCaravanDepartureSubmissionRequiresSeparateExplicitAcquire(t *testing.T) {
	t.Parallel()
	p, _, s, _ := playerFixture(t)
	q := playerCaravanDepartureRequest()
	submission, _, err := p.SubmitCaravanDeparture(context.Background(), q)
	if err != nil {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 {
		t.Fatal("implicit acquire")
	}
	record, err := p.Acquire(context.Background(), store.ControlRequest{RequestID: "acquire-caravan", Kind: store.AcquireControl, World: q.World, Plan: submission.Plan, Revision: submission.Revision})
	if err != nil || record.Phase != store.GrantedControl || s.acquires.Load() != 1 {
		t.Fatal(record, err)
	}
}
