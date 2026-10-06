package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Tree plantations (#2289): a standing wood demand sows a rectangular growing
// zone of one tree species, one tree per domain.TreeCellsPerTree cells (the
// native lattice, #2290). A tree yields harvestYield * (0.5 + 0.5 * (growth -
// harvestMinGrowth) / (1 - harvestMinGrowth)) wood when felled at growth, so
// each species is priced at the better of two fell points: as soon as it is
// harvestable (half its wood, least land time) or at full growth (all of it,
// least labor per wood). The price is wood per labor tick over the tree's land
// (cells x days x TreeCellDayLaborTicks), its sowing and its felling.

// TreeCellDayLaborTicks is the labor ticks one cell held for one day costs: it
// converts the land a plantation occupies into the same unit as sowing and
// felling work, so the fell point trades land time against labor per wood. It
// is a judgement, not a game constant: raise it to fell earlier.
const TreeCellDayLaborTicks = 250.0

// IsTreeCrop is whether crop is a tree plantation species: a plant whose
// harvest fells it and whose neighbours the native sower keeps clear.
func IsTreeCrop(crop CropChoice) bool {
	block, bk := crop.BlockAdjacentSow.Value()
	destroys, dk := crop.HarvestDestroys.Value()
	return bk && dk && block && destroys
}

// treePricing is the fell fraction of crop's growth that prices best, the wood
// one tree gives then, and the wood per labor tick it earns. False when a
// crop fact is unknown.
func treePricing(crop CropChoice) (fraction, wood, price float64, ok bool) {
	days, gk := crop.GrowDays.Value()
	yield, yk := crop.UnitsPerCell.Value()
	minGrowth, mk := crop.HarvestMinGrowth.Value()
	sow, sk := crop.SowWork.Value()
	fell, fk := crop.HarvestWork.Value()
	if !gk || !yk || !mk || !sk || !fk || !fieldPositive(days) || !fieldPositive(yield) || !(minGrowth >= 0 && minGrowth < 1) || sow < 0 || fell < 0 {
		return 0, 0, 0, false
	}
	for _, f := range []float64{minGrowth, 1} {
		w := yield * (f + 1 - 2*minGrowth) / (2 * (1 - minGrowth))
		labor := domain.TreeCellsPerTree*days*f*TreeCellDayLaborTicks + sow + fell
		if p := w / labor; p >= price {
			fraction, wood, price = f, w, p
		}
	}
	return fraction, wood, price, price > 0
}

// treeSowable is why crop cannot be sown as a plantation here, empty when it
// can: not researched, no colonist at the sow skill (the best Plants level among
// those who sow), or wild-only and not native to the biome.
func treeSowable(crop CropChoice, biome domain.Fact[string], skill domain.Fact[int32]) string {
	if reason := cropSowable(crop, skill); reason != "" {
		return reason
	}
	if wild, known := crop.MustBeWildToSow.Value(); !known || wild {
		name, bk := biome.Value()
		native, nk := crop.WildBiomes.Value()
		if !bk || !nk || !slices.Contains(native, name) {
			return "not native to the biome"
		}
	}
	return ""
}

// cropSowable is the availability and sow-skill gate of any resource crop.
func cropSowable(crop CropChoice, skill domain.Fact[int32]) string {
	if available, known := crop.Available.Value(); !known || !available {
		return "crop not available"
	}
	if need, known := crop.SowMinSkill.Value(); known && need > 0 {
		if have, ok := skill.Value(); !ok || have < need {
			return "no colonist meets the sow skill"
		}
	}
	return ""
}

// TreeFellFraction is the colony-wide growth fraction at which plantation trees
// are felled: that of the best-priced tree species harvesting resource that can
// be sown now. False when no species can, so nothing gates the chop. It is a
// pure function of the catalog and the colony, recomputed each review.
func TreeFellFraction(choices []CropChoice, resource Resource, biome domain.Fact[string], skill domain.Fact[int32]) (float64, bool) {
	best, fraction := 0.0, 0.0
	for _, crop := range choices {
		if harvests, known := crop.Harvests.Value(); !known || harvests != resource || !IsTreeCrop(crop) || treeSowable(crop, biome, skill) != "" {
			continue
		}
		if f, _, price, ok := treePricing(crop); ok && price > best {
			best, fraction = price, f
		}
	}
	return fraction, best > 0
}

// GrowerSkill is the best Plants level among the colonists who sow: available,
// able to do growing work. Unknown while any pawn's availability, work or skills
// were not read.
func GrowerSkill(fact domain.Fact[[]WorkPawn]) domain.Fact[int32] {
	pawns, known := fact.Value()
	if !known {
		return domain.Unknown[int32]()
	}
	best := int32(0)
	for _, pawn := range pawns {
		available, ak := pawn.Available.Value()
		work, wk := pawn.Work.Value()
		skills, sk := pawn.Skills.Value()
		if !ak || available && (!wk || !sk) {
			return domain.Unknown[int32]()
		}
		if !available {
			continue
		}
		profile := BuildProfile(pawn)
		for _, row := range work {
			if row.Work != WorkGrowing || row.Disabled || !profile.Capable(row.Work, 0) {
				continue
			}
			for _, skill := range skills {
				if skill.Name == "Plants" && !skill.Disabled {
					best = max(best, int32(skill.Level))
				}
			}
		}
	}
	return domain.Known(best)
}
