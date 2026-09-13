package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type caravanJourneyNativeFake struct {
	*clockCoreFake
	caravans []bridge.CaravanJourney
	maps     []bridge.WorldMap
	home     []*n.PawnState
}

func (f *caravanJourneyNativeFake) ReadWorldProgression(ctx context.Context, id *c.Identity, includeStorage bool) (bridge.WorldProgressionRead, bridge.Result, error) {
	return bridge.WorldProgressionRead{Context: proto.Clone(f.status.Context).(*c.ObservationContext), Caravans: f.caravans, Maps: f.maps}, bridge.Result{}, ctx.Err()
}
func (f *caravanJourneyNativeFake) ReadHomeColonists(ctx context.Context, id *c.Identity) (*n.ListPawnsReply, bridge.Result, error) {
	return &n.ListPawnsReply{Outcome: &n.ListPawnsReply_Observed{Observed: &n.PawnSnapshot{Context: proto.Clone(f.status.Context).(*c.ObservationContext), Pawns: f.home}}}, bridge.Result{}, ctx.Err()
}

func caravanJourneyFixture(t *testing.T) (*CaravanJourneyTracker, *caravanJourneyNativeFake, *store.Store, domain.GenerationSnapshot) {
	t.Helper()
	_, db, f, intent := clockCoreFixture(t)
	s, _, _ := newClockSessionTest(t, db, f)
	p, err := NewPlayer(context.Background(), PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db, s, playerWorldFunc(func(context.Context) (store.World, error) { return playerWorld(intent.Snapshot), nil }))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = p.Close(context.Background()) })
	if _, err = s.Acquire(context.Background(), intent.Snapshot); err != nil {
		t.Fatal(err)
	}
	native := &caravanJourneyNativeFake{clockCoreFake: f}
	tracker, err := NewCaravanJourneyTracker(p, native, db, boundary.FixedClock{}, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	return tracker, native, db, intent.Snapshot
}

func TestCaravanJourneyTrackerNoneTracked(t *testing.T) {
	t.Parallel()
	tracker, _, _, _ := caravanJourneyFixture(t)
	result, err := tracker.Step(context.Background())
	if err != nil || result.Reason != CaravanJourneyNoneTracked || len(result.Outcomes) != 0 {
		t.Fatal(result, err)
	}
}

func TestCaravanJourneyTrackerInFlightNeverResolves(t *testing.T) {
	t.Parallel()
	tracker, native, db, _ := caravanJourneyFixture(t)
	ctx := context.Background()
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"pawn-1"}); err != nil {
		t.Fatal(err)
	}
	native.caravans = []bridge.CaravanJourney{{ID: "caravan-1", Tile: 5, Moving: true, PawnIDs: []string{"pawn-1"}}}
	result, err := tracker.Step(ctx)
	if err != nil || result.Reason != CaravanJourneyPolled || len(result.Resolved) != 0 {
		t.Fatal(result, err)
	}
	if len(result.Outcomes) != 1 || result.Outcomes[0].CaravanID != "caravan-1" || result.Outcomes[0].Status != policy.CaravanJourneyInFlight {
		t.Fatal(result.Outcomes)
	}
	active, err := db.ListActiveCaravanTracking(ctx)
	if err != nil || len(active) != 1 {
		t.Fatal(active, err)
	}
}

func TestCaravanJourneyTrackerUnknownNeverResolves(t *testing.T) {
	t.Parallel()
	tracker, native, db, _ := caravanJourneyFixture(t)
	ctx := context.Background()
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"pawn-1"}); err != nil {
		t.Fatal(err)
	}
	// The caravan is no longer a live world object, but the crew is also not
	// on the home census: neither map accounts for the pawn, so the safety
	// property under test is that this can never be treated as home.
	native.caravans = nil
	native.home = nil
	result, err := tracker.Step(ctx)
	if err != nil || len(result.Resolved) != 0 {
		t.Fatal(result, err)
	}
	if len(result.Outcomes) != 1 || result.Outcomes[0].Status != policy.CaravanJourneyUnknown {
		t.Fatal(result.Outcomes)
	}
	active, err := db.ListActiveCaravanTracking(ctx)
	if err != nil || len(active) != 1 {
		t.Fatal(active, err)
	}
}

func TestCaravanJourneyTrackerStoppedMarksStuck(t *testing.T) {
	t.Parallel()
	tracker, native, db, _ := caravanJourneyFixture(t)
	ctx := context.Background()
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"pawn-1"}); err != nil {
		t.Fatal(err)
	}
	native.caravans = []bridge.CaravanJourney{{ID: "caravan-1", Tile: 5, Moving: false, PawnIDs: []string{"pawn-1"}}}
	result, err := tracker.Step(ctx)
	if err != nil || len(result.Outcomes) != 1 || result.Outcomes[0].Status != policy.CaravanJourneyStopped {
		t.Fatal(result, err)
	}
	stuck, err := db.ListStuckCaravanTracking(ctx, native.status.Context.GetTick(), 0)
	if err != nil || len(stuck) != 1 || stuck[0].CaravanID != "caravan-1" || stuck[0].Status != store.StuckCaravanStopped {
		t.Fatal(stuck, err)
	}
	// Recovering to InFlight clears the stuck record.
	native.caravans = []bridge.CaravanJourney{{ID: "caravan-1", Tile: 5, Moving: true, PawnIDs: []string{"pawn-1"}}}
	if _, err = tracker.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if stuck, err = db.ListStuckCaravanTracking(ctx, native.status.Context.GetTick(), 0); err != nil || len(stuck) != 0 {
		t.Fatal(stuck, err)
	}
}

func TestCaravanJourneyTrackerOnForeignMapNeitherResolvesNorLosesCrew(t *testing.T) {
	t.Parallel()
	tracker, native, db, _ := caravanJourneyFixture(t)
	ctx := context.Background()
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"pawn-1", "pawn-2"}); err != nil {
		t.Fatal(err)
	}
	// The world object is gone (ambushed into an encounter map, say), pawn-1
	// is visible spawned on that non-home map, and pawn-2 is accounted for
	// nowhere. This must never be treated as home, but it must also not be
	// indistinguishable from a caravan that has vanished entirely.
	native.caravans = nil
	native.maps = []bridge.WorldMap{{ID: 99, Home: false, PawnIDs: []string{"pawn-1"}}}
	native.home = nil
	result, err := tracker.Step(ctx)
	if err != nil || len(result.Resolved) != 0 {
		t.Fatal(result, err)
	}
	if len(result.Outcomes) != 1 || result.Outcomes[0].Status != policy.CaravanJourneyOnForeignMap {
		t.Fatal(result.Outcomes)
	}
	active, err := db.ListActiveCaravanTracking(ctx)
	if err != nil || len(active) != 1 {
		t.Fatal(active, err)
	}
	stuck, err := db.ListStuckCaravanTracking(ctx, native.status.Context.GetTick(), 0)
	if err != nil || len(stuck) != 1 || stuck[0].Status != store.StuckCaravanOnForeignMap {
		t.Fatal(stuck, err)
	}
}

func TestCaravanJourneyTrackerReturnedHomeResolves(t *testing.T) {
	t.Parallel()
	tracker, native, db, _ := caravanJourneyFixture(t)
	ctx := context.Background()
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"pawn-1", "pawn-2"}); err != nil {
		t.Fatal(err)
	}
	native.caravans = nil
	native.home = []*n.PawnState{
		{Pawn: &n.EntityRef{Id: proto.String("pawn-1")}},
		{Pawn: &n.EntityRef{Id: proto.String("pawn-2")}},
	}
	result, err := tracker.Step(ctx)
	if err != nil || len(result.Resolved) != 1 || result.Resolved[0] != "caravan-1" {
		t.Fatal(result, err)
	}
	if len(result.Outcomes) != 1 || result.Outcomes[0].Status != policy.CaravanJourneyReturnedHome {
		t.Fatal(result.Outcomes)
	}
	active, err := db.ListActiveCaravanTracking(ctx)
	if err != nil || len(active) != 0 {
		t.Fatal(active, err)
	}
	// Resolving twice is impossible: nothing is left to poll, and a direct
	// resolve on an already-resolved id must fail rather than silently
	// succeed, since a caller should only ever resolve a record it just
	// listed as active.
	if err = db.ResolveCaravanTracking(ctx, "caravan-1"); err == nil {
		t.Fatal("resolved an already-resolved caravan")
	}
}
