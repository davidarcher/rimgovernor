package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func tradeMissionFixture() domain.TradeMission {
	return domain.TradeMission{HomeColony: "colony", HomeMap: 1, HomeTile: 2, Settlement: "settlement", SettlementTile: 3, Crew: []domain.PawnID{"a"}, Negotiator: "a", SilverBudget: 100, Demand: []domain.CargoItem{{Definition: "Steel", Count: 10}}, Pack: []domain.CargoItem{{Definition: "MealSurvivalPack", Count: 3}, {Definition: "Silver", Count: 100}}, Phase: domain.TradeMissionDeparting, ReturnHome: true}
}

func missionWorldRead() *bridge.WorldProgressionRead {
	return &bridge.WorldProgressionRead{Context: &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), MapId: proto.Int32(1), LoadToken: proto.String("load")}}}
}

func TestTradeMissionLostReceiptAndExactNativeCrew(t *testing.T) {
	m := tradeMissionFixture()
	read := missionWorldRead()
	read.Assemblies = []bridge.CaravanAssemblyFact{{ID: "assembly", MapID: 1, PawnIDs: []string{"a"}}}
	if phase, caravan, known := TradeMissionDepartureEvidence(m, read); !known || phase != domain.TradeMissionDeparting || caravan != nil {
		t.Fatal(phase, caravan, known)
	}
	read.Assemblies = nil
	read.Caravans = []bridge.CaravanJourney{{ID: "native-caravan", Tile: 2, PawnIDs: []string{"a"}, Moving: true}}
	if phase, caravan, known := TradeMissionDepartureEvidence(m, read); !known || phase != domain.TradeMissionOutbound || caravan == nil || caravan.ID != "native-caravan" {
		t.Fatal(phase, caravan, known)
	}
	read.Caravans[0].Tile = 3
	read.Caravans[0].Moving = false
	if phase, _, known := TradeMissionDepartureEvidence(m, read); !known || phase != domain.TradeMissionBuying {
		t.Fatal(phase, known)
	}
	read.Caravans[0].PawnIDs = append(read.Caravans[0].PawnIDs, "replacement")
	if _, _, known := TradeMissionDepartureEvidence(m, read); known {
		t.Fatal("extra crew accepted")
	}
	read.Caravans = nil
	if phase, _, known := TradeMissionDepartureEvidence(m, read); known || phase != domain.TradeMissionDeparting {
		t.Fatal("absence retried formation", phase, known)
	}
	read.Context.Identity.ColonyId = proto.String("foreign")
	read.Assemblies = []bridge.CaravanAssemblyFact{{ID: "assembly", MapID: 1, PawnIDs: []string{"a"}}}
	if _, _, known := TradeMissionDepartureEvidence(m, read); known {
		t.Fatal("foreign world evidence accepted")
	}
}

type missionPackFake struct {
	requests []*op.FormCaravanIntent
	unsafe   bool
}

func (f *missionPackFake) ReadTradeAcquisition(_ context.Context, _ *c.Identity, pack *op.FormCaravanIntent) (*o.TradeAcquisition, bridge.Result, error) {
	f.requests = append(f.requests, proto.CloneOf(pack))
	food := int32(0)
	for _, item := range pack.Cargo {
		if item.GetDefName() == "MealSurvivalPack" {
			food = item.GetCount()
		}
	}
	// Mirrors native's minimum-day preparation gate, including partial read.
	if food < 1 {
		return &o.TradeAcquisition{Pack: &o.TradePackEstimate{CanPack: proto.Bool(false)}}, bridge.Result{}, nil
	}
	days := float64(food)
	if f.unsafe && len(f.requests) == 2 {
		days = 0
	}
	return &o.TradeAcquisition{Pack: &o.TradePackEstimate{CanPack: proto.Bool(true), MassUsage: proto.Float64(10 + float64(food)*.3), MassCapacity: proto.Float64(35), FoodDays: proto.Float64(days), FoodRotDays: proto.Float64(100), Outbound: &o.WorldRoute{Destination: proto.Int32(3), Reachable: proto.Bool(true), EstimatedTicks: proto.Int64(int64(domain.TicksPerDay))}, Home: &o.WorldRoute{Destination: proto.Int32(2), Reachable: proto.Bool(true), EstimatedTicks: proto.Int64(2 * int64(domain.TicksPerDay))}}}, bridge.Result{}, nil
}

func missionPreparationFacts() policy.RoundsFacts {
	return policy.RoundsFacts{Colonists: domain.Known(int64(6)), Items: policy.ItemFacts{Currency: "Silver"}, Resources: domain.Known([]policy.Amount{{Resource: "Silver", Count: 1000}}), QuestColonyCalm: domain.Known(true), QuestColonistsAtHome: domain.Known(6), QuestSparePawns: domain.Known([]policy.PawnID{"a"}), QuestDeparturePawns: domain.Known([]policy.QuestDeparturePawn{{ID: "a", HealthyAdult: domain.Known(true), NegotiationAbility: domain.Known(1.0), DefensePoints: domain.Known(20.0), CarryCapacity: domain.Known(35.0), CarriedMass: domain.Known(5.0)}}), QuestDepartureWork: domain.Known([]policy.PawnWorkAssignment{{Pawn: "a", Priorities: []policy.WorkPriority{{Work: "Research", Priority: 1}}}}), WorkRoster: domain.Known([]policy.WorkCoverage{{Work: "Research", Owners: 2}}), DefenseCapacity: domain.Known(300.0), RaidPoints: domain.Known(40.0), AnimalUpkeep: policy.AnimalUpkeepObservation{Food: domain.Known(policy.FoodSupply{Complete: domain.Known(true), Consumers: []policy.FoodConsumer{{ID: "a", NutritionPerDay: domain.Known(1.0)}, {ID: "home", NutritionPerDay: domain.Known(1.0)}}, Stocks: []policy.FoodStock{{ID: "meal", DefName: "MealSurvivalPack", Count: domain.Known(int64(30)), Nutrition: domain.Known(30.0), Holder: domain.Known(policy.PawnID("")), Eaters: []policy.PawnID{"a", "home"}, Perishable: domain.Known(false), Forbidden: domain.Known(false), UnitMass: domain.Known(.3)}}})}}
}

func TestPrepareTradeMissionNativePackAndSilverReserve(t *testing.T) {
	world := domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan"}
	settlement := bridge.SettlementFact{ID: "settlement", Tile: 3, TraderKind: "bulk", CanTrade: domain.Known(true)}
	f := &missionPackFake{}
	m, option, err := PrepareTradeMission(context.Background(), f, world, 2, settlement, missionPreparationFacts(), policy.DefaultRoundsPolicy(), 100, []domain.CargoItem{{Definition: "Steel", Count: 10}}, nil)
	if err != nil || m.Phase != domain.TradeMissionPlanned || len(f.requests) != 2 || option.Participant.Caravan != "" {
		t.Fatal(m, option, err)
	}
	if f.requests[0].Cargo[0].GetCount() != 1 || f.requests[1].Cargo[0].GetCount() != 3 {
		t.Fatal("wrong food seed/roundtrip", f.requests)
	}
	f = &missionPackFake{}
	if _, _, err = PrepareTradeMission(context.Background(), f, world, 2, settlement, missionPreparationFacts(), policy.DefaultRoundsPolicy(), 401, []domain.CargoItem{{Definition: "Steel", Count: 10}}, nil); err == nil || len(f.requests) != 0 {
		t.Fatal("spent home reserve", err)
	}
	f = &missionPackFake{unsafe: true}
	if _, _, err = PrepareTradeMission(context.Background(), f, world, 2, settlement, missionPreparationFacts(), policy.DefaultRoundsPolicy(), 100, []domain.CargoItem{{Definition: "Steel", Count: 10}}, nil); err == nil {
		t.Fatal("unsafe final native pack admitted")
	}
}

func TestTradeMissionDepartureAdmissionHoldsManualStaleAndAlreadyFormed(t *testing.T) {
	r, db, _, _, _ := roundsFixture(t)
	ctx := context.Background()
	state := r.player.session.State()
	m := tradeMissionFixture()
	m.HomeColony = state.Snapshot.Colony
	m.HomeMap = state.Snapshot.Map
	m.Phase = domain.TradeMissionPlanned
	p, err := domain.NewTradeMissionProject("project-trade", 2, state.Snapshot, m)
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
	read := missionWorldRead()
	read.Context.Identity = boundaryMissionIdentity(state.Snapshot)
	read.Context.NativeGeneration = proto.Uint64(uint64(state.Snapshot.Native))
	read.Maps = []bridge.WorldMap{{ID: int32(m.HomeMap), Tile: m.HomeTile, Home: true, PawnIDs: []string{"a"}}}
	manual := state
	manual.Enabled = false
	if id, err := r.AdmitTradeMissionDeparture(ctx, r.player.epoch, manual, project, read, newStepArbiter()); err == nil || id != "" {
		t.Fatal("manual admission", id, err)
	}
	read.Context.Identity.LoadToken = proto.String("other")
	if id, err := r.AdmitTradeMissionDeparture(ctx, r.player.epoch, state, project, read, newStepArbiter()); err == nil || id != "" {
		t.Fatal("stale world admission", id, err)
	}
	read.Context.Identity = boundaryMissionIdentity(state.Snapshot)
	arbiter := newStepArbiter()
	arbiter.tryClaim([]domain.PawnID{"a"}, "urgent-work")
	if id, err := r.AdmitTradeMissionDeparture(ctx, r.player.epoch, state, project, read, arbiter); err == nil || id != "" {
		t.Fatal("claimed crew dispatched", id, err)
	}
	read.Caravans = []bridge.CaravanJourney{{ID: "caravan", Tile: 2, Moving: true, PawnIDs: []string{"a"}}}
	if id, err := r.AdmitTradeMissionDeparture(ctx, r.player.epoch, state, project, read, newStepArbiter()); err != nil || id != "" {
		t.Fatal("formed crew dispatched", id, err)
	}
	after, err := db.LoadProject(ctx, p.ID)
	if err != nil {
		t.Fatal(err)
	}
	intent, err := domain.DecodeTradeMission(after.Project.Record)
	if err != nil || intent.Phase != domain.TradeMissionOutbound || len(after.History) != 0 {
		t.Fatal(intent, after, err)
	}
	// The durable phase remains the replay guard after native caravan evidence
	// goes missing. Neither a lost receipt nor an absent caravan resets it.
	read.Caravans = nil
	if id, err := r.AdmitTradeMissionDeparture(ctx, r.player.epoch, state, after, read, newStepArbiter()); err == nil || id != "" {
		t.Fatal("uncertain crew resent", id, err)
	}
}

func boundaryMissionIdentity(w domain.GenerationSnapshot) *c.Identity {
	return &c.Identity{ColonyId: proto.String(string(w.Colony)), MapId: proto.Int32(int32(w.Map)), LoadToken: proto.String(string(w.Load))}
}
