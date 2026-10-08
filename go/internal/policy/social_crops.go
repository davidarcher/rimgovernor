package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

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

// SocialCropCells is each social crop's fixed field ceiling (#1226).
const SocialCropCells = 9

// socialCropResources are the harvests the social drugs brew or roll from; a
// crop is social by what it harvests, not by name.
var socialCropResources = map[Resource]bool{"Hops": true, "SmokeleafLeaves": true}

// IsSocialCrop is whether crop harvests a social-drug ingredient.
func IsSocialCrop(crop CropChoice) bool {
	harvests, known := crop.Harvests.Value()
	return known && socialCropResources[harvests]
}

// HayResource is the thing animal-feed crops harvest.
const HayResource Resource = "Hay"

// IsHayCrop is whether crop harvests hay.
func IsHayCrop(crop CropChoice) bool {
	harvests, known := crop.Harvests.Value()
	return known && harvests == HayResource
}

// PlanSocialCrop is the cells a social crop still needs under its nine-cell
// ceiling, zero when none or out of season; the caller sites them in the
// layout plan's field blocks (#1226).
func PlanSocialCrop(crop CropChoice, climate CropClimate, existing int) int {
	if !IsSocialCrop(crop) || existing < 0 || existing >= SocialCropCells {
		return 0
	}
	available, known := crop.Available.Value()
	sowing, sk := climate.SowingOutdoors().Value()
	days, dk := crop.GrowDays.Value()
	season, remaining := climate.DaysRemaining.Value()
	if !known || !available || !sk || !sowing || !dk || !fieldPositive(days) || !remaining || season < days*2.5 {
		return 0
	}
	return SocialCropCells - existing
}
