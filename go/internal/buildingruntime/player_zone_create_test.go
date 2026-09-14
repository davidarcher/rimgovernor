package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerZoneCreateRequest() store.ZoneCreateSubmissionRequest {
	zone, _ := domain.NewZoneCreate(domain.GrowingZone, "Rice", []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}})
	return store.ZoneCreateSubmissionRequest{RequestID: "zone-submit", World: playerSubmission().World, Zone: zone}
}

// Zone creation is player-command-driven, unlike the autopilot-goal-bound
// admitZoneMethod routine gate: submission alone commits a plan and never
// acquires authority or issues a native command, the same shape
// SubmitQuestAccept already proves.
func TestPlayerZoneCreateSubmissionReplayAndSharedFamilyNamespace(t *testing.T) {
	t.Parallel()
	p, db, s, worlds := playerFixture(t)
	q := playerZoneCreateRequest()
	result, created, err := p.SubmitZoneCreate(context.Background(), q)
	if err != nil || !created {
		t.Fatal(result, created, err)
	}
	state, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	zone, ok := state.Spec.Actions()[0].ZoneCreate()
	if !ok || zone != q.Zone || state.Progress[0].View().Stage != domain.Pending || state.Progress[0].View().Attempt != 0 {
		t.Fatal(state)
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitZoneCreate(context.Background(), q)
	if err != nil || created || replay != result || worlds.calls != 1 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.Zone, _ = domain.NewZoneCreate(domain.GrowingZone, "Rice", []domain.Cell{{X: 2, Z: 0}})
	if _, _, err = p.SubmitZoneCreate(context.Background(), changed); !errors.Is(err, store.ErrConflict) {
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
	if _, _, err = p.SubmitZoneCreate(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("submission changed native control")
	}
}
func TestPlayerZoneCreateFreshWorldAndClosedPlayer(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	q := playerZoneCreateRequest()
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitZoneCreate(context.Background(), q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupZoneCreateSubmission(context.Background(), q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world = q.World
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitZoneCreate(context.Background(), q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
func TestPlayerZoneCreateSubmissionRequiresSeparateExplicitAcquire(t *testing.T) {
	t.Parallel()
	p, _, s, _ := playerFixture(t)
	q := playerZoneCreateRequest()
	submission, _, err := p.SubmitZoneCreate(context.Background(), q)
	if err != nil {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 {
		t.Fatal("implicit acquire")
	}
	record, err := p.Acquire(context.Background(), store.ControlRequest{RequestID: "acquire-zone", Kind: store.AcquireControl, World: q.World, Plan: submission.Plan, Revision: submission.Revision})
	if err != nil || record.Phase != store.GrantedControl || s.acquires.Load() != 1 {
		t.Fatal(record, err)
	}
}
