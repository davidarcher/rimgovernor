package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// HerdSaleAnimals are the colony animals the herd plan lets a trader take
// (#1632): the animals herdSurplusCandidates lists over each race's ceiling
// (every animal of a retired race, young ones included), less any that is
// bonded or, outside a retired race, trained for work the colony still
// needs. Founders and companions have no ceiling, so none is listed. An
// unread census or any unknown designation, or an unknown bond, sells
// nothing. Animals already designated for slaughter or release are not
// listed: the cull owns them.
func HerdSaleAnimals(animals domain.Fact[[]UpkeepAnimal], herd HerdPolicy) map[PawnID]bool {
	rows, known := animals.Value()
	if !known {
		return nil
	}
	removals, unknown := herdSurplusCandidates(rows, herd.PopulationMax, true, herd.Retired)
	if unknown {
		return nil
	}
	var out map[PawnID]bool
	for _, r := range removals {
		a := r.animal
		if bonded, ok := a.Bonded.Value(); !ok || bonded || herdTrained(a) && !herd.Retired[a.Definition] {
			continue
		}
		if out == nil {
			out = map[PawnID]bool{}
		}
		out[a.ID] = true
	}
	return out
}

// SaleAnimals is HerdSaleAnimals over the herd plan of f.
func (f RoutineFacts) SaleAnimals() map[PawnID]bool {
	return HerdSaleAnimals(f.AnimalUpkeep.Animals, f.HerdPolicy())
}

// AnimalSaleNeed adds the animal-sale reason to the need: sale animals and a
// known silver shortfall (SilverShort). Anything unknown adds nothing.
func AnimalSaleNeed(need domain.Fact[TradeNeed], sale map[PawnID]bool, silver, colonists domain.Fact[int64]) domain.Fact[TradeNeed] {
	n, known := need.Value()
	if !known || len(sale) == 0 || !positive(SilverShort(need, silver, colonists)) {
		return need
	}
	n.SurplusAnimals = int64(len(sale))
	return domain.Known(n)
}
