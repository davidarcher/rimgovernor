package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The StatDefs of a race's comfortable temperature range (#1868).
const (
	StatComfyTemperatureMin = "ComfyTemperatureMin"
	StatComfyTemperatureMax = "ComfyTemperatureMax"
)

// animalComfort is the race's comfortable outdoor temperature range from the
// stat table (the game's GetStatValueAbstract of ComfyTemperatureMin/Max):
// unknown when the game shows neither stat for it, never a default (the
// exposure view then fails for that race, #1869). A single shown stat or an
// inverted range is a contract breach.
func (catalog *DefinitionCatalog) animalComfort(race string) (domain.Fact[policy.AnimalComfort], error) {
	lo, loShown, err := catalog.ShownStatValue(race, "", StatComfyTemperatureMin)
	if err != nil {
		return domain.Unknown[policy.AnimalComfort](), err
	}
	hi, hiShown, err := catalog.ShownStatValue(race, "", StatComfyTemperatureMax)
	if err != nil {
		return domain.Unknown[policy.AnimalComfort](), err
	}
	switch {
	case !loShown && !hiShown:
		return domain.Unknown[policy.AnimalComfort](), nil
	case !loShown || !hiShown:
		return domain.Unknown[policy.AnimalComfort](), contract("race %s shows only one of its comfort range stats", race)
	case lo > hi:
		return domain.Unknown[policy.AnimalComfort](), contract("race %s comfort range is inverted (%v > %v)", race, lo, hi)
	}
	return domain.Known(policy.AnimalComfort{Min: float64(lo), Max: float64(hi)}), nil
}
