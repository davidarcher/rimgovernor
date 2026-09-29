package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TierIEDs is the IED traps on the approach ahead of the killbox corridor
// (#1209): the trap lane extended outward from Entry toward the map edge.
const TierIEDs DefenseTierName = "ieds"

const (
	// defenseIEDReach is how far past Entry the approach is searched.
	defenseIEDReach = 16
	// defenseIEDSpacing keeps IEDs two cells apart along the approach:
	// PlaceWorker_NeverAdjacentTrap refuses a trap beside another.
	defenseIEDSpacing = 2
	defenseIEDMax     = 4
)

// DefenseIED is one IED definition the research and content gates allow,
// with its native explosive radius. Incendiary IEDs also keep that radius
// from every player building.
type DefenseIED struct {
	Definition string
	Radius     float64
	Incendiary bool
}

// iedTier walks the approach outward from Entry along the trap lane and
// places an IED on each free cell, every defenseIEDSpacing cells, whose
// blast cannot reach a door, the safe lane, a colonist route or flammable
// storage; an incendiary IED also stays clear of every player building,
// observed or planned. The first listed IED that fits a cell takes it. An
// unknown storage census, no IED definition or no qualifying cell leaves the
// tier empty.
func (s defenseSite) iedTier(l DefenseLayout, costs corridorCosts) DefenseTier {
	tier := DefenseTier{Name: TierIEDs}
	storage, known := s.r.FlammableStorage.Value()
	if len(s.r.IEDs) == 0 || !known {
		return tier
	}
	keepOff := append(append([]domain.Cell{}, l.SafeLane...), storage...)
	keepOff = append(keepOff, s.r.Entrances...)
	for c, row := range s.cells {
		if positive(row.Door) {
			keepOff = append(keepOff, c)
		}
	}
	for _, start := range append([]domain.Cell{s.r.Home}, s.r.Entrances...) {
		route, _ := s.colonistRoute(costs, start)
		for c := range route {
			keepOff = append(keepOff, c)
		}
	}
	buildings := append([]domain.Cell{}, keepOff...)
	for c, row := range s.cells {
		if positive(row.PlayerOwned) {
			buildings = append(buildings, c)
		}
	}
	taken := map[domain.Cell]bool{}
	for _, t := range l.Tiers {
		for _, c := range t.Reserved {
			taken[c] = true
		}
		for _, b := range t.Buildings {
			taken[b.Cell()] = true
			buildings = append(buildings, b.Cell())
		}
	}
	for _, f := range l.Firing {
		taken[f.Cell], taken[f.Cover], taken[f.Retreat] = true, true, true
	}
	back := scale(directionOf(l.Toward), -1)
	for k := int32(defenseIEDSpacing); k <= defenseIEDReach && len(tier.Buildings) < defenseIEDMax; k += defenseIEDSpacing {
		c := addCell(l.Entry, scale(back, k))
		if !s.inRegion(c) || taken[c] || !s.free(c) || !s.passable(c) {
			continue
		}
		for _, ied := range s.r.IEDs {
			clear := keepOff
			if ied.Incendiary {
				clear = buildings
			}
			if !outsideBlast(c, ied.Radius, clear) {
				continue
			}
			b, err := domain.NewBuilding(ied.Definition, c, domain.North, "")
			if err != nil {
				continue
			}
			tier.Buildings = append(tier.Buildings, b)
			tier.Reserved = append(tier.Reserved, c)
			break
		}
	}
	tier.Costs = tierCosts(s.r.UnitCosts, tier.Buildings)
	return tier
}

// outsideBlast reports every keepOff cell strictly farther than radius from
// at: the game's explosion reaches cells within the radius. A non-positive
// or non-finite radius is not a known blast and fails.
func outsideBlast(at domain.Cell, radius float64, keepOff []domain.Cell) bool {
	if !(radius > 0) || math.IsInf(radius, 0) {
		return false
	}
	for _, c := range keepOff {
		if float64(squaredDistance(at, c)) <= radius*radius {
			return false
		}
	}
	return true
}
