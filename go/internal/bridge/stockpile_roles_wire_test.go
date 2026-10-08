package bridge

import (
	"encoding/json"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"google.golang.org/protobuf/encoding/protojson"
)

// The armory and wardrobe filters reach native as category selectors with the
// armor defs split between them and the biocoded, worn-out and burnable gear excluded.
func TestArmoryAndWardrobeFiltersWire(t *testing.T) {
	armor := []string{"Apparel_PlateArmor", "Apparel_FlakVest"}
	armory, err := domain.ArmoryFilter(armor)
	if err != nil {
		t.Fatal(err)
	}
	wardrobe, err := domain.WardrobeFilter(armor)
	if err != nil {
		t.Fatal(err)
	}
	want := map[string]string{
		"armory":   `{"priority":"STORAGE_PRIORITY_NORMAL","preset":"FILTER_PRESET_NOTHING","filter":{"allow":[{"thingDef":"Apparel_FlakVest"},{"thingDef":"Apparel_PlateArmor"},{"categoryDef":"Weapons"}],"disallow":[{"specialFilterDef":"AllowBiocodedApparel"},{"specialFilterDef":"AllowBiocodedWeapons"},{"specialFilterDef":"AllowDeadmansApparel"},{"specialFilterDef":"RimGovernorBurnable"}],"hitPointsMin":0.5,"hitPointsMax":1,"qualityMin":"Normal","qualityMax":"Legendary"}}`,
		"wardrobe": `{"priority":"STORAGE_PRIORITY_NORMAL","preset":"FILTER_PRESET_NOTHING","filter":{"allow":[{"categoryDef":"Apparel"}],"disallow":[{"thingDef":"Apparel_FlakVest"},{"thingDef":"Apparel_PlateArmor"},{"specialFilterDef":"AllowBiocodedApparel"},{"specialFilterDef":"AllowDeadmansApparel"},{"specialFilterDef":"RimGovernorBurnable"}],"hitPointsMin":0.5,"hitPointsMax":1,"qualityMin":"Normal","qualityMax":"Legendary"}}`,
	}
	for name, f := range map[string]domain.StockpileFilter{"armory": armory, "wardrobe": wardrobe} {
		z, err := domain.NewFilteredStockpileZone(f, domain.NormalPriority, stockpileTestRectangle([]domain.Cell{{X: 1, Z: 1}}))
		if err != nil {
			t.Fatal(name, err)
		}
		got, _ := protojson.Marshal(stockpileSettings(z))
		var a, b any
		if json.Unmarshal(got, &a) != nil || json.Unmarshal([]byte(want[name]), &b) != nil || !reflect.DeepEqual(a, b) {
			t.Fatalf("%s wire:\n got %s", name, got)
		}
	}
}
