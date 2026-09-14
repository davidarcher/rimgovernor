package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// RoutineWorkers counts observed available workers, preserving incomplete facts.
func RoutineWorkers(pawns []WorkPawn) domain.Fact[int] {
	count := 0
	for _, p := range pawns {
		available, known := p.Available.Value()
		applies, appliesKnown := p.Applies.Value()
		if known && !available || appliesKnown && !applies {
			continue
		}
		if !known || !appliesKnown {
			return domain.Unknown[int]()
		}
		count++
	}
	return domain.Known(count)
}

func RoutineDevelopmentDeficit(id GoalID, f RoutineFacts, p RoutinePolicy) domain.Fact[float64] {
	var stock, target int64
	var known bool
	switch id {
	case EnsureExpansion:
		var countKnown bool
		target, countKnown = f.Colonists.Value()
		if !countKnown || target <= 0 || target >= 1<<63-1 {
			return domain.Unknown[float64]()
		}
		target++
		stock, known = f.IndoorCapacity.Value()
	case MaintainWood:
		stock, known = f.Wood.Value()
		target = p.WoodTarget
	case EnsureBasicDefense:
		var countKnown bool
		target, countKnown = f.Colonists.Value()
		target = min(target, 2)
		stock, known = f.Armed.Value()
		known = known && countKnown
	case MaintainWaste:
		// Census-driven, not stock/target: any exposed, eligible item still
		// pending is a full deficit: there's no partial-credit fraction for
		// "half the trash is gone" the way there is for wood or bed count.
		items, itemsKnown := f.Waste.Value()
		if !itemsKnown {
			return domain.Unknown[float64]()
		}
		if len(pendingWaste(items)) == 0 {
			return domain.Known(0.0)
		}
		return domain.Known(1.0)
	default:
		return domain.Unknown[float64]()
	}
	if !known || target <= 0 || stock < 0 {
		return domain.Unknown[float64]()
	}
	return domain.Known(min(1.0, max(0.0, (float64(target)-float64(stock))/float64(target))))
}
