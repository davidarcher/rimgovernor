package policy

import (
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A pest is a wild animal ClearPests hunts for what it destroys rather than
// for meat (#247): an alphabeaver pack arrives as a NegativeEvent letter, is
// factionless, never hostile and not a predator, so no emergency census
// answers it while it defoliates the map. The race rows say which races are
// pests (AnimalRace.Pest: an animal that eats trees); native's
// NativeHuntAcquisition.PestRace applies the same rule to decide which
// animals the hunt census offers as pest rows, and this decides which rows of
// the wild-animal census count as the goal's deficit.

// PestCensus counts the pests in the wild-animal census (every
// living factionless animal on the map, so a pest anywhere counts, not
// only one near the colony). Unknown while the census is unknown.
func PestCensus(wild domain.Fact[[]UpkeepAnimal]) domain.Fact[int] {
	rows, known := wild.Value()
	if !known {
		return domain.Unknown[int]()
	}
	count := 0
	for _, row := range rows {
		if row.Pest {
			count++
		}
	}
	return domain.Known(count)
}

// SelectPestAcquisition picks the pest hunts to admit: every undesignated,
// unheld pest row in native order (nearest the colony first), one hunt per
// pest, up to the hunting budget (native admits at most two outstanding
// hunts per map) and never more than the census's pest count. An unknown
// budget admits nothing.
func SelectPestAcquisition(sources domain.Fact[[]AcquisitionSource], pests domain.Fact[int], held map[string]bool, huntSlots domain.Fact[int]) ([]AcquisitionSource, error) {
	count, known := pests.Value()
	if !known || count < 0 {
		return nil, errors.New("pest census unavailable")
	}
	accept := func(row AcquisitionSource) (float64, bool) {
		return 1, row.Hunt && row.Pest
	}
	return selectAcquisition(sources, domain.Known(float64(count)), domain.Known(0.0), accept, held, huntSlots)
}
