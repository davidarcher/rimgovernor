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

	// Unlisted rooms are Expand by decision; the armory and wardrobe equip the defense and are Secure (#2527).
	PlannedGraveyard:        domain.TierExpand,
	PlannedTrainingRange:    domain.TierExpand,
	PlannedSparringRing:     domain.TierExpand,
	PlannedWasteYard:        domain.TierExpand,
	PlannedYard:             domain.TierExpand,
	PlannedTomb:             domain.TierExpand,
	PlannedMorgue:           domain.TierExpand,
	PlannedIncinerator:      domain.TierExpand,
	PlannedPrison:           domain.TierExpand,
	PlannedReserve:          domain.TierExpand,
	PlannedBattery:          domain.TierExpand,
	PlannedArmory:           domain.TierSecure,
	PlannedWardrobe:         domain.TierSecure,
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

// AdoptedTier is the tier the finishing-skill adoption of an existing site
// restates: the tier native already reads on it, so the adoption never moves
// the build. A site with no tier (placed before tiers, or by hand) is
// unlisted, so Expand.
func AdoptedTier(site ConstructionSite) domain.ConstructionTier {
	if tier, known := site.Tier.Value(); known {
		return tier
	}
	return domain.TierExpand
}

// PlannerTier is the tier of a loose building a Concern's planner places
// outside any planned room. Every concern has a tier: a concern the ladder
// does not list is Expand by decision. The defense concerns are Secure (#2527;
// promotion is DefenseBuildTier). Power takes the tier of what it serves: the
// refrigeration planner's builds stay Sustain, and the generic power planner
// proposes no consumer, so it is Expand.
func PlannerTier(concern ConcernID, phase Phase) domain.ConstructionTier {
	switch concern {
	case MaintainHousing:
		if phase == HousingSleeping {
			return domain.TierSustain
		}
		return domain.TierSurvive
	case EnsureCooking, EnsureResearch:
		return domain.TierSurvive
	case MaintainButcherSpot, MaintainMedicalReserves, MaintainRefrigeration, MaintainStockpiles, MaintainFoodStorage:
		return domain.TierSustain
	case EnsureBasicDefense, EnsureDefensiveLayout:
		return domain.TierSecure
	case EnsureComfort, MaintainAnimalContainment:
		return domain.TierComfort
	case MaintainResource:
		return domain.TierProduce
	default:
		return domain.TierExpand
	}
}
