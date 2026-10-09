package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func TestBillOrderSpecReadsNativeIdentity(t *testing.T) {
	recipe := &o.DefinitionRef{DefName: proto.String("Make_Vest")}
	count := &o.BillState{Recipe: recipe, RepeatMode: op.RepeatMode_REPEAT_MODE_COUNT.Enum(), RepeatCount: proto.Int32(3), DefaultIngredients: proto.Bool(true)}
	spec, known := billOrderSpec(count, "TableTailor").Value()
	if !known || spec.Mode != domain.GearBatch || spec.Target != 3 || spec.BenchKind != "TableTailor" || len(spec.Ingredients) != 0 || spec.Worker != "" {
		t.Fatalf("count spec = %+v %v", spec, known)
	}
	target := &o.BillState{Recipe: recipe, RepeatMode: op.RepeatMode_REPEAT_MODE_TARGET.Enum(), TargetCount: proto.Int32(10), DefaultIngredients: proto.Bool(false),
		IngredientFilter: &o.StockpileFilter{AllowedDefNames: []string{"Cloth"}}, Worker: &c.Ref{Id: proto.String("Pawn_1")}}
	spec, known = billOrderSpec(target, "TableTailor").Value()
	if !known || spec.Mode != domain.StockTarget || spec.Target != 10 || len(spec.Ingredients) != 1 || spec.Worker != "Pawn_1" {
		t.Fatalf("target spec = %+v %v", spec, known)
	}
	forever := &o.BillState{Recipe: recipe, RepeatMode: op.RepeatMode_REPEAT_MODE_FOREVER.Enum(), DefaultIngredients: proto.Bool(true)}
	if spec, known = billOrderSpec(forever, "k").Value(); !known || spec.Mode != domain.ButcherForever || spec.Target != 0 {
		t.Fatalf("forever spec = %+v %v", spec, known)
	}
	// A missing count, or an unread default-filter flag, is unknown.
	for name, bill := range map[string]*o.BillState{
		"count":   {Recipe: recipe, RepeatMode: op.RepeatMode_REPEAT_MODE_COUNT.Enum(), DefaultIngredients: proto.Bool(true)},
		"filter":  {Recipe: recipe, RepeatMode: op.RepeatMode_REPEAT_MODE_FOREVER.Enum()},
		"nomode":  {Recipe: recipe, DefaultIngredients: proto.Bool(true)},
		"nocount": {Recipe: recipe, RepeatMode: op.RepeatMode_REPEAT_MODE_TARGET.Enum(), DefaultIngredients: proto.Bool(true)},
	} {
		if _, known := billOrderSpec(bill, "k").Value(); known {
			t.Errorf("%s: an unread bill has a spec", name)
		}
	}
}
