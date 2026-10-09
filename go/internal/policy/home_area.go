package policy

import (
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HomeAreaMargin is the Chebyshev margin the home area keeps around the
// colony extent.
const HomeAreaMargin int32 = 4

// HomeAreaOutlier is how far (Chebyshev) an extent region may lie from the
// largest one and still be home; farther regions are outposts, not base.
const HomeAreaOutlier int32 = 16

// HomeAreaPlan is the diff from the current home to the target home: the
// base footprint (colony extent plus margin, largest region and its near
// neighbours) with the game's auto-expand off.
type HomeAreaPlan struct {
	AutoOff    bool
	Set, Clear []domain.Cell
}

func (p HomeAreaPlan) Empty() bool { return !p.AutoOff && len(p.Set) == 0 && len(p.Clear) == 0 }

// MethodID names the diff, so an admitted edit is not proposed twice.
func (p HomeAreaPlan) MethodID() domain.MethodID {
	data, _ := json.Marshal(p)
	sum := sha256.Sum256(data)
	return domain.MethodID(fmt.Sprintf("home-%x", sum[:16]))
}

// PlanHomeArea derives the home-area diff. Unknown when the extent, the
// current home cells or the auto-expand setting is unknown. hold are cells the
// base footprint leaves out of home (RangeHomeHold).
func PlanHomeArea(bounds domain.Fact[Bounds], construction domain.Fact[CurrentConstruction], claims domain.Fact[[]ConstructionClaim], home domain.Fact[HomeCoverageObservation], hold []domain.Cell) (domain.Fact[HomeAreaPlan], error) {
	unknown := domain.Unknown[HomeAreaPlan]()
	census, known := home.Value()
	if !known {
		return unknown, nil
	}
	current, ck := census.Home.Value()
	auto, ak := census.AutoHome.Value()
	if !ck || !ak {
		return unknown, nil
	}
	extent, err := DeriveColonyExtent(ColonyExtentRequest{Bounds: bounds, Construction: construction, Claims: claims, Home: home, Margin: HomeAreaMargin})
	if err != nil {
		return unknown, err
	}
	value, known := extent.Value()
	if !known {
		return unknown, nil
	}
	target := homeBase(value)
	for _, c := range hold {
		delete(target, c)
	}
	plan := HomeAreaPlan{AutoOff: auto, Set: []domain.Cell{}, Clear: []domain.Cell{}}
	have := map[domain.Cell]bool{}
	for _, c := range current {
		if have[c] {
			continue
		}
		have[c] = true
		if !target[c] {
			plan.Clear = append(plan.Clear, c)
		}
	}
	for c := range target {
		if !have[c] {
			plan.Set = append(plan.Set, c)
		}
	}
	sort.Slice(plan.Set, func(i, j int) bool { return extentCellLess(plan.Set[i], plan.Set[j]) })
	sort.Slice(plan.Clear, func(i, j int) bool { return extentCellLess(plan.Clear[i], plan.Clear[j]) })
	return domain.Known(plan), nil
}

// homeBase is the base footprint of an extent: its largest region and every
// region within HomeAreaOutlier of it.
func homeBase(value ColonyExtent) map[domain.Cell]bool {
	target := map[domain.Cell]bool{}
	if len(value.Regions) > 0 {
		base := 0
		for i, r := range value.Regions {
			if len(r.Cells) > len(value.Regions[base].Cells) {
				base = i
			}
		}
		b := value.Regions[base]
		minX, minZ, maxX, maxZ := b.Cells[0].Cell.X, b.Cells[0].Cell.Z, b.Cells[0].Cell.X, b.Cells[0].Cell.Z
		baseCells := map[domain.Cell]bool{}
		for _, c := range b.Cells {
			baseCells[c.Cell] = true
			minX, maxX = min(minX, c.Cell.X), max(maxX, c.Cell.X)
			minZ, maxZ = min(minZ, c.Cell.Z), max(maxZ, c.Cell.Z)
		}
		// near reports whether any base cell lies within the cut-off of c;
		// the bounding box rejects far cells without a scan.
		near := func(c domain.Cell) bool {
			d := HomeAreaOutlier
			if c.X < minX-d || c.X > maxX+d || c.Z < minZ-d || c.Z > maxZ+d {
				return false
			}
			for dx := -d; dx <= d; dx++ {
				for dz := -d; dz <= d; dz++ {
					if baseCells[domain.Cell{X: c.X + dx, Z: c.Z + dz}] {
						return true
					}
				}
			}
			return false
		}
		for i, r := range value.Regions {
			keep := i == base
			for j := 0; !keep && j < len(r.Cells); j++ {
				keep = near(r.Cells[j].Cell)
			}
			if keep {
				for _, c := range r.Cells {
					target[c.Cell] = true
				}
			}
		}
	}
	return target
}
