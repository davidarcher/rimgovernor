package bridge

import (
	"math"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Weather and game condition facts the generated def rows state (#1723).

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
