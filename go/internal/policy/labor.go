package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Native work types routine goals dispatch pawn work through. Names are
// RimWorld WorkTypeDef defNames as ListPawns reports them.
const (
	WorkConstruction WorkType = "Construction"
	WorkResearch     WorkType = "Research"
	WorkMining       WorkType = "Mining"
	WorkPlantCutting WorkType = "PlantCutting"
	WorkCrafting     WorkType = "Crafting"
	WorkHauling      WorkType = "Hauling"
	WorkCleaning     WorkType = "Cleaning"
	WorkBasic        WorkType = "BasicWorker"
	WorkHandling     WorkType = "Handling"
	WorkCooking      WorkType = "Cooking"
	WorkFirefighter  WorkType = "Firefighter"
	WorkTailoring    WorkType = "Tailoring"
	WorkSmithing     WorkType = "Smithing"
)

// LaborProfile lists the native work types a goal's methods can put pawns to:
// a goal is admissible when any listed type still has free labor. An empty
// profile means the goal issues no pawn work and is bounded only by the
// coarse worker count.
type LaborProfile []WorkType

// GoalLabor names each routine need's labor profile. Goals not listed here
// (emergencies, monitoring-only needs, configuration pushes) have no profile.
func GoalLabor(id GoalID) LaborProfile {
	switch id {
	case EnsureBasicDefense, EnsureComfort, EnsureBasicComfort, EnsureExpansion, MaintainEssentialRepairs, MaintainStoneShell, MaintainSleeping, MaintainFoodStorage, MaintainHomeCoverage, MaintainAnimalContainment, MaintainLighting, MaintainFlooring, MaintainRoutes:
		return LaborProfile{WorkConstruction}
	case MaintainWood, RemoveBlight:
		return LaborProfile{WorkPlantCutting}
	case EnsureResearch:
		return LaborProfile{WorkResearch}
	case MaintainResource:
		return LaborProfile{WorkMining, WorkPlantCutting, WorkCrafting}
	case MaintainEquipment:
		// A wear order is a forced job any pawn carries; the replacement
		// bill needs the bench's work type (tailoring, smithing or crafting).
		return LaborProfile{WorkTailoring, WorkSmithing, WorkCrafting}
	case SecureSupplies, MaintainStorage, MaintainWaste:
		return LaborProfile{WorkHauling}
	case MaintainCleanFacilities:
		// The bounded cleaning response is a player-forced order any basic
		// worker not incapable of Cleaning can carry, and it exists for the
		// case where nobody has Cleaning at priority > 0.
		return LaborProfile{WorkCleaning, WorkBasic}
	case MaintainHerd:
		return LaborProfile{WorkHandling}
	case MaintainAnimalFeed:
		// Feed is a kibble bill (the butcher spot's Cooking work) hauled
		// into the animals' area (#311); handlers never carry it, and the
		// tribal baseline has none, which left the delivered bill
		// labor_unavailable behind its completed zone.
		return LaborProfile{WorkCooking, WorkHauling}
	case MaintainFireSafety:
		return LaborProfile{WorkFirefighter}
	}
	return nil
}

// RoutineLabor counts, per native work type, the pawns RoutineWorkers counts
// whose work settings enable that type (priority above zero and not
// disabled). Any counted pawn with unknown or empty work settings makes the
// whole census unknown: native lists every WorkTypeDef for a pawn whose work
// applies, so an empty list is an unobserved census, and partial labor
// evidence must not admit or refuse work.
func RoutineLabor(pawns []WorkPawn) domain.Fact[map[WorkType]int] {
	labor := map[WorkType]int{}
	for _, p := range pawns {
		available, known := p.Available.Value()
		applies, appliesKnown := p.Applies.Value()
		if known && !available || appliesKnown && !applies {
			continue
		}
		work, workKnown := p.Work.Value()
		if !known || !appliesKnown || !workKnown || len(work) == 0 {
			return domain.Unknown[map[WorkType]int]()
		}
		for _, w := range work {
			if w.Priority > 0 && !w.Disabled {
				labor[w.Work]++
			}
		}
	}
	return domain.Known(labor)
}

// laborLedger tracks free labor per work type during one ranking pass.
type laborLedger struct {
	free  map[WorkType]int
	known bool
}

func newLaborLedger(labor domain.Fact[map[WorkType]int]) laborLedger {
	v, known := labor.Value()
	l := laborLedger{free: map[WorkType]int{}, known: known}
	for w, n := range v {
		l.free[w] = n
	}
	return l
}

// take reserves one pawn of the first free type in profile. With an unknown
// census or an empty profile nothing is reserved and the goal is admissible;
// otherwise the returned work type names the bottleneck.
func (l *laborLedger) take(profile LaborProfile) (bottleneck WorkType, ok bool) {
	if !l.known || len(profile) == 0 {
		return "", true
	}
	for _, w := range profile {
		if l.free[w] > 0 {
			l.free[w]--
			return "", true
		}
	}
	sorted := append(LaborProfile(nil), profile...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i] < sorted[j] })
	return sorted[0], false
}

func validLabor(profile LaborProfile) bool {
	if len(profile) > 64 {
		return false
	}
	seen := map[WorkType]bool{}
	for _, w := range profile {
		if !validResource(Resource(w)) || seen[w] {
			return false
		}
		seen[w] = true
	}
	return true
}
