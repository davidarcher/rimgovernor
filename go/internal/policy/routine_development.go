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
	case EnsureDefensiveLayout:
		// Config-only opt-in (see RoutinePolicy.DefensiveLayout): while
		// opted in the layout counts as a full deficit so development
		// arbitration can select it; the planner itself reports no deficit
		// once the stored layout is complete. Without this the goal ranks
		// deficit_unknown and every tier admission conflicts.
		if !p.DefensiveLayout {
			return domain.Unknown[float64]()
		}
		return domain.Known(1.0)
	case MaintainMedicalReserves:
		// The reserve review's own stock/target: the harvest or bench method
		// needs a ranked deficit to be admitted at development priority.
		review, err := ReviewMedicalReserve(f.MedicalReserve, f.UpkeepIssued[MaintainMedicalReserves], p.MedicalReserve)
		if err != nil {
			return domain.Unknown[float64]()
		}
		var targetKnown bool
		stock, known = review.Stock.Value()
		target, targetKnown = review.Target.Value()
		known = known && targetKnown
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

// ResearchGoalTarget is the project EnsureResearch pursues: the configured
// target when one is set, else the first derived need the native research
// census lists and has not finished. Unknown facts keep the first derived
// need so the deficit stays unknown rather than recovered; no need at all
// is no target.
func ResearchGoalTarget(configured string, derived []string, facts domain.Fact[ResearchFacts]) string {
	if configured != "" || len(derived) == 0 {
		return configured
	}
	f, known := facts.Value()
	if !known {
		return derived[0]
	}
	listed := map[string]bool{}
	for _, name := range f.Projects {
		listed[string(name)] = true
	}
	finished := map[string]bool{}
	for _, name := range f.Finished {
		finished[string(name)] = true
	}
	for _, name := range derived {
		if listed[name] && !finished[name] {
			return name
		}
	}
	return ""
}

// ResearchTargetNeed measures EnsureResearch: no target is certain recovery;
// a finished target is recovered; an idle research tab with the target
// unfinished is a full deficit. A current native project also recovers a
// configured target (player-chosen research is respected, never replaced),
// but not a derived one: another goal's ladder waits on that project, so
// the deficit stands, and the research planner asks the clock for ticks
// while any project is current, until it is finished. Missing facts stay
// unknown.
func ResearchTargetNeed(target string, derived bool, facts domain.Fact[ResearchFacts]) (recovered domain.Fact[bool], deficit domain.Fact[float64]) {
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
	if f.Current != "" && !derived {
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

// outdoorHazards are native game conditions under which outdoor pawn work is
// observed unsafe rather than merely uncomfortable.
var outdoorHazards = map[string]bool{"ToxicFallout": true}

// RoutineDevelopmentRisk measures a routine goal's observed work exposure for
// ranking: goals whose labor profile is outdoor work (construction, mining,
// plant cutting) carry risk 1 under an observed outdoor hazard condition and
// risk 0.5 while a cold or hot latch is active; everything else is 0. It is
// ordering evidence for admission, not a safety guard: native danger checks
// and Hands dispatch guards still apply.
func RoutineDevelopmentRisk(id GoalID, f RoutineFacts, l RoutineLatches) domain.Fact[float64] {
	outdoor := false
	for _, w := range GoalLabor(id) {
		outdoor = outdoor || w == WorkConstruction || w == WorkMining || w == WorkPlantCutting
	}
	if !outdoor {
		return domain.Known(0.0)
	}
	if conditions, known := f.DisasterConditions.Value(); known {
		for _, c := range conditions {
			if outdoorHazards[c.Definition] {
				return domain.Known(1.0)
			}
		}
	}
	if l.Cold || l.Hot {
		return domain.Known(0.5)
	}
	return domain.Known(0.0)
}
