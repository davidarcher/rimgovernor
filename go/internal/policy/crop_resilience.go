package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// CropWorkers counts observed enabled growers and capable cooks. Unknown pawn
// availability or work rows cannot establish that the colony has no cook.
func CropWorkers(fact domain.Fact[[]WorkPawn]) (growers, cooks domain.Fact[int]) {
	pawns, known := fact.Value()
	if !known {
		return
	}
	g, c := 0, 0
	for _, pawn := range pawns {
		available, ak := pawn.Available.Value()
		work, wk := pawn.Work.Value()
		if !ak {
			return
		}
		if !available {
			continue
		}
		_, sk := pawn.Skills.Value()
		if !wk || !sk {
			return
		}
		profile := BuildProfile(pawn)
		for _, row := range work {
			if row.Disabled || !profile.Capable(row.Work, 0) {
				continue
			}
			if row.Work == WorkGrowing && row.Priority > 0 {
				g++
			}
			if row.Work == WorkCooking {
				c++
			}
		}
	}
	return domain.Known(g), domain.Known(c)
}

// Harvest work is priced against the observed grower share. With spare growers,
// the time before the first harvest matters more than work saved per nutrition.
// Both charges are fractions of daily output, so they compare across crops.
func cropChoiceTerms(r FieldRequest, crop CropChoice, cells int) []FarmSiteTerm {
	unit := cropRate(crop, 1) * float64(cells)
	var terms []FarmSiteTerm
	work, wk := crop.HarvestWork.Value()
	yield, yk := crop.HarvestNutrition.Value()
	days, dk := crop.GrowDays.Value()
	growers, gk := r.Growers.Value()
	people, pk := r.Colonists.Value()
	if wk && yk && dk && gk && pk && foodNumber(work) && work >= 0 && fieldPositive(yield) && fieldPositive(days) && growers >= 0 && people > 0 {
		terms = append(terms, FarmSiteTerm{"labor", -unit * .0002 * work / yield * float64(people) / float64(max(1, growers))},
			FarmSiteTerm{"harvest-delay", -unit * .03 * days})
	}
	if cooks, known := r.Cooks.Value(); known && cooks == 0 {
		if raw, known := crop.RawPreferred.Value(); known && raw {
			terms = append(terms, FarmSiteTerm{"cook", unit})
		}
	}
	return terms
}

func cropSoilCompatible(crop CropChoice, cell SiteCell) bool {
	if GrowsInDark(crop) {
		glow, known := cell.Glow.Value()
		if !known || glow != 0 {
			return false
		}
	}
	pollution, pk := cell.Polluted.Value()
	if positive(crop.RequiresPollution) && (!pk || !pollution) {
		return false
	}
	if positive(crop.RequiresCleanSoil) && (!pk || pollution) {
		return false
	}
	return true
}

// Indoor resilience is credited only for an observed threat. Feasibility
// (power, heat, crop and cells) is established before these terms are added.
func siteRiskTerms(r FieldRequest, crop CropChoice, cells int) []FarmSiteTerm {
	unit := cropRate(crop, 1) * float64(cells)
	var terms []FarmSiteTerm
	if conditions, known := r.Conditions.Value(); known {
		for _, condition := range conditions {
			if condition.Definition == "ToxicFallout" {
				terms = append(terms, FarmSiteTerm{"risk-fallout", 2 * unit})
				break
			}
		}
	}
	if gap, known := HarvestGapDays(r.Calendar, domain.Known([]DisasterCondition{})).Value(); known && gap > 0 {
		terms = append(terms, FarmSiteTerm{"risk-frost", 2 * unit * min(1, gap/max(1, r.ReserveDays))})
	}
	return terms
}

func farmNeighbors(c domain.Cell) [4]domain.Cell {
	return [4]domain.Cell{{X: c.X - 1, Z: c.Z}, {X: c.X + 1, Z: c.Z}, {X: c.X, Z: c.Z - 1}, {X: c.X, Z: c.Z + 1}}
}
