package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// worldEvaluationNativeFake embeds clockCoreFake (for its Identity
// implementation, reusing clockCoreFixture's already-consistent context)
// and adds the two censuses WorldEvaluation.Read composes, mirroring
// caravanJourneyNativeFake's shape in caravan_journey_tracker_test.go.
type worldEvaluationNativeFake struct {
	*clockCoreFake
	world       bridge.WorldProgressionRead
	colony      *o.ColonyFactsReply
	worldErr    error
	colonyErr   error
	identityErr error
}

// Identity shadows clockCoreFake.Identity with its own error field: sharing
// clockCoreFake's identityError would also poison the session's own
// internal Control/Owner shutdown calls (they use the same embedded fake),
// leaving the test's profile lock file unreleased on cleanup.
func (f *worldEvaluationNativeFake) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	if f.identityErr != nil {
		return nil, bridge.Result{}, f.identityErr
	}
	return f.clockCoreFake.Identity(ctx)
}
func (f *worldEvaluationNativeFake) ReadWorldProgression(ctx context.Context, id *c.Identity, includeStorage bool) (bridge.WorldProgressionRead, bridge.Result, error) {
	if f.worldErr != nil {
		return bridge.WorldProgressionRead{}, bridge.Result{}, f.worldErr
	}
	return f.world, bridge.Result{}, ctx.Err()
}
func (f *worldEvaluationNativeFake) ReadColonyFacts(ctx context.Context, id *c.Identity, planning bool, definitions []string) (*o.ColonyFactsReply, bridge.Result, error) {
	if f.colonyErr != nil {
		return nil, bridge.Result{}, f.colonyErr
	}
	return f.colony, bridge.Result{}, ctx.Err()
}

func worldEvaluationColonyFixture(context *c.ObservationContext, resources []*o.Quantity) *o.ColonyFactsReply {
	return &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: &o.ColonyFactsSnapshot{
		Context:      proto.Clone(context).(*c.ObservationContext),
		MapSize:      &o.MapSize{Width: proto.Uint32(10), Height: proto.Uint32(10)},
		Center:       &c.Cell{X: proto.Int32(5), Z: proto.Int32(5)},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)},
		Resources:    resources,
	}}}
}

func worldEvaluationFixture(t *testing.T) (*WorldEvaluation, *worldEvaluationNativeFake) {
	t.Helper()
	_, db, f, intent := clockCoreFixture(t)
	s, _, _ := newClockSessionTest(t, db, f)
	t.Cleanup(func() { _ = s.Close(context.Background()) })
	p, err := NewPlayer(context.Background(), PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db, s, playerWorldFunc(func(context.Context) (store.World, error) { return playerWorld(intent.Snapshot), nil }))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = p.Close(context.Background()) })
	if _, err = s.Acquire(context.Background(), intent.Snapshot); err != nil {
		t.Fatal(err)
	}
	native := &worldEvaluationNativeFake{clockCoreFake: f}
	w, err := NewWorldEvaluation(p, native, policy.WorldEvaluationPolicy{TravelFoodMarginDays: 0.5})
	if err != nil {
		t.Fatal(err)
	}
	return w, native
}

func TestWorldEvaluationReadSuccess(t *testing.T) {
	w, native := worldEvaluationFixture(t)
	ctx := native.status.Context
	native.world = bridge.WorldProgressionRead{
		Context: proto.Clone(ctx).(*c.ObservationContext),
		Caravans: []bridge.CaravanJourney{{
			ID: "caravan-1", FoodDays: 3, FoodDaysKnown: true,
			Pawns:      []bridge.CaravanPawnFact{{ID: "pawn-1", Dead: false, DeadKnown: true, Downed: false, DownedKnown: true}},
			HomeRoutes: []bridge.WorldRouteFact{{DestinationMapID: 1, Reachable: true, EstimatedTicks: 60000, EstimatedTicksKnown: true}},
			Inventory:  map[string]int64{"Steel": 100},
		}},
		Quests: []bridge.QuestOffer{{
			ID: "quest-1", State: "Ongoing", CanAccept: true,
			TradeRequests: []bridge.QuestTradeRequestFact{{Resource: "Steel", Count: 40, DestinationTile: 7}},
		}},
	}
	native.colony = worldEvaluationColonyFixture(ctx, []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(5)}})
	report, err := w.Read(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if !report.Readable || len(report.Caravans) != 1 || len(report.Quests) != 1 {
		t.Fatal(report)
	}
	caravan := report.Caravans[0]
	if caravan.RecoveryRequired || !caravan.Healthy || caravan.ReachableHome == nil {
		t.Fatal(caravan)
	}
	quest := report.Quests[0]
	if len(quest.CarriedCandidates) != 1 || quest.CarriedCandidates[0] != "caravan-1" || quest.ResourceDeficits["Steel"] != 35 {
		t.Fatal(quest)
	}
}

func TestWorldEvaluationReadPropagatesNativeErrors(t *testing.T) {
	sentinel := errors.New("native failure")
	t.Run("identity", func(t *testing.T) {
		w, native := worldEvaluationFixture(t)
		native.identityErr = sentinel
		if _, err := w.Read(context.Background()); err == nil {
			t.Fatal("expected identity failure to propagate")
		}
	})
	t.Run("world", func(t *testing.T) {
		w, native := worldEvaluationFixture(t)
		native.worldErr = sentinel
		if _, err := w.Read(context.Background()); err == nil {
			t.Fatal("expected world progression failure to propagate")
		}
	})
	t.Run("colony", func(t *testing.T) {
		w, native := worldEvaluationFixture(t)
		native.world = bridge.WorldProgressionRead{Context: proto.Clone(native.status.Context).(*c.ObservationContext)}
		native.colonyErr = sentinel
		if _, err := w.Read(context.Background()); err == nil {
			t.Fatal("expected colony facts failure to propagate")
		}
	})
}

