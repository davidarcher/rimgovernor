package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

const SocialDrugPolicyName = "RimGovernor social drugs"

func DrugPolicyChange(pawn WorkPawn) bool {
	writable, known := pawn.DrugPolicyWritable.Value()
	available, ready := pawn.Available.Value()
	return known && writable && ready && available && pawn.DrugPolicyName != SocialDrugPolicyName
}

func BrewingFinished(research domain.Fact[ResearchFacts]) bool {
	facts, known := research.Value()
	if known {
		for _, project := range facts.Finished {
			if project == "Brewing" {
				return true
			}
		}
	}
	return false
}

// Small floors use ordinary production bills and never reserve food nutrition.
func SocialDrugTargets(research domain.Fact[ResearchFacts]) map[Resource]int64 {
	if !BrewingFinished(research) {
		return nil
	}
	return map[Resource]int64{"Beer": 12, "SmokeleafJoint": 12}
}

func PlanSocialCrop(crop CropChoice, climate CropClimate, existing int, site FarmSiteRequest) FarmSitePlan {
	if crop.Name != "Plant_Hops" && crop.Name != "Plant_Smokeleaf" || existing < 0 || existing >= 9 {
		return FarmSitePlan{}
	}
	available, known := crop.Available.Value()
	sowing, sk := climate.Sowing.Value()
	days, dk := crop.GrowDays.Value()
	season, remaining := climate.DaysRemaining.Value()
	if !known || !available || !sk || !sowing || !dk || !fieldPositive(days) || !remaining || season < days*2.5 {
		return FarmSitePlan{}
	}
	// Equal unit weighting ranks soil/travel costs without a food yield.
	crop.HarvestUnits = domain.Known(1.0)
	site.Crop, site.Needed = crop, 9-existing
	site.StrictTarget = true
	return PlanFarmSites(site)
}
