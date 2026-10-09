package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DefenseMortarRequest is the mortar tier's input. Available is the
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
)

// MortarBudget is the number of mortars the observed raid points demand:
// none while they are unknown or below turretHighPoints (sieges are not yet
// a threat worth the shells), then one, plus one more for every
// mortarHighPoints-turretHighPoints above that. Stock and the sites behind
// the line bound what is placed.
func MortarBudget(raidPoints domain.Fact[float64]) int {
	points, known := raidPoints.Value()
	if !known || math.IsNaN(points) || math.IsInf(points, 0) || points < turretHighPoints {
		return 0
	}
	return 1 + int((points-turretHighPoints)/(mortarHighPoints-turretHighPoints))
}

// MortarGatesOpen reports whether the observed gates (available definition,
// budget, stock for one mortar) allow at least one mortar before any site
// is read.
func (r DefenseRequest) MortarGatesOpen() bool { return mortarGate(r, 1) > 0 }

// mortarGate is how many of the sites the availability, budget and stock
// gates allow.
func mortarGate(r DefenseRequest, sites int) int {
	n, _ := mortarLimit(r, sites)
	return n
}

// mortarLimit is mortarGate with the gate that cut the count below the
// sites ("" when nothing did): the threat demand (Max), then stock.
func mortarLimit(r DefenseRequest, sites int) (int, string) {
	q := r.Mortar
	available, ak := q.Available.Value()
	if q.Definition == "" || !ak || !available {
		return 0, GateMortarUnavailable
	}
	if q.Max <= 0 {
		return 0, GateMortarThreat
	}
	n, gate := q.Max, GateMortarThreat
	if n >= sites {
		n, gate = sites, ""
	}
	for ; n > 0; n-- {
		var mortars []domain.Building
		for i := 0; i < n; i++ {
			b, err := domain.NewBuilding(q.Definition, domain.Cell{}, domain.North, q.Stuff)
			if err != nil {
				return 0, GateMortarUnavailable
			}
			mortars = append(mortars, b)
		}
		if stockCovers(r.UnitCosts, q.Stock, mortars) {
			break
		}
		gate = GateMortarStock
	}
	return n, gate
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
	for behind := int32(mortarBehindMin); behind <= mortarBehindMax; behind++ {
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
	n, gate := mortarLimit(r, len(sites))
	if len(sites) > 0 {
		tier.Gated = gate
	}
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
