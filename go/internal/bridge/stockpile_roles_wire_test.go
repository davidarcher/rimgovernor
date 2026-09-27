package bridge

import (
	"encoding/json"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"google.golang.org/protobuf/encoding/protojson"
)

// The #724 role filters reach native as category and special-filter
// selectors with their hit-point and quality floors.
func TestGearAndDumpRoleFiltersWire(t *testing.T) {
	want := map[string]string{
		domain.ApparelRole:    `{"priority":"STORAGE_PRIORITY_PREFERRED","preset":"FILTER_PRESET_NOTHING","filter":{"allow":[{"categoryDef":"Apparel"}],"disallow":[{"specialFilterDef":"AllowBiocodedApparel"},{"specialFilterDef":"AllowDeadmansApparel"}],"hitPointsMin":0.5,"hitPointsMax":1,"qualityMin":"Normal","qualityMax":"Legendary"}}`,
		domain.WeaponsRole:    `{"priority":"STORAGE_PRIORITY_PREFERRED","preset":"FILTER_PRESET_NOTHING","filter":{"allow":[{"categoryDef":"Weapons"}],"disallow":[{"specialFilterDef":"AllowBiocodedWeapons"}],"hitPointsMin":0.5,"hitPointsMax":1,"qualityMin":"Normal","qualityMax":"Legendary"}}`,
		domain.WornDumpRole:   `{"priority":"STORAGE_PRIORITY_LOW","preset":"FILTER_PRESET_NOTHING","filter":{"allow":[{"categoryDef":"Apparel"},{"categoryDef":"Weapons"}]}}`,
		domain.RottenDumpRole: `{"priority":"STORAGE_PRIORITY_LOW","preset":"FILTER_PRESET_NOTHING","filter":{"allow":[{"categoryDef":"CorpsesAnimal"},{"categoryDef":"CorpsesInsect"},{"categoryDef":"Foods"}],"disallow":[{"specialFilterDef":"AllowFresh"}]}}`,
		domain.CorpseDumpRole: `{"priority":"STORAGE_PRIORITY_LOW","preset":"FILTER_PRESET_NOTHING","filter":{"allow":[{"categoryDef":"CorpsesHumanlike"}]}}`,
	}
	for _, spec := range domain.GearAndDumpRoles() {
		z, err := domain.NewFilteredStockpileZone(spec.Filter, spec.Priority, []domain.Cell{{X: 1, Z: 1}})
		if err != nil {
			t.Fatal(spec.Role, err)
		}
		got, _ := protojson.Marshal(stockpileSettings(z))
		var a, b any
		if json.Unmarshal(got, &a) != nil || json.Unmarshal([]byte(want[spec.Role]), &b) != nil || !reflect.DeepEqual(a, b) {
			t.Fatalf("%s wire:\n got %s", spec.Role, got)
		}
	}
}
