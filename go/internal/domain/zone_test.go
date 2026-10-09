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
	z, err := NewFilteredStockpileZone(FoodFilter(), ImportantPriority, stockpileTestRectangle(cells))
	if err != nil {
		t.Fatal(err)
	}
	if z.Kind() != StockpileZone || z.Filter() != FoodFilter() || z.Priority() != ImportantPriority || z.Crop() != "" {
		t.Fatal("unexpected stockpile zone fields", z)
	}
	if z.Label() != "Food storage" {
		t.Fatal("unexpected label", z.Label())
	}
	same, err := NewFilteredStockpileZone(FoodFilter(), ImportantPriority, stockpileTestRectangle([]Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}))
	if err != nil || z != same {
		t.Fatal("mutable/canonical", z, err)
	}
	for _, priority := range []StockpilePriority{"urgent", ""} {
		if _, err := NewFilteredStockpileZone(FoodFilter(), priority, stockpileTestRectangle(cells)); err == nil {
			t.Fatal(priority)
		}
	}
}

// The named filters keep the labels their retired presets sent native.
func TestStockpileLabelsFollowFilters(t *testing.T) {
	cells := []Cell{{X: 1, Z: 1}}
	larder, _ := NewFilteredStockpileZone(CorpseLarderFilter(), ImportantPriority, stockpileTestRectangle(cells))
	dump, _ := allowListZone(LowPriority, []string{"ChunkGranite"}, cells)
	supplies, _ := allowListZone(ImportantPriority, []string{"WoodLog", "Steel", "Cloth"}, cells)
	many, _ := allowListZone(ImportantPriority, []string{"WoodLog", "Steel", "Cloth", "Silver", "Gold"}, cells)
	for z, want := range map[ZoneCreate]string{larder: "Corpse larder", dump: "Dumping", supplies: "Cloth, Steel, WoodLog", many: "Cloth, Gold, Silver +2 more"} {
		if z.Label() != want {
			t.Fatal(z.Label(), want)
		}
	}
}

func allowListZone(priority StockpilePriority, allow []string, cells []Cell) (ZoneCreate, error) {
	f, err := AllowOnlyFilter(allow)
	if err != nil {
		return ZoneCreate{}, err
	}
	return NewFilteredStockpileZone(f, priority, stockpileTestRectangle(cells))
}

func allowOf(z ZoneCreate) []string {
	names, _ := z.Filter().AllowOnlyDefinitions()
	return names
}

func TestReconstructZoneRoundTripsBothKinds(t *testing.T) {
	cells := []Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}
	growing, err := NewZoneCreate(GrowingZone, "Plant_Rice", cells)
	if err != nil {
		t.Fatal(err)
	}
	stockpile, err := NewFilteredStockpileZone(FoodFilter(), ImportantPriority, stockpileTestRectangle(cells))
	if err != nil {
		t.Fatal(err)
	}
	allowList, err := allowListZone(ImportantPriority, []string{"MealSimple"}, cells)
	if err != nil {
		t.Fatal(err)
	}
	general, err := NewFilteredStockpileZone(OpeningStoreFilter(), NormalPriority, stockpileTestRectangle(cells))
	if err != nil || general.Label() != "General store" {
		t.Fatal(general, err)
	}
	for _, z := range []ZoneCreate{growing, stockpile, allowList, general} {
		got, err := ReconstructZone(z)
		if err != nil || got != z {
			t.Fatal("reconstruct mismatch", z, got, err)
		}
	}
	if _, err := ReconstructZone(ZoneCreate{kind: "bogus"}); err == nil {
		t.Fatal("expected unsupported zone kind error")
	}
	if _, err := ReconstructZone(ZoneCreate{kind: StockpileZone, priority: "bogus"}); err == nil {
		t.Fatal("expected invalid stockpile error")
	}
}

func TestAllowListStockpileZoneCanonicalAndBounded(t *testing.T) {
	cells := []Cell{{X: 2, Z: 1}, {X: 1, Z: 1}}
	names := []string{"MealSimple", "MealFine"}
	z, err := allowListZone(ImportantPriority, names, cells)
	if err != nil {
		t.Fatal(err)
	}
	if z.Kind() != StockpileZone || z.Filter().Base() != BaseNothing || z.Priority() != ImportantPriority {
		t.Fatal("unexpected allow-list zone fields", z)
	}
	if z.Label() != "MealFine, MealSimple" {
		t.Fatal("unexpected label", z.Label())
	}
	names[0] = "Tampered"
	if got := allowOf(z); len(got) != 2 || got[0] != "MealFine" || got[1] != "MealSimple" {
		t.Fatal("allow-list not canonical/immutable", got)
	}
	same, err := allowListZone(ImportantPriority, []string{"MealFine", "MealSimple"}, []Cell{{X: 1, Z: 1}, {X: 2, Z: 1}})
	if err != nil || z != same {
		t.Fatal("mutable/canonical", z, same, err)
	}
	for _, bad := range []struct {
		priority StockpilePriority
		allow    []string
	}{
		{"urgent", names},
		{ImportantPriority, nil},
	} {
		if _, err := allowListZone(bad.priority, bad.allow, cells); err == nil {
			t.Fatal(bad)
		}
	}
}

func TestNewZoneCreateActionRejectsNonCanonicalZone(t *testing.T) {
	value, err := NewFilteredStockpileZone(FoodFilter(), ImportantPriority, stockpileTestRectangle([]Cell{{X: 1, Z: 1}}))
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

// TestStockpilePrioritiesSpanVanilla pins: every vanilla storage
// priority reaches native, not just Important and Low.
func TestStockpilePrioritiesSpanVanilla(t *testing.T) {
	cells := []Cell{{X: 1, Z: 1}}
	for _, p := range []StockpilePriority{CriticalPriority, ImportantPriority, PreferredPriority, NormalPriority, LowPriority} {
		if z, err := NewFilteredStockpileZone(FoodFilter(), p, stockpileTestRectangle(cells)); err != nil || z.Priority() != p {
			t.Fatal(p, err)
		}
		if _, err := allowListZone(p, []string{"Steel"}, cells); err != nil {
			t.Fatal(p, err)
		}
	}
}
