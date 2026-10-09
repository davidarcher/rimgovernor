package bridge

import (
	"math"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Weather and game condition facts the generated def rows state.

// electricityDisabledClass is the GameCondition whose ElectricityDisabled is
// true: every powered building is off while it lasts (a solar flare). A mod's
// subclass is one too.
const electricityDisabledClass = "RimWorld.GameCondition_DisableElectricity"

// WeatherAccuracy is the WeatherDef's accuracyMultiplier: the factor on every
// ranged hit chance while the weather lasts. A weather the catalog has no row
// for and a non-positive multiplier are errors.
func (catalog *DefinitionCatalog) WeatherAccuracy(name string) (float64, error) {
	if catalog == nil {
		return 0, contract("no definition catalog")
	}
	row := DefRow[*d.WeatherDef](catalog, name)
	if row == nil {
		return 0, contract("catalog has no weather def row for %s", name)
	}
	accuracy := float64(row.GetAccuracyMultiplier())
	if math.IsNaN(accuracy) || math.IsInf(accuracy, 0) || accuracy <= 0 {
		return 0, contract("weather %s has accuracy multiplier %v", name, accuracy)
	}
	return accuracy, nil
}

// DisablesElectricity reports whether the game condition def's class is
// GameCondition_DisableElectricity or derives from it.
func (catalog *DefinitionCatalog) DisablesElectricity(name string) (bool, error) {
	if catalog == nil {
		return false, contract("no definition catalog")
	}
	row := DefRow[*d.GameConditionDef](catalog, name)
	if row == nil {
		return false, contract("catalog has no game condition def row for %s", name)
	}
	if row.GetConditionClass() == "" {
		return false, contract("game condition %s names no class", name)
	}
	return catalog.ClassIsA(row.GetConditionClass(), electricityDisabledClass)
}

// noSunlightClass is the GameCondition family that blacks out the sky: a
// condition of it or a subclass (a mod's included) keeps outdoor light at
// night level while it lasts.
const noSunlightClass = "RimWorld.GameCondition_NoSunlight"

// OutdoorsPermanentlyDark reports whether the biome's map conditions (the
// ones every map of the biome carries for its whole life) include one whose
// class is GameCondition_NoSunlight or derives from it: the sky never
// lights the ground, so plants that need light do not grow outdoors and an
// unroofed work cell stays dark by day. A biome or condition the catalog has
// no row for is an error.
func (catalog *DefinitionCatalog) OutdoorsPermanentlyDark(biome string) (bool, error) {
	if catalog == nil {
		return false, contract("no definition catalog")
	}
	row := DefRow[*d.BiomeDef](catalog, biome)
	if row == nil {
		return false, contract("catalog has no biome def row for %s", biome)
	}
	for _, name := range row.GetBiomeMapConditions() {
		condition := DefRow[*d.GameConditionDef](catalog, name)
		if condition == nil {
			return false, contract("catalog has no game condition def row for %s (biome %s)", name, biome)
		}
		if condition.GetConditionClass() == "" {
			return false, contract("game condition %s names no class", name)
		}
		dark, err := catalog.ClassIsA(condition.GetConditionClass(), noSunlightClass)
		if err != nil {
			return false, err
		}
		if dark {
			return true, nil
		}
	}
	return false, nil
}
