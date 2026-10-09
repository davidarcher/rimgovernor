package facts

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func wireRect(minX, minZ, maxX, maxZ int32) *k.Rectangle {
	return &k.Rectangle{Minimum: &c.Cell{X: proto.Int32(minX), Z: proto.Int32(minZ)}, Maximum: &c.Cell{X: proto.Int32(maxX), Z: proto.Int32(maxZ)}}
}

// fullColonyStore holds every colony-family section plus research, with
// the planning window covering cells (10..19, 10..19).
func fullColonyStore(t *testing.T) *Store {
	t.Helper()
	s := NewStore()
	scope := Scope{Load: "a", Generation: 1}
	for _, section := range []Section{Colony, Zones, Buildings, Research} {
		Put(s, scope, section, Held[string]{Value: string(section), AsOf: 10, Complete: true})
	}
	Put(s, scope, PlanningCells, Held[string]{Value: "cells", AsOf: 10, Complete: true, Region: Rect{MinX: 10, MinZ: 10, MaxX: 19, MaxZ: 19}})
	return s
}

// TestInvalidationFromWire: families decode as bridge.FactFamilyFromWire
// does, the ids and rectangle ride along, and an unknown family refuses.
func TestInvalidationFromWire(t *testing.T) {
	inv, ok := InvalidationFromWire(&k.ObservationInvalidated{Families: []k.FactFamily{k.FactFamily_FACT_FAMILY_COLONY, k.FactFamily_FACT_FAMILY_ROOMS}, EntityIds: []string{"Zone_7"}, Cells: wireRect(3, 4, 5, 6)})
	if !ok || !reflect.DeepEqual(inv.Families, []bridge.FactFamily{bridge.FactColony, bridge.FactRooms}) || !reflect.DeepEqual(inv.IDs, []string{"Zone_7"}) || inv.Rect == nil || *inv.Rect != (Rect{3, 4, 5, 6}) || !inv.Narrowed() {
		t.Fatalf("decoded %+v ok=%v", inv, ok)
	}
	plain, ok := InvalidationFromWire(&k.ObservationInvalidated{Families: []k.FactFamily{k.FactFamily_FACT_FAMILY_RESEARCH}})
	if !ok || plain.Narrowed() || plain.Rect != nil || len(plain.IDs) != 0 {
		t.Fatalf("plain %+v ok=%v", plain, ok)
	}
	if _, ok := InvalidationFromWire(&k.ObservationInvalidated{Families: []k.FactFamily{k.FactFamily(99)}}); ok {
		t.Fatal("an unknown family must refuse")
	}
}

// TestApplyWholeFamily: an invalidation with neither ids nor a rectangle
// drops every section of the family and leaves the others.
func TestApplyWholeFamily(t *testing.T) {
	s := fullColonyStore(t)
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}})
	for _, section := range []Section{Colony, PlanningCells, Zones, Buildings} {
		if fresh(s, section) {
			t.Fatalf("%s survived a whole-family invalidation", section)
		}
	}
	if !fresh(s, Research) {
		t.Fatal("research dropped by the colony family")
	}
}

// TestApplyEntityIDs: ids drop every section of the family, the cell
// section too (no rectangle to check), and leave the other families.
func TestApplyEntityIDs(t *testing.T) {
	s := fullColonyStore(t)
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, IDs: []string{"Zone_7"}})
	for _, section := range []Section{Colony, PlanningCells, Zones, Buildings} {
		if fresh(s, section) {
			t.Fatalf("%s survived an id invalidation", section)
		}
	}
	if !fresh(s, Research) {
		t.Fatal("an id invalidation dropped another family")
	}
}

// TestApplyRectangle: a rectangle drops the cell section when it
// intersects its region and leaves it when it does not;
// an unknown region intersects every rectangle.
func TestApplyRectangle(t *testing.T) {
	s := fullColonyStore(t)
	outside := Rect{MinX: 30, MinZ: 30, MaxX: 31, MaxZ: 31}
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, Rect: &outside})
	if !fresh(s, PlanningCells) {
		t.Fatal("a disjoint rectangle must leave the window held")
	}
	edge := Rect{MinX: 19, MinZ: 0, MaxX: 25, MaxZ: 10}
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, Rect: &edge})
	if fresh(s, PlanningCells) {
		t.Fatal("an intersecting rectangle must drop the window")
	}
	Put(s, s.Scope(), PlanningCells, Held[string]{Value: "cells", AsOf: 20, Complete: true})
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, Rect: &outside})
	if fresh(s, PlanningCells) {
		t.Fatal("an unknown region intersects every rectangle")
	}
	var nilStore *Store
	nilStore.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, Rect: &outside})
}

func TestRect(t *testing.T) {
	a := Rect{MinX: 0, MinZ: 0, MaxX: 5, MaxZ: 5}
	if !a.Intersects(Rect{MinX: 5, MinZ: 5, MaxX: 9, MaxZ: 9}) || a.Intersects(Rect{MinX: 6, MinZ: 0, MaxX: 9, MaxZ: 9}) || a.Intersects(Rect{MinX: 0, MinZ: 6, MaxX: 5, MaxZ: 9}) {
		t.Fatal("intersection")
	}
	if !a.Intersects(Rect{}) || !(Rect{}).Intersects(a) || !(Rect{}).Unknown() {
		t.Fatal("unknown intersects everything")
	}
}
