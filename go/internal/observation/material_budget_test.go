package observation

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The Go material budget is the native one it replaced (#1354): free
// stock less every blueprint's and frame's undelivered material and every
// other pawn's live bill-job ingredients.
func TestMaterialBudgetFromFrameRows(t *testing.T) {
	need := func(def string, n int64) *o.MaterialDeficit {
		return &o.MaterialDeficit{DefName: proto.String(def), StillNeeded: proto.Int64(n)}
	}
	sites := &o.BuildingsSnapshot{Buildings: []*o.BuildingState{
		{Status: o.BuildingStatus_BUILDING_STATUS_BLUEPRINT.Enum(), Construction: &o.ConstructionState{Resources: []*o.MaterialDeficit{need("Steel", 30), need("WoodLog", 0)}}},
		{Status: o.BuildingStatus_BUILDING_STATUS_FRAME.Enum(), Construction: &o.ConstructionState{Resources: []*o.MaterialDeficit{need("BlocksGranite", 5)}}},
		// A built building owes nothing, whatever its row says.
		{Status: o.BuildingStatus_BUILDING_STATUS_BUILT.Enum(), Construction: &o.ConstructionState{Resources: []*o.MaterialDeficit{need("Steel", 99)}}},
	}}
	quantity := func(def string, n int64) *o.Quantity {
		return &o.Quantity{DefName: proto.String(def), Units: proto.Int64(n)}
	}
	bills := &o.BillsSnapshot{Benches: []*o.BillStack{{Bills: []*o.BillState{
		{Reservations: []*o.IngredientReservation{{PawnId: proto.String("Human1"), Items: []*o.Quantity{quantity("Cloth", 20)}}}},
		{Reservations: []*o.IngredientReservation{{PawnId: proto.String("Human2"), Items: []*o.Quantity{quantity("Steel", 10), quantity("Cloth", 5)}}}},
	}}}}
	stock := domain.Known([]policy.Amount{{Resource: "Steel", Count: 50}, {Resource: "Cloth", Count: 40}, {Resource: "WoodLog", Count: 80}})
	deficit, reservations := ConstructionDeficit(sites), BillReservations(bills)

	got, known := policy.MaterialBudget(stock, deficit, reservations, "").Value()
	want := map[policy.Resource]int64{"Steel": 10, "Cloth": 15, "WoodLog": 80, "BlocksGranite": -5}
	if !known || !reflect.DeepEqual(got, want) {
		t.Fatalf("budget = %v (known %v), want %v", got, known, want)
	}
	// The worker's own bill job is not held against it.
	got, _ = policy.MaterialBudget(stock, deficit, reservations, "Human1").Value()
	if got["Cloth"] != 35 || got["Steel"] != 10 {
		t.Fatalf("worker budget = %v", got)
	}
	if _, known := BillReservations(nil).Value(); known {
		t.Fatal("no bill census must leave reservations unknown")
	}
	if _, known := policy.MaterialBudget(domain.Unknown[[]policy.Amount](), deficit, reservations, "").Value(); known {
		t.Fatal("unknown stock must leave the budget unknown")
	}
}
