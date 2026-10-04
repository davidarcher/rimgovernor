package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// MaintainGeneBank keeps every genepack in a gene bank (epic #1693, #1933).
// A Genepack deteriorates unless it sits in a powered GeneBank (4 packs
// each, the design note on #1693), so the goal is in deficit while more packs
// lie loose on the map than the standing banks have room for, and settles
// once every pack has a slot. It is a Standard whose target is no
// outstanding work, like EnsureMechCharger. The method is one bank
// construction; powering it is not this goal's: native lists every
// CompPowerTrader building and EnsureBasicPower wires an unpowered consumer
// by its draw alone, never by definition.
//
// A bank is found by its CompProperties_GenepackContainer comp, never by a
// definition name. Harvesting, assembly and implanting are other goals.
const MaintainGeneBank GoalID = "MaintainGeneBank"

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

// GeneBankNeed is RoutineFacts.GeneBankOwed's value: whether more packs lie
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
