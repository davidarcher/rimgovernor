package policy

import (
	"fmt"
	"sort"
)

// GrowerCropRequest asks which observed plant growers should sow a
// different crop than they do now. Choices are the same crop census a
// field request carries; Urgent prefers the fastest crop the way an urgent
// site selection does.
type GrowerCropRequest struct {
	Choices []CropChoice
	Growers []PlantGrower
	Urgent  bool
	Field   FieldRequest
}

// GrowerCropChoice is one grower whose crop should change, with the
// ranking that chose the crop.
type GrowerCropChoice struct {
	Grower  string
	Current string
	Crop    CropChoice
	Reason  string
	Ranking []FieldCandidate
}

// PlanGrowerCrops ranks, for each grower that can sow, every available
// edible crop carrying the grower's sow tag by its nutrition rate over the
// grower's fertility (the same rate a basin candidate is scored with), and
// returns the growers whose current crop is not the winner. A grower whose
// crop, sow tag, fertility or sowing state is unknown is left alone, as is
// one already sowing the winner; growers are returned in id order.
func PlanGrowerCrops(r GrowerCropRequest) []GrowerCropChoice {
	var out []GrowerCropChoice
	for _, g := range r.Growers {
		tag, tk := g.SowTag.Value()
		current, ck := g.Crop.Value()
		fertility, fk := g.Fertility.Value()
		if !positive(g.CanSow) || !tk || tag == "" || !ck || !fk || !fieldPositive(fertility) || g.ID == "" {
			continue
		}
		ranking := growerCropRanking(r.Choices, tag, fertility, r.Urgent, r.Field)
		if len(ranking) == 0 || ranking[0].Score <= 0 || ranking[0].Crop.Name == current {
			continue
		}
		out = append(out, GrowerCropChoice{Grower: g.ID, Current: current, Crop: ranking[0].Crop, Reason: ranking[0].Reason, Ranking: ranking})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Grower < out[j].Grower })
	return out
}

// growerCropRanking scores every sowable crop for one grower; excluded
// crops carry a zero score and their reason.
func growerCropRanking(choices []CropChoice, tag string, fertility float64, urgent bool, field FieldRequest) []FieldCandidate {
	var ranking []FieldCandidate
	for _, crop := range choices {
		c := FieldCandidate{Crop: crop}
		available, ak := crop.Available.Value()
		edible, ek := crop.Edible.Value()
		days, gk := crop.GrowDays.Value()
		tags, tk := crop.SowTags.Value()
		_, yk := crop.HarvestNutrition.Value()
		_, sk := crop.FertilitySensitivity.Value()
		switch {
		case siteKnownFalse(crop.DietAllowed) || positive(crop.RequiresPollution) || GrowsInDark(crop):
			c.Reason = "crop needs a compatible soil or dark site"
		case !ak || !ek || !gk || !fieldPositive(days) || !tk || !yk || !sk:
			c.Reason = "incomplete crop facts"
		case !available || !edible:
			c.Reason = "not an available edible crop"
		case !containsString(tags, tag):
			c.Reason = "crop cannot be sown on this grower"
		default:
			c.Score = cropRate(crop, fertility)
			for _, term := range cropChoiceTerms(field, crop, 1) {
				c.Score += term.Value
			}
			c.Reason = fmt.Sprintf("%.4f nutrition/day per cell", c.Score)
		}
		ranking = append(ranking, c)
	}
	sort.SliceStable(ranking, func(i, j int) bool {
		a, b := ranking[i], ranking[j]
		if (a.Score > 0) != (b.Score > 0) {
			return a.Score > 0
		}
		ad, _ := a.Crop.GrowDays.Value()
		bd, _ := b.Crop.GrowDays.Value()
		if urgent && a.Score > 0 && ad != bd {
			return ad < bd
		}
		if a.Score != b.Score {
			return a.Score > b.Score
		}
		if ad != bd {
			return ad < bd
		}
		return a.Crop.Name < b.Crop.Name
	})
	return ranking
}
