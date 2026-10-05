package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type countingPacked struct {
	items map[string][]bridge.PackedItem
	reads int
}

func (p *countingPacked) ReadPackedItems(_ context.Context, _ *c.Identity, packedDef string) ([]bridge.PackedItem, bridge.Result, error) {
	p.reads++
	return p.items[packedDef], bridge.Result{}, nil
}

func TestPackedStockInstallsStoredPieceOnce(t *testing.T) {
	src := &countingPacked{items: map[string][]bridge.PackedItem{
		policy.PackedFurnitureDefinition: {{ID: "m1", Inner: "bed7", InnerDef: "DoubleBed"}},
	}}
	stock := newPackedStock(src, &c.Identity{})
	cell := domain.Cell{X: 4, Z: 5}
	move, ok, err := stock.Install(context.Background(), policy.PackedFurnitureDefinition, "DoubleBed", cell, domain.Rotation("east"))
	if err != nil || !ok {
		t.Fatalf("stock install = %v, %v", ok, err)
	}
	if move.Thing() != "bed7" || move.Definition() != "DoubleBed" || move.Cell() != cell || move.Rotation() != domain.Rotation("east") {
		t.Fatalf("move %+v", move)
	}
	if _, ok, _ := stock.Install(context.Background(), policy.PackedFurnitureDefinition, "Bed", cell, domain.Rotation("north")); ok {
		t.Fatal("a def with no stock must report none")
	}
	if src.reads != 1 {
		t.Fatalf("packed reads = %d, want 1 shared across owners", src.reads)
	}
}

func TestPackedStockWithoutSourceHasNone(t *testing.T) {
	stock := newPackedStock(struct{}{}, &c.Identity{})
	if _, ok, err := stock.Install(context.Background(), policy.PackedFurnitureDefinition, "Bed", domain.Cell{}, domain.Rotation("north")); ok || err != nil {
		t.Fatalf("no source = %v, %v", ok, err)
	}
}
