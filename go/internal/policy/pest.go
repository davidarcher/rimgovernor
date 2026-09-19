package policy

import (
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// pestDefinitions are the wild animals ClearPests hunts for what they
// destroy rather than for meat (#247): an alphabeaver pack arrives as a
// NegativeEvent letter, is factionless, never hostile and not a predator,
// so no emergency census answers it while it defoliates the map. Native's
// NativeHuntAcquisition.PestDefinitions lists the same names: it decides
// which animals the hunt census offers as pest rows, this decides which
// rows of the wild-animal census count as the goal's deficit.
var pestDefinitions = map[Resource]bool{"Alphabeaver": true}

// PestDefinition reports whether a native definition name is a recognised
// pest.
func PestDefinition(definition Resource) bool { return pestDefinitions[definition] }

// PestCensus counts the recognised pests in the wild-animal census (every
// living factionless animal on the map, so a pest anywhere counts, not
// only one near the colony). Unknown while the census is unknown.
func PestCensus(wild domain.Fact[[]UpkeepAnimal]) domain.Fact[int] {
	rows, known := wild.Value()
	if !known {
		return domain.Unknown[int]()
	}
	count := 0
	for _, row := range rows {
		if PestDefinition(row.Definition) {
			count++
		}
	}
	return domain.Known(count)
}

// PestAcquisitionSources are the hunt rows the acquisition census offers
// for recognised pests: a hunt of one unit of nothing edible (food false,
// no nutrition), which the food and wood selections pass over.
func PestAcquisitionSources(sources domain.Fact[[]AcquisitionSource]) []AcquisitionSource {
	rows, known := sources.Value()
	if !known {
		return nil
	}
	var pests []AcquisitionSource
	for _, row := range rows {
		if row.Hunt && PestDefinition(Resource(row.Definition)) {
			pests = append(pests, row)
		}
	}
	return pests
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
		return 1, row.Hunt && PestDefinition(Resource(row.Definition))
	}
	return selectAcquisition(sources, domain.Known(float64(count)), domain.Known(0.0), accept, held, huntSlots)
}
