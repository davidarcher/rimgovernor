package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func stockpileSiteCell(x, z int32, zone string, stored bool) policy.SiteCell {
	cell := policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(zone != ""), StorageEmpty: domain.Known(!stored)}
	if zone != "" {
		cell.ZoneID = domain.Known(zone)
	}
	return cell
}

// A snapshot over recorded colony facts: the planning cells name each
// zone's cells and the storage-empty flag its used ones; the owned
// stockpile claims (not growing zones, not zones the census lost) become
// the review's zones, a later patch supersedes the created settings and
// role, and a nearly full claimed zone grows through the registered role.
func TestStockpileRequestFromCensusAndClaims(t *testing.T) {
	projection := &observation.ColonyProjection{Bounds: policy.Bounds{Width: 20, Height: 20}, Facts: policy.RoutineFacts{Colonists: domain.Known(int64(2))}}
	projection.Identity.Tick = 5000
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			zone := ""
			switch {
			case x >= 2 && x < 4 && z >= 2 && z < 4:
				zone = "Zone_1"
			case x >= 10 && x < 12 && z >= 10 && z < 12:
				zone = "Zone_2"
			case x == 15 && z == 15:
				zone = "Zone_3"
			}
			projection.Cells = append(projection.Cells, stockpileSiteCell(x, z, zone, zone == "Zone_1" || zone == "Zone_2" && x == 10 && z == 10))
		}
	}
	food := domain.FoodFilter()
	owned := []store.OwnedZone{
		{ID: "Zone_1", Kind: domain.StockpileZone, Role: "general", Filter: domain.GeneralFilter(), Priority: domain.NormalPriority},
		{ID: "Zone_2", Kind: domain.StockpileZone, Filter: domain.GeneralFilter(), Priority: domain.NormalPriority},
		{ID: "Zone_3", Kind: domain.GrowingZone, Crop: "Plant_Rice"},
		{ID: "Zone_9", Kind: domain.StockpileZone, Role: "general"},
	}
	patches := map[string]store.AppliedStockpile{"Zone_2": {Zone: "Zone_2", Filter: food, Priority: domain.PreferredPriority, Role: "kitchen", Tick: 10}}
	request := stockpileRequest(projection, owned, patches)
	if len(request.Zones) != 2 || request.Tick != 5000 {
		t.Fatalf("request zones %+v", request.Zones)
	}
	one, two := request.Zones[0], request.Zones[1]
	if one.ID != "Zone_1" || len(one.Cells) != 4 || one.Used() != 4 || one.Role != "general" {
		t.Fatalf("zone 1 %+v", one)
	}
	if two.ID != "Zone_2" || len(two.Cells) != 4 || two.Used() != 1 || two.Role != "kitchen" || two.Filter != food || two.Priority != domain.PreferredPriority {
		t.Fatalf("zone 2 %+v", two)
	}
	review := policy.PlanStockpileMaintenance(request)
	if len(review.Edits) != 1 || review.Edits[0].Kind != policy.StockpileGrow || review.Edits[0].Zone != "Zone_1" {
		t.Fatalf("review %+v", review)
	}
}

func TestStockpileMemoryTracksLowSincePerWorld(t *testing.T) {
	var m stockpileMemory
	low := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 8)}}
	m.observe("a", 100, low)
	if low[0].LowSince != 100 {
		t.Fatalf("first low %+v", low[0])
	}
	later := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 8)}}
	m.observe("a", 900, later)
	if later[0].LowSince != 100 {
		t.Fatalf("low since reset %+v", later[0])
	}
	read := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 8)}}
	m.fill("a", read)
	if read[0].LowSince != 100 {
		t.Fatalf("fill %+v", read[0])
	}
	full := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 2), Stored: make([]domain.Cell, 2)}}
	m.observe("a", 1000, full)
	again := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 8)}}
	m.observe("a", 2000, again)
	if again[0].LowSince != 2000 {
		t.Fatalf("filled zone kept its low tick %+v", again[0])
	}
	other := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 8)}}
	m.fill("b", other)
	if other[0].LowSince != 0 {
		t.Fatal("memory crossed worlds")
	}
}

func TestStockpileEditActionsCarryTheZoneToken(t *testing.T) {
	cells := []domain.Cell{{X: 1, Z: 1}}
	for _, tc := range []struct {
		edit policy.StockpileEdit
		kind domain.ActionKind
	}{
		{policy.StockpileEdit{Kind: policy.StockpileGrow, Zone: "Zone_1", Cells: cells}, domain.ZoneCellEditAction},
		{policy.StockpileEdit{Kind: policy.StockpileShrink, Zone: "Zone_1", Cells: cells}, domain.ZoneCellEditAction},
		{policy.StockpileEdit{Kind: policy.StockpileRetarget, Zone: "Zone_1", Filter: domain.FoodFilter(), Priority: domain.ImportantPriority, Role: "kitchen"}, domain.StockpilePatchAction},
		{policy.StockpileEdit{Kind: policy.StockpileDelete, Zone: "Zone_1"}, domain.ZoneDeleteAction},
		{policy.StockpileEdit{Kind: policy.StockpileMerge, Zone: "Zone_1", Into: "Zone_2"}, domain.ZoneDeleteAction},
	} {
		a, err := stockpileEditAction("a-0", tc.edit, "tok")
		if err != nil || a.Kind() != tc.kind {
			t.Fatalf("%s: %v %v", tc.edit.Kind, a.Kind(), err)
		}
		if e, ok := a.ZoneCellEdit(); ok {
			want := domain.AddZoneCells
			if tc.edit.Kind == policy.StockpileShrink {
				want = domain.RemoveZoneCells
			}
			if e.Mode() != want || e.BeforeToken() != "tok" {
				t.Fatalf("edit %+v", e)
			}
		}
		if p, ok := a.StockpilePatch(); ok && (p.Role() != "kitchen" || p.TargetKind() != domain.StorageZoneTarget || p.BeforeToken() != "tok") {
			t.Fatalf("patch %+v", p)
		}
	}
}

func TestStockpileRolesResolveByPrefix(t *testing.T) {
	RegisterStockpileRole("test-role", func(_ *observation.ColonyProjection, role string) (policy.StockpileRoleState, bool) {
		return policy.StockpileRoleState{Retired: role == "test-role:gone"}, true
	})
	roles := stockpileRoles(&observation.ColonyProjection{})
	if state, ok := roles("test-role:gone"); !ok || !state.Retired {
		t.Fatal("prefixed role unresolved")
	}
	if state, ok := roles("test-role"); !ok || state.Retired {
		t.Fatal("bare role unresolved")
	}
	if _, ok := roles("test-roleX"); ok {
		t.Fatal("unregistered role resolved")
	}
	defer func() {
		if recover() == nil {
			t.Fatal("double registration accepted")
		}
	}()
	RegisterStockpileRole("test-role", func(*observation.ColonyProjection, string) (policy.StockpileRoleState, bool) {
		return policy.StockpileRoleState{}, false
	})
}
