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

// ResearchFacts is the review-time native research state EnsureResearch's
// need is measured against. Projects lists every native project the census
// exposes so a configured target absent from the game stays unknown rather
// than an unbounded deficit.
type ResearchFacts struct {
	Current  ResearchProjectID
	Finished []ResearchProjectID
	Projects []ResearchProjectID
}

// ResearchTargetNeed measures EnsureResearch: no configured target is certain
// recovery; a finished target or any current native project (player-chosen
// research is respected, never replaced) is recovered; an idle research tab
// with the target unfinished is a full deficit. Missing facts stay unknown.
func ResearchTargetNeed(target string, facts domain.Fact[ResearchFacts]) (recovered domain.Fact[bool], deficit domain.Fact[float64]) {
	if target == "" {
		return domain.Known(true), domain.Known(0.0)
	}
	f, known := facts.Value()
	if !known {
		return domain.Unknown[bool](), domain.Unknown[float64]()
	}
	listed := false
	for _, name := range f.Projects {
		listed = listed || string(name) == target
	}
	if !listed {
		return domain.Unknown[bool](), domain.Unknown[float64]()
	}
	for _, name := range f.Finished {
		if string(name) == target {
			return domain.Known(true), domain.Known(0.0)
		}
	}
	if f.Current != "" {
		return domain.Known(true), domain.Known(0.0)
	}
	return domain.Known(false), domain.Known(1.0)
}

// ResourceTargetNeed measures MaintainResource against the generic resource census:
// the deficit is the worst-covered configured target's shortfall fraction,
// the same proportion SelectResourceTarget dispatches on. No configured
// target is certain recovery; unknown stock stays unknown.
func ResourceTargetNeed(targets map[Resource]int64, stock domain.Fact[[]Amount]) (recovered domain.Fact[bool], deficit domain.Fact[float64]) {
	if len(targets) == 0 {
		return domain.Known(true), domain.Known(0.0)
	}
	if _, known := stock.Value(); !known {
		return domain.Unknown[bool](), domain.Unknown[float64]()
	}
	resource, target, ok, err := SelectResourceTarget(targets, stock)
	if err != nil {
		return domain.Unknown[bool](), domain.Unknown[float64]()
	}
	if !ok {
		return domain.Known(true), domain.Known(0.0)
	}
	rows, _ := stock.Value()
	var have int64
	for _, q := range rows {
		if q.Resource == resource {
			have = q.Count
		}
	}
	return domain.Known(false), domain.Known(min(1.0, max(0.0, float64(target-have)/float64(target))))
}

// DevelopmentExempt reports routine needs whose methods are configuration
// pushes rather than pawn work: they are assessed and admitted without a
// development ranking row and consume no optional capacity slot.
func DevelopmentExempt(need GoalID) bool {
	return need == ProductionPolicy
}
