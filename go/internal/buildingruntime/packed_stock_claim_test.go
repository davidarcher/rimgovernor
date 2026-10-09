package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// Two pieces of one definition in one pass each get their own stored item.
func TestPackedStockHandsEachStoredItemOutOnce(t *testing.T) {
	src := &countingPacked{items: map[string][]bridge.PackedItem{
		policy.PackedFurnitureDefinition: {{ID: "m1", Inner: "b1", InnerDef: "Brazier"}, {ID: "m2", Inner: "b2", InnerDef: "Brazier"}},
	}}
	stock := newPackedStock(src, &c.Identity{})
	var things []string
	for i := 0; i < 3; i++ {
		move, ok, err := stock.Install(context.Background(), policy.PackedFurnitureDefinition, "Brazier", domain.Cell{X: int32(i), Z: 1}, domain.Rotation("north"))
		if err != nil {
			t.Fatal(err)
		}
		if ok {
			things = append(things, move.Thing())
		}
	}
	if len(things) != 2 || things[0] == things[1] {
		t.Fatalf("stored items handed out = %v", things)
	}
}
