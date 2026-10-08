package policy

import (
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
func ConcernLabor(id ConcernID) LaborProfile {
	switch id {
	case ClearHomeObstructions, ClearAncientShrine, EnsureBasicDefense, EnsureComfort, MaintainHousing, MaintainEssentialRepairs, MaintainStoneShell, MaintainFoodStorage, MaintainButcherSpot, MaintainHomeCoverage, MaintainAnimalContainment, MaintainLighting, MaintainFlooring, MaintainRoutes, EnsureMechCharger, MaintainGeneBank:
		return LaborProfile{WorkConstruction}
	case RemoveBlight:
		return LaborProfile{WorkPlantCutting}
	case MaintainArt:
		return LaborProfile{WorkArt}
	case EnsureResearch:
		return LaborProfile{WorkResearch}
	case MaintainResource:
		return LaborProfile{WorkMining, WorkPlantCutting, WorkCrafting}
	case MaintainEquipment:
		// A wear order is a forced job any pawn carries; the replacement
		// bill needs the bench's work type. Construction keeps the workshop
		// rung available before a bench exists to request its crafting work.
		return LaborProfile{WorkConstruction, WorkTailoring, WorkSmithing, WorkCrafting}
	case MaintainIncineration, ManagePollution:
		return LaborProfile{WorkHauling}
	case MaintainBurial:
		// The shell, sarcophagus and graves are built; vanilla haulers carry
		// the corpses.
		return LaborProfile{WorkConstruction}
	case MaintainCleanFacilities:
		// The bounded cleaning response is a player-forced order any basic
		// worker not incapable of Cleaning can carry, and it exists for the
		// case where nobody has Cleaning at priority > 0.
		return LaborProfile{WorkCleaning, WorkBasic}
	case MaintainHerd:
		return LaborProfile{WorkHandling}
	case MaintainBabyFeeding:
		return LaborProfile{WorkCooking}
	case MaintainFireSafety:
		return LaborProfile{WorkFirefighter}
	case MaintainFirebreak:
		// Plants are cut by plant cutters, wooden ruins taken down by
		// builders (#1548).
		return LaborProfile{WorkPlantCutting, WorkConstruction}
	case MaintainStockpiles:
		// A resized or retargeted stockpile is refilled by haulers (#725).
		return LaborProfile{WorkHauling}
	case MaintainMedicalReserves:
		// A medicine bill at a crafting bench, or a wild healroot harvest
		// and wild healroot harvests.
		return LaborProfile{WorkCrafting, WorkPlantCutting}
	}
	return nil
}

// RoundsLabor counts, per native work type, the pawns RoundsWorkers counts
// whose work settings enable that type (priority above zero and not
// disabled). Any counted pawn with unknown or empty work settings makes the
// whole census unknown: native lists every WorkTypeDef for a pawn whose work
// applies, so an empty list is an unobserved census, and partial labor
// evidence must not admit or refuse work.
func RoundsLabor(pawns []WorkPawn) domain.Fact[map[WorkType]int] {
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
