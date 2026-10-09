package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// MaintainGeneBank adds capacity for loose genepacks, which deteriorate outside
// powered banks. Capacity comes from native bank facts; EnsureBasicPower owns
// power supply. Banks are identified by CompProperties_GenepackContainer.
const MaintainGeneBank ConcernID = "MaintainGeneBank"

// GeneBankSlot is one standing bank: its capacity as the game reports it and
// how many packs it already holds.
type GeneBankSlot struct {
	Capacity domain.Fact[int32]
	Held     int32
}

// GeneBankFacts are the gene-building rows the need reads: the standing
// banks and, per pack, whether it lies loose on the map (a pack held by a
// bank is not loose; unknown when the pack reported neither).
type GeneBankFacts struct {
	Banks []GeneBankSlot
	Packs []domain.Fact[bool]
}

// GeneBankNeed is RoundsFacts.GeneBankOwed's value: whether more packs lie
// loose than the banks' free slots hold. Unknown while the section is
// unread, a bank's capacity is unknown or a pack's whereabouts are.
func GeneBankNeed(section domain.Fact[GeneBankFacts]) domain.Fact[bool] {
	f, known := section.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	var free, loose int32
	for _, b := range f.Banks {
		capacity, ok := b.Capacity.Value()
		if !ok {
			return domain.Unknown[bool]()
		}
		free += max(capacity-b.Held, 0)
	}
	for _, p := range f.Packs {
		isLoose, ok := p.Value()
		if !ok {
			return domain.Unknown[bool]()
		}
		if isLoose {
			loose++
		}
	}
	return domain.Known(loose > free)
}
