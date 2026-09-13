package domain

import "testing"

func TestZoneCanonicalConnectedAndImmutable(t *testing.T) {
	cells := []Cell{{X: 2, Z: 1}, {X: 1, Z: 1}}
	z, err := NewZoneCreate(GrowingZone, "Plant_Rice", cells)
	if err != nil {
		t.Fatal(err)
	}
	cells[0].X = 8
	got := z.Cells()
	got[0].X = 9
	same, err := NewZoneCreate(GrowingZone, "Plant_Rice", []Cell{{X: 1, Z: 1}, {X: 2, Z: 1}})
	if err != nil || z != same {
		t.Fatal("mutable/canonical", z, err)
	}
	for _, bad := range [][]Cell{nil, {{X: 1, Z: 1}, {X: 1, Z: 1}}, {{X: 1, Z: 1}, {X: 3, Z: 1}}, {{X: -1, Z: 0}}} {
		if _, err := NewZoneCreate(GrowingZone, "Plant_Rice", bad); err == nil {
			t.Fatal(bad)
		}
	}
}

func TestStockpileZoneCanonicalAndClosedToFoodImportant(t *testing.T) {
	cells := []Cell{{X: 2, Z: 1}, {X: 1, Z: 1}}
	z, err := NewStockpileZone(FoodPreset, ImportantPriority, cells)
	if err != nil {
		t.Fatal(err)
	}
	if z.Kind() != StockpileZone || z.Preset() != FoodPreset || z.Priority() != ImportantPriority || z.Crop() != "" {
		t.Fatal("unexpected stockpile zone fields", z)
	}
	if z.Label() != "RimGovernor food storage" {
		t.Fatal("unexpected label", z.Label())
	}
	same, err := NewStockpileZone(FoodPreset, ImportantPriority, []Cell{{X: 1, Z: 1}, {X: 2, Z: 1}})
	if err != nil || z != same {
		t.Fatal("mutable/canonical", z, err)
	}
	for _, bad := range []struct {
		preset   StockpilePreset
		priority StockpilePriority
	}{{"nothing", ImportantPriority}, {FoodPreset, "normal"}, {"", ""}} {
		if _, err := NewStockpileZone(bad.preset, bad.priority, cells); err == nil {
			t.Fatal(bad)
		}
	}
}

func TestReconstructZoneRoundTripsBothKinds(t *testing.T) {
	cells := []Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}
	growing, err := NewZoneCreate(GrowingZone, "Plant_Rice", cells)
	if err != nil {
		t.Fatal(err)
	}
	stockpile, err := NewStockpileZone(FoodPreset, ImportantPriority, cells)
	if err != nil {
		t.Fatal(err)
	}
	for _, z := range []ZoneCreate{growing, stockpile} {
		got, err := ReconstructZone(z)
		if err != nil || got != z {
			t.Fatal("reconstruct mismatch", z, got, err)
		}
	}
	if _, err := ReconstructZone(ZoneCreate{kind: "bogus"}); err == nil {
		t.Fatal("expected unsupported zone kind error")
	}
}

func TestNewZoneCreateActionRejectsNonCanonicalZone(t *testing.T) {
	value, err := NewStockpileZone(FoodPreset, ImportantPriority, []Cell{{X: 1, Z: 1}})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := NewZoneCreateAction("action-1", value); err != nil {
		t.Fatal(err)
	}
	value.crop = "Plant_Rice"
	if _, err := NewZoneCreateAction("action-1", value); err == nil {
		t.Fatal("expected rejection of tampered zone value")
	}
	if _, err := NewZoneCreateAction("", value); err == nil {
		t.Fatal("expected rejection of invalid action id")
	}
}
