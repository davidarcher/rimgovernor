package observation

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestBillTakeoverProjectionPreservesDriftAndUnknowns(t *testing.T) {
	bill := &o.BillState{Recipe: &o.DefinitionRef{DefName: proto.String("CookMealSimple")}, Suspended: proto.Bool(true), RepeatMode: proto.String("RepeatCount"), DefaultIngredients: proto.Bool(false), UnrestrictedWorker: proto.Bool(false), WorkerId: proto.String("pawn"), IngredientFilter: &o.StockpileFilter{AllowedDefNames: []string{"Rice"}}}
	snapshot := &o.ColonyFactsSnapshot{Cooking: []*o.CookingFacts{{Bench: &o.EntityRef{Id: proto.String("stove")}, Bills: []*o.BillState{bill}}}}
	benches, known := colonyProductionBenches(snapshot).Value()
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
	bill.DefaultIngredients, bill.UnrestrictedWorker, bill.WorkerId, bill.IngredientFilter = nil, nil, nil, nil
	benches, _ = colonyProductionBenches(snapshot).Value()
	got = benches[0].Bills[0]
	if _, k := got.DefaultIngredients.Value(); k {
		t.Fatal("missing defaults became known")
	}
	if _, k := got.UnrestrictedWorker.Value(); k {
		t.Fatal("missing restriction became known")
	}
	if _, k := got.Worker.Value(); k {
		t.Fatal("missing worker became unrestricted")
	}
	if _, k := got.Ingredients.Value(); k {
		t.Fatal("missing filter became empty")
	}
}
