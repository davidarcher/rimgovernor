package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The StatDefs of a race's comfortable temperature range (#1868).
const (
	StatComfyTemperatureMin = "ComfyTemperatureMin"
	StatComfyTemperatureMax = "ComfyTemperatureMax"
)

// AnimalComfort is race's comfortable outdoor temperature range from the stat
// table (the game's GetStatValueAbstract of ComfyTemperatureMin/Max). A race
// with no row, a stat the game does not show for it, or an inverted range is
// an error: no default range is assumed.
func (catalog *DefinitionCatalog) AnimalComfort(race string) (policy.AnimalComfort, error) {
	lo, err := catalog.StatValue(race, "", StatComfyTemperatureMin)
	if err != nil {
		return policy.AnimalComfort{}, err
	}
	hi, err := catalog.StatValue(race, "", StatComfyTemperatureMax)
	if err != nil {
		return policy.AnimalComfort{}, err
	}
	if lo > hi {
		return policy.AnimalComfort{}, contract("race %s comfort range is inverted (%v > %v)", race, lo, hi)
	}
	return policy.AnimalComfort{Min: float64(lo), Max: float64(hi)}, nil
}
