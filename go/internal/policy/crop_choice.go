package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"sort"
)

type CropClimate struct {
	Sowing        domain.Fact[bool]
	DaysRemaining domain.Fact[float64]
}
type CropChoice struct {
	Name                                                                   string
	Available, Edible                                                      domain.Fact[bool]
	GrowDays, FertilityMin, FertilitySensitivity, HarvestNutrition, Demand domain.Fact[float64]
}

// ChooseCrop uses native yield, free soil and the remaining seasonal budget.
// A short stored-food runway prefers the fastest viable crop; future yield
// remains capacity and cannot recover the stored-food need.
func ChooseCrop(choices []CropChoice, climate CropClimate, runway domain.Fact[float64], cells []SiteCell, protected []domain.Cell) (CropChoice, bool) {
	sowing, sk := climate.Sowing.Value()
	season, dk := climate.DaysRemaining.Value()
	if !sk || !sowing || !dk || !fieldPositive(season) || len(choices) > 256 || len(cells) > 65536 || len(protected) > 65536 {
		return CropChoice{}, false
	}
	seenCells := map[domain.Cell]bool{}
	for _, cell := range cells {
		if seenCells[cell.Cell] {
			return CropChoice{}, false
		}
		seenCells[cell.Cell] = true
	}
	seenNames := map[string]bool{}
	for _, crop := range choices {
		if seenNames[crop.Name] {
			return CropChoice{}, false
		}
		seenNames[crop.Name] = true
	}
	blocked := map[domain.Cell]bool{}
	for _, cell := range protected {
		blocked[cell] = true
	}
	type candidate struct {
		crop       CropChoice
		rate, days float64
	}
	var candidates []candidate
	for _, crop := range choices {
		if crop.Name != "Plant_Rice" && crop.Name != "Plant_Potato" && crop.Name != "Plant_Corn" {
			continue
		}
		available, ak := crop.Available.Value()
		edible, ek := crop.Edible.Value()
		days, gk := crop.GrowDays.Value()
		yield, yk := crop.HarvestNutrition.Value()
		minimum, mk := crop.FertilityMin.Value()
		sensitivity, fk := crop.FertilitySensitivity.Value()
		if !ak || !available || !ek || !edible || !gk || !yk || !mk || !fk || !fieldPositive(days) || !fieldPositive(yield) || !fieldPositive(minimum) || !foodNumber(sensitivity) || days*2.5 > season {
			continue
		}
		total := 0.0
		count := 0
		for _, cell := range cells {
			if soil, ok := freeCropSoil(cell, minimum, blocked); ok {
				total += soil
				count++
			}
		}
		if count == 0 {
			continue
		}
		factor := math.Max(0, 1+(total/float64(count)-1)*sensitivity)
		rate := yield * factor / days
		if !foodNumber(rate) || rate == 0 {
			continue
		}
		candidates = append(candidates, candidate{crop, rate, days})
	}
	if len(candidates) == 0 {
		return CropChoice{}, false
	}
	fastest := candidates[0].days
	for _, c := range candidates {
		fastest = min(fastest, c.days)
	}
	foodDays, known := runway.Value()
	urgent := known && foodNumber(foodDays) && foodDays < fastest*2.5
	sort.Slice(candidates, func(i, j int) bool {
		a, b := candidates[i], candidates[j]
		if urgent && a.days != b.days {
			return a.days < b.days
		}
		if a.rate != b.rate {
			return a.rate > b.rate
		}
		if a.days != b.days {
			return a.days < b.days
		}
		return a.crop.Name < b.crop.Name
	})
	return candidates[0].crop, true
}
func freeCropSoil(cell SiteCell, minimum float64, blocked map[domain.Cell]bool) (float64, bool) {
	walk, wk := cell.Walkable.Value()
	occupied, ok := cell.Occupied.Value()
	zone, zk := cell.Zone.Value()
	roof, rk := cell.Roofed.Value()
	soil, fk := cell.Fertility.Value()
	return soil, !blocked[cell.Cell] && wk && walk && ok && !occupied && zk && !zone && rk && !roof && fk && foodNumber(soil) && soil >= minimum
}

func FieldTarget(colonists domain.Fact[int64], crop CropChoice, reserve float64) domain.Fact[int] {
	count, ck := colonists.Value()
	demand, dk := crop.Demand.Value()
	days, gk := crop.GrowDays.Value()
	yield, yk := crop.HarvestNutrition.Value()
	if !ck || count < 0 || !dk || !gk || !yk || !fieldPositive(demand) || !fieldPositive(days) || !fieldPositive(yield) || !fieldPositive(reserve) {
		return domain.Unknown[int]()
	}
	target := math.Max(float64(count)*10, math.Ceil(demand*(days*2.5+reserve)/yield))
	if !fieldPositive(target) || target > 65536 {
		return domain.Unknown[int]()
	}
	return domain.Known(int(target))
}

// GrowthFields adds up to 32 disjoint patches, preserving occupied soil, player
// footprints and existing zones. Missing cells never become free land.
func GrowthFields(bounds Bounds, anchor domain.Cell, cells []SiteCell, protected []domain.Cell, crop CropChoice, target domain.Fact[int], coverage domain.Fact[float64]) []Rectangle {
	count, ck := target.Value()
	fraction, fk := coverage.Value()
	minimum, mk := crop.FertilityMin.Value()
	if !ck || count <= 0 || count > 65536 || !fk || !foodNumber(fraction) || !mk || !fieldPositive(minimum) || len(cells) > 65536 || len(protected) > 65536 || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 4096 || bounds.Height > 4096 {
		return nil
	}
	seen := map[domain.Cell]bool{}
	for _, cell := range cells {
		if seen[cell.Cell] {
			return nil
		}
		seen[cell.Cell] = true
	}
	needed := int(math.Ceil(float64(count)*(1-fraction) - 1e-9))
	if needed <= 0 {
		return nil
	}
	blocked := map[domain.Cell]bool{}
	for _, c := range protected {
		blocked[c] = true
	}
	free := map[domain.Cell]bool{}
	var ordered []domain.Cell
	for _, cell := range cells {
		if cell.Cell.X < 0 || cell.Cell.Z < 0 || cell.Cell.X >= bounds.Width || cell.Cell.Z >= bounds.Height || free[cell.Cell] {
			continue
		}
		if _, ok := freeCropSoil(cell, minimum, blocked); ok {
			free[cell.Cell] = true
			ordered = append(ordered, cell.Cell)
		}
	}
	sort.Slice(ordered, func(i, j int) bool {
		a, b := squaredDistance(ordered[i], anchor), squaredDistance(ordered[j], anchor)
		if a != b {
			return a < b
		}
		return cellLess(ordered[i], ordered[j])
	})
	selected := map[domain.Cell]bool{}
	var patches []Rectangle
	for _, size := range []int32{4, 3, 2, 1} {
		for _, cell := range ordered {
			if len(selected) >= needed || len(patches) == 32 {
				return patches
			}
			patch := Rectangle{X: cell.X, Z: cell.Z, Width: size, Height: size}
			footprint := rectCells(patch)
			ok := true
			for _, c := range footprint {
				ok = ok && free[c] && !selected[c]
			}
			if !ok {
				continue
			}
			patches = append(patches, patch)
			for _, c := range footprint {
				selected[c] = true
			}
		}
	}
	return patches
}
