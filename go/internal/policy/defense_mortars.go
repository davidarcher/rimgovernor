package policy

import (
	"errors"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DefenseMortarRequest is the mortar tier's input (#1206). Available is the
// native planning definition's availability re-checked against finished
// research (like the turret gate), Stock the colony's stock census and Max
// the budget MortarBudget buys from the observed raid points. An unknown
// gate places no mortar; Definition empty asks for no mortar tier.
type DefenseMortarRequest struct {
	Definition, Stuff string
	Available         domain.Fact[bool]
	Stock             domain.Fact[map[Resource]int64]
	Max               int
}

const (
	TierMortars DefenseTierName = "mortars"
	// mortarBehindMin and mortarBehindMax bound how far behind the
	// shooters' row a mortar stands: far enough that a destroyed mortar's
	// blast misses the line, near enough to stay on defended ground.
	mortarBehindMin = 4
	mortarBehindMax = 10
	// mortarHighPoints is the raid-point reading at which a second mortar
	// is budgeted; the first comes with the turret budget's top band.
	mortarHighPoints = armoryFabricationPoints
	mortarHardCap    = 8
)

// MortarBudget is the most mortars the layout proposes: none while the raid
// points are unknown or below the turret budget's top band (sieges are not
// yet a threat worth the shells), one below mortarHighPoints, two above.
func MortarBudget(raidPoints domain.Fact[float64]) int {
	points, known := raidPoints.Value()
	switch {
	case !known || math.IsNaN(points) || points < turretHighPoints:
		return 0
	case points < mortarHighPoints:
		return 1
	}
	return 2
}

// MortarGatesOpen reports whether the observed gates (available definition,
// budget, stock for one mortar) allow at least one mortar before any site
// is read.
func (r DefenseRequest) MortarGatesOpen() bool { return mortarGate(r, 1) > 0 }

// mortarGate is how many of the sites the availability, budget and stock
// gates allow.
func mortarGate(r DefenseRequest, sites int) int {
	q := r.Mortar
	available, ak := q.Available.Value()
	if q.Definition == "" || !ak || !available || q.Max <= 0 {
		return 0
	}
	n := min(q.Max, sites)
	for ; n > 0; n-- {
		var mortars []domain.Building
		for i := 0; i < n; i++ {
			b, err := domain.NewBuilding(q.Definition, domain.Cell{}, domain.North, q.Stuff)
			if err != nil {
				return 0
			}
			mortars = append(mortars, b)
		}
		if stockCovers(r.UnitCosts, q.Stock, mortars) {
			break
		}
	}
	return n
}

// stockCovers reports whether the stock covers the buildings' summed unit
// costs; unknown costs or stock never cover anything.
func stockCovers(unitCosts map[string][]Amount, stock domain.Fact[map[Resource]int64], buildings []domain.Building) bool {
	costs, ck := tierCosts(unitCosts, buildings).Value()
	have, sk := stock.Value()
	if !ck || !sk {
		return false
	}
	for _, a := range costs {
		if have[a.Resource] < a.Count {
			return false
		}
	}
	return true
}

// DefenseMortars proposes the mortar tier for a layout: mortars behind the
// shooters' row (away from the corridor entry), on free ground known to be
// unroofed (a mortar cannot fire through a roof), off the lanes and every
// reserved cell, spaced from the shooters, the turrets and each other so a
// destroyed one takes nothing with it. It needs no line of sight: a mortar
// lobs over walls at a siege far outside the killbox.
func DefenseMortars(r DefenseRequest, g DefenseGeometry) (DefenseTier, error) {
	s, err := newDefenseSite(r)
	if err != nil {
		return DefenseTier{}, err
	}
	tier := DefenseTier{Name: TierMortars, Costs: domain.Known([]Amount{})}
	q := r.Mortar
	if q.Definition == "" {
		return tier, nil
	}
	if q.Max > mortarHardCap {
		return DefenseTier{}, errors.New("invalid mortar request")
	}
	if len(g.Firing) == 0 {
		return tier, nil
	}
	d := directionOf(g.Toward)
	p := perpendicular(d)
	taken := map[domain.Cell]bool{}
	for _, cells := range [][]domain.Cell{g.Lanes, g.Reserved, g.Turrets} {
		for _, c := range cells {
			taken[c] = true
		}
	}
	spaced := append(append([]domain.Cell{}, g.Firing...), g.Turrets...)
	origin := g.Firing[0]
	low, high := int32(0), int32(0)
	for _, f := range g.Firing {
		o := (f.X-origin.X)*p.X + (f.Z-origin.Z)*p.Z
		low, high = min(low, o), max(high, o)
	}
	mid := (low + high) / 2
	var sites []domain.Cell
	for behind := int32(mortarBehindMin); behind <= mortarBehindMax && len(sites) < mortarHardCap; behind++ {
		row := addCell(origin, scale(d, behind))
		// The centre of the firing span first, then outward.
		for w := int32(0); w <= defenseMaxWidth; w++ {
			offsets := []int32{mid + w, mid - w}
			if w == 0 {
				offsets = offsets[:1]
			}
			for _, o := range offsets {
				c := addCell(row, scale(p, o))
				cell, ok := s.cells[c]
				if !ok || taken[c] || !s.free(c) || !negative(cell.Roofed) {
					continue
				}
				if tooClose(c, spaced) || tooClose(c, sites) {
					continue
				}
				sites = append(sites, c)
			}
		}
	}
	n := mortarGate(r, len(sites))
	for _, c := range sites[:n] {
		b, err := domain.NewBuilding(q.Definition, c, domain.North, q.Stuff)
		if err != nil {
			return DefenseTier{}, err
		}
		tier.Buildings = append(tier.Buildings, b)
		tier.Reserved = append(tier.Reserved, c)
	}
	tier.Costs = tierCosts(r.UnitCosts, tier.Buildings)
	return tier, nil
}

// tooClose reports a cell within turret spacing of any of the cells.
func tooClose(c domain.Cell, cells []domain.Cell) bool {
	for _, o := range cells {
		if chebyshev(c, o) < turretSpacing {
			return true
		}
	}
	return false
}
