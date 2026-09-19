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
// drops the family's sections, as InvalidateFamily does; the incremental
// sections are kept and marked wholly stale (#357, #358), so a colony
// invalidation forces a changed_since read of each rather than a full one.
func TestApplyWholeFamily(t *testing.T) {
	s := fullColonyStore(t)
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}})
	if _, ok := Get[string](s, Colony); ok {
		t.Fatal("colony survived a whole-family invalidation")
	}
	for _, section := range []Section{PlanningCells, Zones, Buildings} {
		if held, ok := Get[string](s, section); !ok || !held.Stale.All || s.Fresh(section, 10) {
			t.Fatalf("%s = %+v ok=%v", section, held, ok)
		}
	}
	if !s.Fresh(Research, 10) {
		t.Fatal("research dropped by the colony family")
	}
}

// TestApplyEntityIDs: ids mark the family's entity sections stale, keep
// their values, and leave a cell section (no rectangle to check) marked
// whole; a second event unions the ids.
func TestApplyEntityIDs(t *testing.T) {
	s := fullColonyStore(t)
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, IDs: []string{"Zone_7"}})
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, IDs: []string{"Zone_7", "Zone_8"}})
	for _, section := range []Section{Colony, Zones, Buildings} {
		held, ok := Get[string](s, section)
		if !ok || held.Value != string(section) {
			t.Fatalf("%s value dropped: %+v ok=%v", section, held, ok)
		}
		if !reflect.DeepEqual(held.Stale.IDs, []string{"Zone_7", "Zone_8"}) || held.Stale.All || held.Stale.Rect != nil {
			t.Fatalf("%s stale = %+v", section, held.Stale)
		}
		if s.Fresh(section, 10) {
			t.Fatalf("%s reads fresh while marked", section)
		}
	}
	cells, ok := Get[string](s, PlanningCells)
	if !ok || !cells.Stale.All || s.Fresh(PlanningCells, 10) {
		t.Fatalf("cells without a rectangle must be marked whole: %+v ok=%v", cells.Stale, ok)
	}
	if !s.Fresh(Research, 10) {
		t.Fatal("research touched by a colony invalidation")
	}
	status := s.Status()
	if len(status) != 5 || status[0].Section != Colony || !status[0].Stale.Any() || status[2].Section != Research || status[2].Stale.Any() {
		t.Fatalf("status = %+v", status)
	}
	// A fresh put clears the mark.
	Put(s, s.Scope(), Zones, Held[string]{Value: "zones2", AsOf: 20, Complete: true})
	if held, _ := Get[string](s, Zones); held.Stale.Any() || !s.Fresh(Zones, 20) {
		t.Fatalf("put kept the mark: %+v", held.Stale)
	}
}

// TestApplyRectangle: a rectangle marks the cell section when it
// intersects its region and leaves it fresh when it does not; entity
// sections without ids are marked whole; ids and a rectangle together
// narrow both shapes.
func TestApplyRectangle(t *testing.T) {
	s := fullColonyStore(t)
	outside := Rect{MinX: 30, MinZ: 30, MaxX: 31, MaxZ: 31}
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, Rect: &outside})
	if !s.Fresh(PlanningCells, 10) {
		t.Fatal("a disjoint rectangle must leave the window fresh")
	}
	for _, section := range []Section{Colony, Zones, Buildings} {
		if held, _ := Get[string](s, section); !held.Stale.All {
			t.Fatalf("%s without ids must be marked whole: %+v", section, held.Stale)
		}
	}

	s = fullColonyStore(t)
	edge := Rect{MinX: 19, MinZ: 0, MaxX: 25, MaxZ: 10}
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, IDs: []string{"Zone_7"}, Rect: &edge})
	cells, _ := Get[string](s, PlanningCells)
	if cells.Stale.Rect == nil || *cells.Stale.Rect != edge || cells.Stale.All || s.Fresh(PlanningCells, 10) {
		t.Fatalf("an intersecting rectangle must mark the window: %+v", cells.Stale)
	}
	zones, _ := Get[string](s, Zones)
	if !reflect.DeepEqual(zones.Stale.IDs, []string{"Zone_7"}) || zones.Stale.All || zones.Stale.Rect != nil {
		t.Fatalf("zones stale = %+v", zones.Stale)
	}
	second := Rect{MinX: 0, MinZ: 15, MaxX: 12, MaxZ: 16}
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, IDs: []string{"Zone_9"}, Rect: &second})
	cells, _ = Get[string](s, PlanningCells)
	if *cells.Stale.Rect != (Rect{MinX: 0, MinZ: 0, MaxX: 25, MaxZ: 16}) {
		t.Fatalf("rectangles must union: %+v", *cells.Stale.Rect)
	}
	// A window put without a region is conservatively intersected.
	Put(s, s.Scope(), PlanningCells, Held[string]{Value: "cells", AsOf: 20, Complete: true})
	s.Apply(Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, Rect: &outside})
	if s.Fresh(PlanningCells, 20) {
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
	if got := a.Union(Rect{MinX: 3, MinZ: 7, MaxX: 8, MaxZ: 9}); got != (Rect{MinX: 0, MinZ: 0, MaxX: 8, MaxZ: 9}) {
		t.Fatalf("union = %+v", got)
	}
	if !a.Union(Rect{}).Unknown() {
		t.Fatal("union with unknown is unknown")
	}
}
