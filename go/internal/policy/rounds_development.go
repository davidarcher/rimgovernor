package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// RoundsWorkers counts observed available workers, preserving incomplete facts.
func RoundsWorkers(pawns []WorkPawn) domain.Fact[int] {
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

func RoundsDevelopmentDeficit(id ConcernID, f RoundsFacts, p RoundsPolicy) domain.Fact[float64] {
	var stock, target int64
	var known bool
	switch id {
	case MaintainHousing:
		// The expansion phase: one indoor place beyond the population.
		var countKnown bool
		target, countKnown = f.Colonists.Value()
		if !countKnown || target <= 0 || target >= 1<<63-1 {
			return domain.Unknown[float64]()
		}
		target++
		stock, known = f.IndoorCapacity.Value()
	case EnsureBasicDefense:
		var countKnown bool
		target, countKnown = f.Colonists.Value()
		target = min(target, 2)
		stock, known = f.Armed.Value()
		known = known && countKnown
		if unarmed, uk := f.Unarmed.Value(); known && uk && unarmed > 0 {
			target = max(target, stock+unarmed)
		}
	case EnsureDefensiveLayout:
		// Config-only opt-in (see RoundsPolicy.DefensiveLayout): while
		// opted in the layout counts as a full deficit so development
		// arbitration can select it, until the journal shows every tier
		// standing; then it ranks by age alone so an active repair or power
		// deficit takes the slot first and the planner's periodic
		// re-verification still runs when a slot is free. Without this
		// the goal ranks deficit_unknown and every tier admission conflicts.
		if !p.DefensiveLayout {
			return domain.Unknown[float64]()
		}
		if positive(f.DefensiveLayoutStanding) {
			return domain.Known(0.0)
		}
		return domain.Known(1.0)
	case MaintainFoodStorage:
		// The food reserve review (#428): its refill is a preservation bill
		// that needs a ranked deficit for a development slot. Reserve access
		// (a pending hold or release) is a full deficit so the slot is not
		// withheld while stock moves; the upkeep census itself ranks no slot.
		reserve, reserveKnown := f.FoodReserve.Value()
		if !reserveKnown {
			return domain.Unknown[float64]()
		}
		if len(reserve.Hold) > 0 || len(reserve.Release) > 0 {
			return domain.Known(1.0)
		}
		if reserve.TargetNutrition <= 0 {
			return domain.Known(0.0)
		}
		return domain.Known(min(1.0, max(0.0, reserve.DeficitNutrition/reserve.TargetNutrition)))
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
	case MaintainBurial:
		// Census-driven: a tomb, grave or morgue step due is a full deficit.
		owed, owedKnown := f.BurialOwed.Value()
		if !owedKnown {
			return domain.Unknown[float64]()
		}
		if !owed {
			return domain.Known(0.0)
		}
		return domain.Known(1.0)
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
	case MaintainIncineration:
		owed, known := f.IncinerationOwed.Value()
		if !known {
			return domain.Unknown[float64]()
		}
		if owed {
			return domain.Known(1.0)
		}
		return domain.Known(0.0)
	case RemoveBlight:
		// Census-driven like waste: any standing blighted plant is a full
		// deficit until the census is empty.
		deficit, deficitKnown := BlightDeficit(f.Blight).Value()
		if !deficitKnown {
			return domain.Unknown[float64]()
		}
		if !deficit {
			return domain.Known(0.0)
		}
		return domain.Known(1.0)
	case EnsureMechCharger:
		// A charger owed is a full deficit, none owed none.
		owed, known := f.MechChargerOwed.Value()
		if !known {
			return domain.Unknown[float64]()
		}
		if !owed {
			return domain.Known(0.0)
		}
		return domain.Known(1.0)
	case MaintainGeneBank:
		// A bank owed is a full deficit, none owed none.
		owed, known := f.GeneBankOwed.Value()
		if !known {
			return domain.Unknown[float64]()
		}
		if !owed {
			return domain.Known(0.0)
		}
		return domain.Known(1.0)
	case ManagePollution:
		// Census-driven like blight: any exposed or forbidden wastepack, or
		// polluted cell outside the clear area, is a full deficit.
		deficit, deficitKnown := PollutionDeficit(f.Pollution).Value()
		if !deficitKnown {
			return domain.Unknown[float64]()
		}
		if !deficit {
			return domain.Known(0.0)
		}
		return domain.Known(1.0)
	case MaintainStockpiles:
		review, known := f.Stockpiles.Value()
		if !known || !review.Known {
			return domain.Unknown[float64]()
		}
		if !review.Active {
			return domain.Known(0.0)
		}
		return domain.Known(stockpileDeficit)
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
	// CurrentBenchMissing: the current project's only native lock is the
	// research bench nobody has built, so it does not progress and the
	// goal stays in deficit for the bench (#254).
	CurrentBenchMissing bool
	// KnowledgePick is the knowledge project an empty Anomaly knowledge
	// slot should fund now (KnowledgePick, #1745), "" when none is owed.
	KnowledgePick ResearchProjectID
}

// ResearchGoalTarget is the project EnsureResearch pursues: the configured
// target when one is set, else the first derived need the native research
// census lists and has not finished. Unknown facts keep the first derived
// need so the deficit stays unknown rather than recovered; no need at all
// is no target.
func ResearchConcernTarget(configured string, derived []string, facts domain.Fact[ResearchFacts]) string {
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

// DefaultResearchLadder is the roadmap EnsureResearch walks when neither an
// operator target nor a workshop need names a project: the early research
// direction a new colony needs on its own (stone blocks, then power with its
// storage, geothermal and solar rungs, the medieval crafts, then Machining
// and simple firearms). Microelectronics is deliberately absent. Rungs the
// native census does not list (another mod set) are skipped.
func DefaultResearchLadder() []string {
	return []string{"Stonecutting", "Electricity", "Batteries", "GeothermalPower", "SolarPanels", "Smithing", "CarpetMaking", "ComplexClothing", "Machining", "Gunsmithing"}
}

// ResearchGoal resolves the project EnsureResearch pursues and whether it is
// a workshop need (derived): the first
// unfinished project the workshop ladder recorded, then the first unfinished
// rung of RoundsPolicy.ResearchLadder. A ladder rung is only walked under a
// known research census: the ladder is a default, not a declared need, and
// without the census there is nothing to measure it against.
func ResearchConcern(p RoundsPolicy, needs []string, facts domain.Fact[ResearchFacts]) (target string, derived bool) {
	if target = ResearchConcernTarget("", needs, facts); target != "" {
		return target, true
	}
	if _, known := facts.Value(); !known {
		return "", false
	}
	return ResearchConcernTarget("", p.ResearchLadder, facts), false
}

// ResearchGate names the first project of required that the native census
// has not finished: the research a goal's only method waits on. Unknown
// facts keep the first requirement; none unfinished is no gate.
func ResearchGate(required []string, facts domain.Fact[ResearchFacts]) string {
	if len(required) == 0 {
		return ""
	}
	f, known := facts.Value()
	if !known {
		return required[0]
	}
	finished := map[string]bool{}
	for _, name := range f.Finished {
		finished[string(name)] = true
	}
	for _, name := range required {
		if !finished[name] {
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
	if f.Current != "" && !derived && !f.CurrentBenchMissing {
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

// outdoorHazards are native game conditions under which outdoor pawn work is
// observed unsafe rather than merely uncomfortable.
var outdoorHazards = map[string]bool{"ToxicFallout": true}

// RoundsDevelopmentRisk measures a routine goal's observed work exposure for
// ranking: goals whose labor profile is outdoor work (construction, mining,
// plant cutting) carry risk 1 under an observed outdoor hazard condition and
// risk 0.5 while a cold or hot latch is active; everything else is 0. It is
// ordering evidence for admission, not a safety guard: native danger checks
// and Hands dispatch guards still apply.
func RoundsDevelopmentRisk(id ConcernID, f RoundsFacts, l RoundsLatches) domain.Fact[float64] {
	outdoor := false
	for _, w := range ConcernLabor(id) {
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
