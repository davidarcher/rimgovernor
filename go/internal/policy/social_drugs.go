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

// SocialCropCells is each social crop's fixed field ceiling (#1226).
const SocialCropCells = 9

// PlanSocialCrop is the cells a social crop still needs under its nine-cell
// ceiling, zero when none or out of season; the caller sites them in the
// layout plan's field blocks (#1226).
func PlanSocialCrop(crop CropChoice, climate CropClimate, existing int) int {
	if crop.Name != "Plant_Hops" && crop.Name != "Plant_Smokeleaf" || existing < 0 || existing >= SocialCropCells {
		return 0
	}
	available, known := crop.Available.Value()
	sowing, sk := climate.Sowing.Value()
	days, dk := crop.GrowDays.Value()
	season, remaining := climate.DaysRemaining.Value()
	if !known || !available || !sk || !sowing || !dk || !fieldPositive(days) || !remaining || season < days*2.5 {
		return 0
	}
	return SocialCropCells - existing
}
