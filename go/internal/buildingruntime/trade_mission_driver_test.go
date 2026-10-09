package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type missionDriverNative struct {
	RoundsTradeSource
	world   bridge.WorldProgressionRead
	cargo   map[string]int64
	session bridge.TradeSessionRead
	reads   int
}

func (n *missionDriverNative) ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error) {
	n.reads++
	return n.world, bridge.Result{}, nil
}
func (n *missionDriverNative) ReadPawnCargo(context.Context, *c.Identity, []string) (map[string]int64, bridge.Result, error) {
	return n.cargo, bridge.Result{}, nil
}
func (n *missionDriverNative) ReadTradeSession(context.Context, *c.Identity) (bridge.TradeSessionRead, bridge.Result, error) {
	return n.session, bridge.Result{}, nil
}

func TestTradeMissionReturnSafetyAndActualDelivery(t *testing.T) {
	m := tradeMissionFixture()
	m.Phase = domain.TradeMissionReturning
	m.PurchaseCommitted = true
	m.ReturnGoods = []domain.CargoItem{{Definition: "Steel", Count: 10}}
	world := missionWorldRead()
	world.Maps = []bridge.WorldMap{{ID: 1, Home: true, Tile: 2, PawnIDs: []string{"a"}}}
	if TradeMissionDelivered(m, world, nil) || TradeMissionDelivered(m, world, map[string]int64{"Steel": 9}) {
		t.Fatal("missing actual cargo delivered")
	}
	if !TradeMissionDelivered(m, world, map[string]int64{"Steel": 10}) {
		t.Fatal("native home cargo missed")
	}
	world.Caravans = []bridge.CaravanJourney{{PawnIDs: []string{"a"}}}
	if TradeMissionDelivered(m, world, map[string]int64{"Steel": 10}) {
		t.Fatal("away inventory credited")
	}
	world.Caravans = nil
	m.Demand = []domain.CargoItem{{Definition: "MealSurvivalPack", Count: 10}}
	m.ReturnGoods = []domain.CargoItem{{Definition: "MealSurvivalPack", Count: 2}}
	if TradeMissionDelivered(m, world, map[string]int64{"MealSurvivalPack": 3}) {
		t.Fatal("packed food substituted for a purchase")
	}
	if !TradeMissionDelivered(m, world, map[string]int64{"MealSurvivalPack": 5}) {
		t.Fatal("purchased food not delivered")
	}
	caravan := &bridge.CaravanJourney{MassUsage: domain.Known(10.0), MassCapacity: domain.Known(20.0), FoodDays: 3, FoodDaysKnown: true, FoodRotDays: domain.Known(4.0), HomeRoutes: []bridge.WorldRouteFact{{DestinationTile: 2, Reachable: true, EstimatedTicksKnown: true, EstimatedTicks: 2 * int64(domain.TicksPerDay)}}}
	if !TradeMissionReturnSafe(m, caravan) {
		t.Fatal("safe refreshed return refused")
	}
	caravan.FoodDays = 1
	if TradeMissionReturnSafe(m, caravan) {
		t.Fatal("return without food")
	}
	caravan.FoodDays = 3
	caravan.FoodRotDays = domain.Unknown[float64]()
	if TradeMissionReturnSafe(m, caravan) {
		t.Fatal("unknown rot admitted")
	}
	caravan.FoodRotDays = domain.Known(4.0)
	caravan.MassUsage = domain.Known(21.0)
	if TradeMissionReturnSafe(m, caravan) {
		t.Fatal("overloaded return admitted")
	}
}

func TestTradeMissionSavedCommitmentReturnsWithoutRebuy(t *testing.T) {
	ctx := context.Background()
	r, db, _, _, _ := roundsFixture(t)
	state := r.player.session.State()
	m := tradeMissionFixture()
	m.HomeColony = state.Snapshot.Colony
	m.HomeMap = state.Snapshot.Map
	m.Phase = domain.TradeMissionPlanned
	p, err := domain.NewTradeMissionProject("project-trade-reload", 1, state.Snapshot, m)
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreateProject(ctx, p); err != nil {
		t.Fatal(err)
	}
	project, err := db.LoadProject(ctx, p.ID)
	if err != nil {
		t.Fatal(err)
	}
	project, err = db.ReviewProject(ctx, p.ID, project.Revision, state.Snapshot, 7, domain.FindingUnmet)
	if err != nil {
		t.Fatal(err)
	}
	m.Phase = domain.TradeMissionBuying
	m.PurchaseCommitted = true
	m.ReturnGoods = []domain.CargoItem{{Definition: "Steel", Count: 10}}
	target := domain.TradeParticipant{Kind: domain.TradeParticipantSettlement, ID: m.Settlement, Caravan: "caravan"}
	value, err := domain.NewTradeAccept(target.Key(), m.Negotiator, "signature", false, false)
	if err != nil {
		t.Fatal(err)
	}
	value, err = value.WithParticipant(target)
	if err != nil {
		t.Fatal(err)
	}
	planner := &RoundsTradePlanner{reviewer: r}
	result, _, err := planner.missionTradeCommit(ctx, r.player.epoch, state, project, m, value)
	if err != nil || result.Plan == "" {
		t.Fatal(result, err)
	}
	// Save/load intentionally loses all session methods and action receipts.
	blobs, err := db.GovernorStateBlobs(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if err = db.RebuildStandards(ctx, blobs, nil); err != nil {
		t.Fatal(err)
	}
	restored, err := db.LoadProject(ctx, p.ID)
	if err != nil || len(restored.History) != 0 {
		t.Fatal(restored, err)
	}
	saved, err := domain.DecodeTradeMission(restored.Project.Record)
	if err != nil || !saved.PurchaseCommitted {
		t.Fatal(saved, err)
	}
	world := *missionWorldRead()
	world.Context.Identity = boundaryMissionIdentity(state.Snapshot)
	world.Context.NativeGeneration = proto.Uint64(uint64(state.Snapshot.Native))
	world.Context.Tick = proto.Int64(7)
	world.Caravans = []bridge.CaravanJourney{{ID: "caravan", Tile: m.SettlementTile, PawnIDs: []string{"a"}}}
	native := &missionDriverNative{world: world}
	planner.native = native
	manual := state
	manual.Enabled = false
	if result, _, err := planner.mission(ctx, r.player.epoch, manual, store.Rounds{}, newStepArbiter()); err != nil || result.Plan != "" || native.reads != 0 {
		t.Fatal("manual dispatched", result, err)
	}
	result, _, err = planner.mission(ctx, r.player.epoch, state, store.Rounds{}, newStepArbiter())
	if err != nil || result.Plan != "" {
		t.Fatal(result, err)
	}
	restored, err = db.LoadProject(ctx, p.ID)
	if err != nil {
		t.Fatal(err)
	}
	saved, err = domain.DecodeTradeMission(restored.Project.Record)
	if err != nil || saved.Phase != domain.TradeMissionReturning || len(restored.History) != 0 {
		t.Fatal("reload reopened trade", saved, restored, err)
	}
	native.world.Caravans = nil
	native.world.Maps = []bridge.WorldMap{{ID: int32(m.HomeMap), Home: true, Tile: m.HomeTile, PawnIDs: []string{"a"}}}
	native.cargo = map[string]int64{"Steel": 9}
	if _, _, err = planner.mission(ctx, r.player.epoch, state, store.Rounds{}, newStepArbiter()); err != nil {
		t.Fatal(err)
	}
	restored, _ = db.LoadProject(ctx, p.ID)
	if restored.Project.Status != domain.ProjectOpen {
		t.Fatal("receipt/home arrival completed without goods")
	}
	native.cargo["Steel"] = 10
	if _, _, err = planner.mission(ctx, r.player.epoch, state, store.Rounds{}, newStepArbiter()); err != nil {
		t.Fatal(err)
	}
	restored, _ = db.LoadProject(ctx, p.ID)
	saved, _ = domain.DecodeTradeMission(restored.Project.Record)
	if restored.Project.Status != domain.ProjectCompleted || saved.Phase != domain.TradeMissionDelivered {
		t.Fatal(restored, saved)
	}
}
