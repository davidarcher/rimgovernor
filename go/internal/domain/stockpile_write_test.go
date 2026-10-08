package domain

import (
	"encoding/json"
	"testing"
)

// The warehouse takes packed buildings (#2103): the indoor_only preset never
// lists a building def (it cannot deteriorate, so it is outdoor-safe), so the
// Buildings category, which holds a chair's def, carries the minified chair.
func TestWarehouseFilterAcceptsPackedFurniture(t *testing.T) {
	f := GeneralFilter()
	if f.Base() != BaseIndoorOnly {
		t.Fatalf("base %s", f.Base())
	}
	if got := f.Allow(); len(got) != 1 || got[0] != CategoryDef("Buildings") {
		t.Fatalf("allow %v", got)
	}
	// The burnable belongs to the waste yard (#2192).
	if got := f.Disallow(); len(got) != 1 || got[0] != SpecialFilter(BurnableFilterDef) {
		t.Fatalf("disallow %v, want only the burnable special", got)
	}
}

func TestStockpileFilterCanonical(t *testing.T) {
	a, err := NewStockpileFilter(BaseNothing, []FilterSelector{SpecialFilter("AllowFresh"), ThingDef("Steel"), CategoryDef("Meals"), ThingDef("Cloth")}, nil)
	if err != nil {
		t.Fatal(err)
	}
	b, err := NewStockpileFilter(BaseNothing, []FilterSelector{ThingDef("Cloth"), CategoryDef("Meals"), ThingDef("Steel"), SpecialFilter("AllowFresh")}, nil)
	if err != nil || a != b {
		t.Fatal("order-dependent filter", a, b, err)
	}
	if got := a.Allow(); got[0] != ThingDef("Cloth") || got[3] != SpecialFilter("AllowFresh") {
		t.Fatal(got)
	}
	for _, bad := range [][]FilterSelector{{ThingDef("A"), ThingDef("A")}, {{Kind: "x", Name: "A"}}, {ThingDef(" ")}} {
		if _, err := NewStockpileFilter(BaseEverything, bad, nil); err == nil {
			t.Fatal("accepted", bad)
		}
	}
	if _, err := NewStockpileFilter(BaseEverything, []FilterSelector{ThingDef("A")}, []FilterSelector{ThingDef("A")}); err == nil {
		t.Fatal("accepted overlapping allow/disallow")
	}
	if _, err := NewStockpileFilter("bogus", nil, nil); err == nil {
		t.Fatal("accepted unknown base")
	}
	if _, err := a.WithHitPoints(0.6, 0.5); err == nil {
		t.Fatal("accepted inverted range")
	}
	if _, err := a.WithQuality("Legendary", "Awful"); err == nil {
		t.Fatal("accepted inverted quality")
	}
	ranged, _ := a.WithHitPoints(0.5, 1)
	ranged, _ = ranged.WithQuality("Normal", "Legendary")
	data, _ := json.Marshal(ranged)
	var back StockpileFilter
	if err := json.Unmarshal(data, &back); err != nil || back != ranged {
		t.Fatal("json round trip", string(data), err)
	}
	if r, err := ReconstructStockpileFilter(ranged); err != nil || r != ranged {
		t.Fatal(r, err)
	}
}

func TestFilteredStockpileZoneRoleRoundTrips(t *testing.T) {
	f, _ := NewStockpileFilter(BaseEverything, nil, []FilterSelector{CategoryDef("Chunks")})
	z, err := NewFilteredStockpileZone(f, LowPriority, stockpileTestRectangle([]Cell{{X: 1, Z: 1}}))
	if err != nil {
		t.Fatal(err)
	}
	if z, err = z.WithRole("dump:worn"); err != nil || z.Role() != "dump:worn" || z.Filter() != f {
		t.Fatal(z, err)
	}
	if r, err := ReconstructZone(z); err != nil || r != z {
		t.Fatal("reconstruct", r, err)
	}
	if _, err := NewZoneCreateAction("a", z); err != nil {
		t.Fatal(err)
	}
	legacy, _ := allowListZone(ImportantPriority, []string{"Steel", "Cloth"}, []Cell{{X: 1, Z: 1}})
	if r, err := ReconstructZone(legacy); err != nil || r != legacy || legacy.Role() != "" || len(allowOf(legacy)) != 2 {
		t.Fatal("legacy reconstruct", r, err)
	}
	crop, _ := NewZoneCreate(GrowingZone, "Plant_Rice", []Cell{{X: 1, Z: 1}})
	if _, err := crop.WithRole("general"); err == nil {
		t.Fatal("growing zone took a role")
	}
}

func TestZoneCellEditAndStockpilePatchActions(t *testing.T) {
	e, err := NewZoneCellEdit("Zone_1", RemoveZoneCells, []Cell{{X: 3, Z: 1}, {X: 1, Z: 1}})
	if err != nil || e.Cells()[0] != (Cell{X: 1, Z: 1}) {
		t.Fatal(e, err)
	}
	if _, err := NewZoneCellEdit("Zone_1", "grow", []Cell{{X: 1}}); err == nil {
		t.Fatal("accepted unknown mode")
	}
	if _, err := NewZoneCellEdit("Zone_1", AddZoneCells, []Cell{{X: 1}, {X: 1}}); err == nil {
		t.Fatal("accepted duplicate cell")
	}
	ea, err := NewZoneCellEditAction("e", e)
	if got, ok := ea.ZoneCellEdit(); err != nil || !ok || got != e || ea.Kind() != ZoneCellEditAction {
		t.Fatal(ea, err)
	}
	p, err := NewStockpilePatch(StorageBuildingTarget, "Shelf1", GeneralFilter(), CriticalPriority, "shelf:Shelf1")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := NewStockpilePatch("thing", "Shelf1", GeneralFilter(), CriticalPriority, ""); err == nil {
		t.Fatal("accepted unknown target kind")
	}
	pa, err := NewStockpilePatchAction("p", p)
	if got, ok := pa.StockpilePatch(); err != nil || !ok || got != p {
		t.Fatal(pa, err)
	}
	plan, err := NewPlan("plan", 1, []Action{ea, pa})
	if err != nil || plan.Actions()[1] != pa {
		t.Fatal(plan, err)
	}
}
