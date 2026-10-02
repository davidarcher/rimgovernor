package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	ops "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestBillTakeoverProjectionPreservesDriftAndUnknowns(t *testing.T) {
	bill := &o.BillState{Recipe: &o.DefinitionRef{DefName: proto.String("CookMealSimple")}, Suspended: proto.Bool(true), RepeatMode: ops.RepeatMode_REPEAT_MODE_COUNT.Enum(), DefaultIngredients: proto.Bool(false), UnrestrictedWorker: proto.Bool(false), Worker: &commonpb.Ref{Id: proto.String("pawn")}, IngredientFilter: &o.StockpileFilter{AllowedDefNames: []string{"Rice"}}}
	snapshot := &o.ColonyFactsSnapshot{Cooking: []*o.CookingFacts{{Bench: &commonpb.Ref{Id: proto.String("stove")}, Bills: []*o.BillState{bill}}}}
	if _, k := colonyProductionBenches(snapshot, nil).Value(); k {
		t.Fatal("an unresolved bench became a known census")
	}
	benches, known := colonyProductionBenches(snapshot, buildingRows(&o.BuildingState{Building: &o.EntityRef{Id: proto.String("stove")}})).Value()
	if !known {
		t.Fatal("missing benches")
	}
	got := benches[0].Bills[0]
	if got.Humanlike {
		t.Fatal("ordinary ingredient filter misclassified as human butchery")
	}
	if v, k := got.DefaultIngredients.Value(); !k || v {
		t.Fatal(got)
	}
	if v, k := got.UnrestrictedWorker.Value(); !k || v {
		t.Fatal(got)
	}
	if v, k := got.Worker.Value(); !k || v != "pawn" {
		t.Fatal(got)
	}
	if v, k := got.RepeatMode.Value(); !k || v != "RepeatCount" {
		t.Fatal(got)
	}
	if v, k := got.Ingredients.Value(); !k || len(v) != 1 || v[0] != "Rice" {
		t.Fatal(got)
	}
	bill.DefaultIngredients, bill.UnrestrictedWorker, bill.Worker, bill.IngredientFilter = nil, nil, nil, nil
	benches, _ = colonyProductionBenches(snapshot, buildingRows(&o.BuildingState{Building: &o.EntityRef{Id: proto.String("stove")}})).Value()
	got = benches[0].Bills[0]
	if _, k := got.DefaultIngredients.Value(); k {
		t.Fatal("missing defaults became known")
	}
	if _, k := got.UnrestrictedWorker.Value(); k {
		t.Fatal("missing restriction became known")
	}
	// No worker reference is an unrestricted bill (#1342).
	if v, k := got.Worker.Value(); !k || v != "" {
		t.Fatal("absent worker is not unrestricted")
	}
	if _, k := got.Ingredients.Value(); k {
		t.Fatal("missing filter became empty")
	}
}
