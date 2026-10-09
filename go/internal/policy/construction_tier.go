package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// plannedRoleTier is the construction tier of every planned room role (#2524,
// ladder in docs/developers/contracts/construction-tiers.md). A role without
// a row fails TestEveryPlannedRoleHasATier, so a new room kind states where it
// stands. Tier orders construction only; it is not the tech ladder.
var plannedRoleTier = map[PlannedRole]domain.ConstructionTier{
	PlannedShelter: domain.TierSurvive,
	PlannedKitchen: domain.TierSurvive,

	PlannedBedroom:    domain.TierSustain,
	PlannedHospital:   domain.TierSustain,
	PlannedButchery:   domain.TierSustain,
	PlannedStorage:    domain.TierSustain,
	PlannedMealCloset: domain.TierSustain,
	PlannedLab:        domain.TierSustain,
	PlannedFreezer:    domain.TierSustain,

	PlannedDining:  domain.TierComfort,
	PlannedRec:     domain.TierComfort,
	PlannedThrone:  domain.TierComfort,
	PlannedWorship: domain.TierComfort,
	PlannedSuite:   domain.TierComfort,
	PlannedBarn:    domain.TierComfort,
	PlannedPen:     domain.TierComfort,

	PlannedWorkshop: domain.TierProduce,

	// Unlisted rooms are Expand by decision; defense rooms get Secure in #2527.
	PlannedGraveyard:        domain.TierExpand,
	PlannedWasteYard:        domain.TierExpand,
	PlannedYard:             domain.TierExpand,
	PlannedTomb:             domain.TierExpand,
	PlannedMorgue:           domain.TierExpand,
	PlannedIncinerator:      domain.TierExpand,
	PlannedPrison:           domain.TierExpand,
	PlannedReserve:          domain.TierExpand,
	PlannedBattery:          domain.TierExpand,
	PlannedArmory:           domain.TierExpand,
	PlannedWardrobe:         domain.TierExpand,
	PlannedVetRoom:          domain.TierExpand,
	PlannedNursery:          domain.TierExpand,
	PlannedPlayroom:         domain.TierExpand,
	PlannedClassroom:        domain.TierExpand,
	PlannedDeathrestChamber: domain.TierExpand,
	PlannedContainmentCell:  domain.TierExpand,
	PlannedIsolationRoom:    domain.TierExpand,
}

// RoomTier is the tier a planned room's builds carry: its shell, floors and
// furniture. A role missing from the table is Expand.
func RoomTier(role PlannedRole) domain.ConstructionTier {
	if tier, ok := plannedRoleTier[role]; ok {
		return tier
	}
	return domain.TierExpand
}

// PlannerTier is the tier of a loose building a Concern's planner places
// outside any planned room. Unknown leaves the build untiered (ungated until
// #2525 covers the path, and defense until #2527 assigns Secure). Power takes
// the tier of what it serves: the refrigeration planner's builds stay Sustain,
// and the generic power planner proposes no consumer, so it is Expand.
func PlannerTier(concern ConcernID, phase Phase) domain.Fact[domain.ConstructionTier] {
	var tier domain.ConstructionTier
	switch concern {
	case MaintainHousing:
		tier = domain.TierSurvive
		if phase == HousingSleeping {
			tier = domain.TierSustain
		}
	case EnsureCooking, EnsureResearch:
		tier = domain.TierSurvive
	case MaintainButcherSpot, MaintainMedicalReserves, MaintainRefrigeration, MaintainStockpiles, MaintainFoodStorage:
		tier = domain.TierSustain
	case EnsureComfort, MaintainAnimalContainment:
		tier = domain.TierComfort
	case MaintainResource:
		tier = domain.TierProduce
	case EnsureBasicPower, EnsureTemperatureSafety, MaintainLighting, MaintainFlooring, MaintainRoutes, MaintainBurial, MaintainIncineration, MaintainPopulation:
		tier = domain.TierExpand
	default:
		return domain.Unknown[domain.ConstructionTier]()
	}
	return domain.Known(tier)
}
