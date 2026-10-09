package bridge

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// legacyZone is a zone the retired preset constructors built, with
// the preset and canonical allow-list they named it by.
type legacyZone struct {
	domain.ZoneCreate
	preset string
	allow  []string
}

// The named filters replace the retired presets; their zone intent wire
// bytes (settings and label) must equal what the preset switch
// (legacyStockpileSettings, copied verbatim) and preset labels sent.
func TestPresetFiltersWireUnchanged(t *testing.T) {
	cells := []domain.Cell{{X: 1, Z: 1}}
	var zones []legacyZone
	presets := map[string]domain.StockpileFilter{"food": domain.FoodFilter(), "corpse_larder": domain.CorpseLarderFilter(), "general": domain.OpeningStoreFilter()}
	labels := map[string]string{"food": "Food storage", "corpse_larder": "Corpse larder", "general": "General store"}
	for preset, filter := range presets {
		for _, priority := range []domain.StockpilePriority{domain.CriticalPriority, domain.ImportantPriority, domain.PreferredPriority, domain.NormalPriority, domain.LowPriority} {
			z, err := domain.NewFilteredStockpileZone(filter, priority, stockpileTestRectangle(cells))
			if err != nil {
				t.Fatal(err)
			}
			if z.Label() != labels[preset] {
				t.Fatal(preset, z.Label())
			}
			zones = append(zones, legacyZone{z, preset, nil})
		}
	}
	for _, allow := range [][]string{{"Steel"}, {"Cloth", "Steel", "WoodLog"}} {
		for priority, label := range map[domain.StockpilePriority]string{domain.LowPriority: "Dumping", domain.ImportantPriority: strings.Join(allow, ", ")} {
			f, err := domain.AllowOnlyFilter(allow)
			if err != nil {
				t.Fatal(err)
			}
			z, err := domain.NewFilteredStockpileZone(f, priority, stockpileTestRectangle(cells))
			if err != nil || z.Label() != label {
				t.Fatal(z.Label(), err)
			}
			zones = append(zones, legacyZone{z, "nothing", allow})
		}
	}
	det := proto.MarshalOptions{Deterministic: true}
	for _, z := range zones {
		want, _ := det.Marshal(legacyStockpileSettings(z))
		got, _ := det.Marshal(stockpileSettings(z.ZoneCreate))
		if !bytes.Equal(want, got) {
			t.Fatalf("%s/%s wire drift:\nwant %v\n got %v", z.preset, z.Priority(), legacyStockpileSettings(z), stockpileSettings(z.ZoneCreate))
		}
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

// Every zone write is one zone intent and puts the same ProtoJSON on the wire the
// zone/delete acceptance case sends by hand; no CAS token is sent.
func TestZoneIntentActions(t *testing.T) {
	edit, _ := domain.NewZoneCellEdit("Zone_7", domain.RemoveZoneCells, []domain.Cell{{X: 2, Z: 3}})
	ea, _ := domain.NewZoneCellEditAction("e", edit)
	got, err := IntentAction("k", ea)
	if err != nil {
		t.Fatal(err)
	}
	want := `{"key":"k","zone":{"zone":{"id":"Zone_7"},"removeCells":{"explicitCells":{"cells":[{"x":2,"z":3}]}}}}`
	if s := compactJSON(t, got); s != want {
		t.Fatal(s)
	}
	patch, _ := domain.NewStockpilePatch(domain.StorageBuildingTarget, "Shelf_1", domain.OpeningStoreFilter(), domain.CriticalPriority, "shelf:Shelf_1")
	pa, _ := domain.NewStockpilePatchAction("p", patch)
	if got, err = IntentAction("k", pa); err != nil {
		t.Fatal(err)
	}
	want = `{"key":"k","zone":{"zone":{"id":"Shelf_1"},"stockpile":{"priority":"STORAGE_PRIORITY_CRITICAL","preset":"FILTER_PRESET_NONPERISHABLES","filter":{"disallow":[{"categoryDef":"Chunks"}]}}}}`
	if s := compactJSON(t, got); s != want {
		t.Fatal(s)
	}
	del, _ := domain.NewZoneDelete("Zone_7")
	da, _ := domain.NewZoneDeleteAction("d", del)
	if got, err = IntentAction("k", da); err != nil || compactJSON(t, got) != `{"key":"k","zone":{"zone":{"id":"Zone_7"},"delete":true}}` {
		t.Fatal(got, err)
	}
	zone, _ := domain.NewZoneCreate(domain.GrowingZone, "Plant_Rice", []domain.Cell{{X: 0, Z: 0}})
	za, _ := domain.NewZoneCreateAction("z", zone)
	if got, err = IntentAction("k", za); err != nil || got.GetZone().GetGrowing().GetPlantDef() != "Plant_Rice" {
		t.Fatal(got, err)
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

func legacyStockpileSettings(zone legacyZone) *op.StockpileSettings {
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
	switch zone.preset {
	case "food":
		preset = op.FilterPreset_FILTER_PRESET_FOOD
	case "nothing", "corpse_larder":
		preset = op.FilterPreset_FILTER_PRESET_NOTHING
	case "general":
		preset = op.FilterPreset_FILTER_PRESET_NONPERISHABLES
	}
	settings := &op.StockpileSettings{Priority: priority.Enum(), Preset: preset.Enum()}
	if zone.preset == "corpse_larder" {
		settings.Filter = &op.FilterPatch{
			Allow:    []*op.FilterSelector{{Definition: &op.FilterSelector_CategoryDef{CategoryDef: "CorpsesAnimal"}}, {Definition: &op.FilterSelector_CategoryDef{CategoryDef: "CorpsesInsect"}}, {Definition: &op.FilterSelector_SpecialFilterDef{SpecialFilterDef: "AllowFresh"}}},
			Disallow: []*op.FilterSelector{{Definition: &op.FilterSelector_SpecialFilterDef{SpecialFilterDef: "AllowRotten"}}},
		}
	}
	if zone.preset == "general" {
		settings.Filter = &op.FilterPatch{Disallow: []*op.FilterSelector{{Definition: &op.FilterSelector_CategoryDef{CategoryDef: "Chunks"}}}}
	}
	if zone.preset == "nothing" {
		var allow []*op.FilterSelector
		for _, name := range zone.allow {
			allow = append(allow, &op.FilterSelector{Definition: &op.FilterSelector_ThingDef{ThingDef: name}})
		}
		settings.Filter = &op.FilterPatch{Allow: allow}
	}
	return settings
}
