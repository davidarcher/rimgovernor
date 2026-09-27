package bridge

import (
	"bytes"
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// The named preset constructors now build a domain.StockpileFilter; their
// CreateZone wire bytes must equal what the pre-filter preset switch
// (legacyStockpileSettings, copied verbatim) sent.
func TestPresetFiltersWireUnchanged(t *testing.T) {
	cells := []domain.Cell{{X: 1, Z: 1}}
	var zones []domain.ZoneCreate
	for _, preset := range []domain.StockpilePreset{domain.FoodPreset, domain.CorpseLarderPreset, domain.GeneralPreset} {
		for _, priority := range []domain.StockpilePriority{domain.CriticalPriority, domain.ImportantPriority, domain.PreferredPriority, domain.NormalPriority, domain.LowPriority} {
			z, err := domain.NewStockpileZone(preset, priority, cells)
			if err != nil {
				t.Fatal(err)
			}
			zones = append(zones, z)
		}
	}
	for _, allow := range [][]string{{"Steel"}, {"WoodLog", "Cloth", "Steel"}} {
		z, err := domain.NewAllowListStockpileZone(domain.LowPriority, allow, cells)
		if err != nil {
			t.Fatal(err)
		}
		zones = append(zones, z)
	}
	det := proto.MarshalOptions{Deterministic: true}
	for _, z := range zones {
		want, _ := det.Marshal(legacyStockpileSettings(z))
		got, _ := det.Marshal(stockpileSettings(z))
		if !bytes.Equal(want, got) {
			t.Fatalf("%s/%s wire drift:\nwant %v\n got %v", z.Preset(), z.Priority(), legacyStockpileSettings(z), stockpileSettings(z))
		}
	}
	if got := zones[len(zones)-1].Allow(); len(got) != 3 || got[0] != "Cloth" || got[2] != "WoodLog" {
		t.Fatal("allow-list not canonical", got)
	}
}

func TestStockpileSettingsCarriesRanges(t *testing.T) {
	f, _ := domain.NewStockpileFilter(domain.BaseEverything, nil, nil)
	if s := StockpileSettings(f, domain.NormalPriority); s.Filter != nil || s.GetPreset() != op.FilterPreset_FILTER_PRESET_EVERYTHING {
		t.Fatal(s)
	}
	f, _ = f.WithHitPoints(0.5, 1)
	f, _ = f.WithQuality("Good", "Legendary")
	s := StockpileSettings(f, domain.NormalPriority)
	if s.Filter.GetHitPointsMin() != 0.5 || s.Filter.GetQualityMin() != "Good" || s.Filter.GetQualityMax() != "Legendary" {
		t.Fatal(s)
	}
}

// The two stockpile write kinds put the same ProtoJSON on the wire the
// zone/delete acceptance case sends by hand.
func TestStockpileWriteOperations(t *testing.T) {
	edit, _ := domain.NewZoneCellEdit("Zone_7", "tok", domain.RemoveZoneCells, []domain.Cell{{X: 2, Z: 3}})
	ea, _ := domain.NewZoneCellEditAction("e", edit)
	got, err := stockpileWriteOperation(ea)
	if err != nil {
		t.Fatal(err)
	}
	want := `{"editZoneCells":{"zone":{"entityId":"Zone_7","expectedSnapshotToken":"tok"},"edit":"CELL_EDIT_REMOVE","cells":{"explicitCells":{"cells":[{"x":2,"z":3}]}}}}`
	if s := compactJSON(t, got); s != want {
		t.Fatal(s)
	}
	patch, _ := domain.NewStockpilePatch(domain.StorageBuildingTarget, "Shelf_1", "tok", domain.GeneralFilter(), domain.CriticalPriority, "shelf:Shelf_1")
	pa, _ := domain.NewStockpilePatchAction("p", patch)
	if got, err = stockpileWriteOperation(pa); err != nil {
		t.Fatal(err)
	}
	want = `{"patchStockpile":{"zone":{"entityId":"Shelf_1","expectedSnapshotToken":"tok"},"settings":{"priority":"STORAGE_PRIORITY_CRITICAL","preset":"FILTER_PRESET_NONPERISHABLES","filter":{"disallow":[{"categoryDef":"Chunks"}]}}}}`
	if s := compactJSON(t, got); s != want {
		t.Fatal(s)
	}
	if _, err := stockpileWriteOperation(domain.Action{}); err == nil {
		t.Fatal("accepted a non-stockpile action")
	}
}

func compactJSON(t *testing.T, m proto.Message) string {
	t.Helper()
	data, err := protojson.Marshal(m)
	if err != nil {
		t.Fatal(err)
	}
	var out bytes.Buffer
	if err := json.Compact(&out, data); err != nil {
		t.Fatal(err)
	}
	return out.String()
}

func legacyStockpileSettings(zone domain.ZoneCreate) *op.StockpileSettings {
	var priority op.StoragePriority
	switch zone.Priority() {
	case domain.CriticalPriority:
		priority = op.StoragePriority_STORAGE_PRIORITY_CRITICAL
	case domain.ImportantPriority:
		priority = op.StoragePriority_STORAGE_PRIORITY_IMPORTANT
	case domain.PreferredPriority:
		priority = op.StoragePriority_STORAGE_PRIORITY_PREFERRED
	case domain.NormalPriority:
		priority = op.StoragePriority_STORAGE_PRIORITY_NORMAL
	case domain.LowPriority:
		priority = op.StoragePriority_STORAGE_PRIORITY_LOW
	}
	var preset op.FilterPreset
	switch zone.Preset() {
	case domain.FoodPreset:
		preset = op.FilterPreset_FILTER_PRESET_FOOD
	case domain.NothingPreset, domain.CorpseLarderPreset:
		preset = op.FilterPreset_FILTER_PRESET_NOTHING
	case domain.GeneralPreset:
		preset = op.FilterPreset_FILTER_PRESET_NONPERISHABLES
	}
	settings := &op.StockpileSettings{Priority: priority.Enum(), Preset: preset.Enum()}
	if zone.Preset() == domain.CorpseLarderPreset {
		settings.Filter = &op.FilterPatch{
			Allow:    []*op.FilterSelector{{Definition: &op.FilterSelector_CategoryDef{CategoryDef: "CorpsesAnimal"}}, {Definition: &op.FilterSelector_SpecialFilterDef{SpecialFilterDef: "AllowFresh"}}},
			Disallow: []*op.FilterSelector{{Definition: &op.FilterSelector_SpecialFilterDef{SpecialFilterDef: "AllowRotten"}}},
		}
	}
	if zone.Preset() == domain.GeneralPreset {
		settings.Filter = &op.FilterPatch{Disallow: []*op.FilterSelector{{Definition: &op.FilterSelector_CategoryDef{CategoryDef: "Chunks"}}}}
	}
	if zone.Preset() == domain.NothingPreset {
		var allow []*op.FilterSelector
		for _, name := range zone.Allow() {
			allow = append(allow, &op.FilterSelector{Definition: &op.FilterSelector_ThingDef{ThingDef: name}})
		}
		settings.Filter = &op.FilterPatch{Allow: allow}
	}
	return settings
}
