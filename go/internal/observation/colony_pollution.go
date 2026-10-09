package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// colonyPollution is ManagePollution's read from the Biotech colony
// section: each wastepack with the game's verdicts and the count of polluted
// cells outside the pollution-clear area. Unknown without the section, so the
// goal has no assessment on a colony without Biotech.
func colonyPollution(section domain.Fact[BiotechColony]) domain.Fact[policy.PollutionFacts] {
	b, known := section.Value()
	if !known {
		return domain.Unknown[policy.PollutionFacts]()
	}
	out := policy.PollutionFacts{UncoveredCells: b.PollutedUncoveredCells}
	for _, w := range b.Wastepacks {
		out.Wastepacks = append(out.Wastepacks, policy.Wastepack{ID: w.ID, Definition: w.Definition, Cell: w.Position,
			Frozen: w.Frozen, InAtomizer: w.InAtomizer, Forbidden: w.Forbidden})
	}
	return domain.Known(out)
}
