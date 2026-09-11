package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type Rectangle struct{ X, Z, Width, Height int32 }
type SiteCell struct {
	Cell                                            domain.Cell
	Walkable, Occupied, Zone, Roofed, SupportsLight domain.Fact[bool]
	Fertility                                       domain.Fact[float64]
}
type StarterRequest struct {
	Bounds Bounds
	Anchor domain.Cell
	Cells  []SiteCell
	// Protected contains accepted footprints, player exclusions and walkways.
	Protected                                                     []domain.Cell
	NutritionPerDay, CropGrowDays, HarvestNutrition, FertilityMin domain.Fact[float64]
}
type StarterLayout struct {
	Room, Storage Rectangle
	Farms         []Rectangle
	Score         int64
	SelectedCells int
	TargetCells   domain.Fact[int]
}

func rectCells(r Rectangle) []domain.Cell {
	cells := make([]domain.Cell, 0, int(r.Width*r.Height))
	for x := r.X; x < r.X+r.Width; x++ {
		for z := r.Z; z < r.Z+r.Height; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	return cells
}
func cellLess(a, b domain.Cell) bool { return a.X < b.X || a.X == b.X && a.Z < b.Z }
func squaredDistance(a, b domain.Cell) int64 {
	x, z := int64(a.X)-int64(b.X), int64(a.Z)-int64(b.Z)
	return x*x + z*z
}

// StarterLayouts ports the bounded 9x9 starter template and fragmented-field
// ranking. These are proposals: native placement/access previews still decide
// legality, and only admitted actions reserve geometry. Missing cells are blocked.
func StarterLayouts(r StarterRequest) ([]StarterLayout, error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 || len(r.Cells) > 65536 || len(r.Protected) > 65536 {
		return nil, errors.New("invalid starter site bounds")
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
	if !inBounds(r.Anchor) {
		return nil, errors.New("invalid colony anchor")
	}
	for _, f := range []domain.Fact[float64]{r.NutritionPerDay, r.CropGrowDays, r.HarvestNutrition, r.FertilityMin} {
		if v, k := f.Value(); k && (math.IsNaN(v) || math.IsInf(v, 0) || v < 0) {
			return nil, errors.New("invalid crop fact")
		}
	}
	n, nk := r.NutritionPerDay.Value()
	days, dk := r.CropGrowDays.Value()
	yield, yk := r.HarvestNutrition.Value()
	fertility, fk := r.FertilityMin.Value()
	target := 0
	targetFact := domain.Unknown[int]()
	if nk && dk && yk && days > 0 && yield > 0 {
		v := math.Ceil(n * days * 2.5 / yield)
		if math.IsInf(v, 0) || v > 65536 {
			return nil, errors.New("crop target exceeds bounded site search")
		}
		target = int(v)
		targetFact = domain.Known(target)
	}
	cells := make(map[domain.Cell]SiteCell, len(r.Cells))
	ordered := make([]domain.Cell, 0, len(r.Cells))
	for _, c := range r.Cells {
		if !inBounds(c.Cell) {
			return nil, errors.New("site cell out of bounds")
		}
		if _, exists := cells[c.Cell]; exists {
			return nil, errors.New("duplicate site cell")
		}
		if f, k := c.Fertility.Value(); k && (math.IsNaN(f) || math.IsInf(f, 0) || f < 0) {
			return nil, errors.New("invalid fertility")
		}
		cells[c.Cell] = c
		ordered = append(ordered, c.Cell)
	}
	sort.Slice(ordered, func(i, j int) bool { return cellLess(ordered[i], ordered[j]) })
	protected := map[domain.Cell]bool{}
	for _, c := range r.Protected {
		if !inBounds(c) {
			return nil, errors.New("protected cell out of bounds")
		}
		protected[c] = true
	}
	free := func(p domain.Cell) bool {
		c, exists := cells[p]
		return exists && !protected[p] && positive(c.Walkable) && positive(measured(c.Occupied, func(v bool) bool { return !v })) && positive(measured(c.Zone, func(v bool) bool { return !v }))
	}
	type site struct {
		score int64
		cell  domain.Cell
	}
	var sites []site
	for _, c := range ordered {
		if c.X+9 > r.Bounds.Width || c.Z+9 > r.Bounds.Height {
			continue
		}
		legal := true
		for _, p := range rectCells(Rectangle{c.X, c.Z, 9, 9}) {
			if !free(p) || !positive(cells[p].SupportsLight) {
				legal = false
				break
			}
		}
		if !legal {
			continue
		}
		score := squaredDistance(domain.Cell{X: c.X + 4, Z: c.Z + 4}, r.Anchor)
		for _, p := range rectCells(Rectangle{c.X, c.Z - 4, 9, 3}) {
			if !free(p) {
				score += 3
			}
		}
		sites = append(sites, site{score, c})
	}
	sort.Slice(sites, func(i, j int) bool {
		if sites[i].score != sites[j].score {
			return sites[i].score < sites[j].score
		}
		return cellLess(sites[i].cell, sites[j].cell)
	})
	if len(sites) > 24 {
		sites = sites[:24]
	}
	var layouts []StarterLayout
	for _, site := range sites {
		x, z := site.cell.X, site.cell.Z
		reserved := map[domain.Cell]bool{}
		for _, r := range []Rectangle{{x - 1, z - 1, 11, 11}, {x, z - 5, 9, 4}} {
			for _, p := range rectCells(r) {
				reserved[p] = true
			}
		}
		farmland := append([]domain.Cell(nil), ordered...)
		center := domain.Cell{X: x + 4, Z: z + 4}
		sort.Slice(farmland, func(i, j int) bool {
			a, b := squaredDistance(farmland[i], center), squaredDistance(farmland[j], center)
			if a != b {
				return a < b
			}
			return cellLess(farmland[i], farmland[j])
		})
		layout := StarterLayout{Room: Rectangle{x, z, 9, 9}, Storage: Rectangle{x + 3, z + 5, 3, 3}, Score: site.score, TargetCells: targetFact}
		chosen := map[domain.Cell]bool{}
		for _, size := range []int32{4, 3, 2, 1} {
			if !fk || len(chosen) >= target || len(layout.Farms) >= 32 {
				break
			}
			for _, anchor := range farmland {
				patch := Rectangle{anchor.X, anchor.Z, size, size}
				points := rectCells(patch)
				legal := true
				for _, p := range points {
					c := cells[p]
					f, k := c.Fertility.Value()
					if reserved[p] || chosen[p] || !free(p) || !positive(measured(c.Roofed, func(v bool) bool { return !v })) || !k || f < fertility {
						legal = false
						break
					}
				}
				if !legal {
					continue
				}
				layout.Farms = append(layout.Farms, patch)
				for _, p := range points {
					chosen[p] = true
				}
				if len(chosen) >= target || len(layout.Farms) >= 32 {
					break
				}
			}
		}
		layout.SelectedCells = len(chosen)
		layout.Score += int64(max(0, target-len(chosen))) * 2
		layouts = append(layouts, layout)
	}
	sort.Slice(layouts, func(i, j int) bool {
		a, b := layouts[i], layouts[j]
		if a.Score != b.Score {
			return a.Score < b.Score
		}
		return cellLess(domain.Cell{X: a.Room.X, Z: a.Room.Z}, domain.Cell{X: b.Room.X, Z: b.Room.Z})
	})
	if len(layouts) > 12 {
		layouts = layouts[:12]
	}
	return layouts, nil
}
