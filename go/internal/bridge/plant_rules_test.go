package bridge

import "testing"

// TestPlantRulesReadTheDefRows is the parity check against the native censuses
// these rules replaced (PlantProperties.IsTree, PlantProperties.diesToLight,
// CompProperties_Glower.glowRadius): over the recorded vanilla defs the oak is
// a tree and rice is not, only cave fungus dies to light, and the lamps glow.
func TestPlantRulesReadTheDefRows(t *testing.T) {
	catalog := sharedRecordedCatalog(t)
	if !catalog.PlantIsTree("Plant_TreeOak") || catalog.PlantIsTree("Plant_Rice") || catalog.PlantIsTree("SunLamp") || catalog.PlantIsTree("") {
		t.Error("PlantIsTree disagrees with PlantProperties.IsTree")
	}
	var dying []string
	for name := range catalog.ThingDefs {
		if catalog.PlantDiesToLight(name) {
			dying = append(dying, name)
		}
	}
	if len(dying) == 0 || catalog.PlantDiesToLight("Plant_Rice") || catalog.PlantDiesToLight("Plant_TreeOak") {
		t.Errorf("diesToLight plants: %v", dying)
	}
	for _, lamp := range []string{"SunLamp", "StandingLamp", "TorchLamp"} {
		radius, glows, err := catalog.GlowRadius(lamp)
		if err != nil || !glows || !(radius > 0) {
			t.Errorf("%s glow radius %v %v %v", lamp, radius, glows, err)
		}
	}
	if _, glows, err := catalog.GlowRadius("Plant_Rice"); glows || err != nil {
		t.Errorf("rice glows: %v %v", glows, err)
	}
	if _, glows, _ := catalog.GlowRadius("NoSuchDef"); glows {
		t.Error("an unknown def glows")
	}
}
