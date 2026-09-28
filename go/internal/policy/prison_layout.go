package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Prison layout (#1081, epic #845 prison break): mini-turrets stand just
// outside the prison doors, and no weapon stockpile stands near a prison,
// so an escapee finds neither an open exit nor a weapon.

const (
	// TierPrisonTurretPrefix names the prison turret sections under the
	// perimeter's prefix, each with its conduit run, as the pumps do.
	TierPrisonTurretPrefix = TierPerimeterPrefix + "prison-"
	// prisonTurretMax bounds the turrets for all prisons together: the wiki
	// asks for one to three.
	prisonTurretMax = 3
	// prisonWeaponClearance is the Chebyshev distance a weapon stockpile
	// keeps from every prison cell (walls and door included).
	prisonWeaponClearance = 6
)

// prisonDoorOutside is the cell just outside a room's door and the
// direction out; false when the door does not border the interior.
func prisonDoorOutside(r LayoutRoom) (domain.Cell, domain.Cell, bool) {
	for _, d := range directions {
		if contains(r.Interior, addCell(r.Door, d)) {
			out := domain.Cell{X: -d.X, Z: -d.Z}
			return addCell(r.Door, out), out, true
		}
	}
	return domain.Cell{}, domain.Cell{}, false
}

// PrisonTurretSites are the mini-turret cells outside the plan's prison
// doors: one each side of the cell in front of a door, never on that cell
// (the walkway) nor on any room's footprint or reservation, at most three.
func PrisonTurretSites(plan LayoutPlan) []domain.Cell {
	blocked := map[domain.Cell]bool{}
	for _, r := range plan.Rooms {
		for _, c := range rectCells(pad(r.Interior, 1)) {
			blocked[c] = true
		}
	}
	for _, r := range plan.Reservations {
		for _, c := range rectCells(r.Area) {
			blocked[c] = true
		}
	}
	var out []domain.Cell
	for _, r := range plan.Rooms {
		if r.Role != ModulePrison {
			continue
		}
		front, dir, ok := prisonDoorOutside(r)
		if !ok {
			continue
		}
		blocked[front] = true
		p := perpendicular(dir)
		for _, c := range []domain.Cell{addCell(front, p), addCell(front, scale(p, -1))} {
			if len(out) == prisonTurretMax {
				return out
			}
			if !blocked[c] {
				out = append(out, c)
				blocked[c] = true
			}
		}
	}
	return out
}

// PerimeterPrisonTurrets is one section per prison turret site: the turret
// and the conduit run that brings a transmitter within connector reach,
// chained over the ring's interior clear of the killbox and the turret
// sites. Nothing without a transmitter: a dark turret guards nothing.
func PerimeterPrisonTurrets(plan LayoutPlan, turret, stuff, conduit string, transmitters []domain.Cell) ([]PerimeterSection, error) {
	sites := PrisonTurretSites(plan)
	if len(sites) == 0 || len(transmitters) == 0 {
		return nil, nil
	}
	var ring, killbox Rectangle
	for _, r := range plan.Reservations {
		switch r.Kind {
		case ReservePerimeter:
			ring = unionRect(ring, r.Area)
		case ReserveKillbox:
			killbox = r.Area
		}
	}
	allowed, carry := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, c := range rectCells(pad(ring, -perimeterThick)) {
		allowed[c] = !contains(killbox, c)
	}
	for _, c := range sites {
		allowed[c] = false
	}
	for _, c := range transmitters {
		allowed[c], carry[c] = true, true
	}
	var out []PerimeterSection
	for _, site := range sites {
		chain, ok := defenseSite{}.conduitChain(site, carry, allowed)
		if !ok {
			continue
		}
		s := PerimeterSection{Name: DefenseTierName(fmt.Sprintf("%s%02d", TierPrisonTurretPrefix, len(out)))}
		b, err := domain.NewBuilding(turret, site, domain.North, stuff)
		if err != nil {
			return nil, err
		}
		s.Buildings = append(s.Buildings, b)
		for _, c := range chain {
			if b, err = domain.NewBuilding(conduit, c, domain.North, ""); err != nil {
				return nil, err
			}
			s.Buildings = append(s.Buildings, b)
			carry[c] = true
		}
		out = append(out, s)
	}
	return out, nil
}

// PrisonCells are every cell of the plan's prisons, walls included.
func PrisonCells(plan LayoutPlan) []domain.Cell {
	var out []domain.Cell
	for _, r := range plan.Rooms {
		if r.Role == ModulePrison {
			out = append(out, rectCells(pad(r.Interior, 1))...)
		}
	}
	return out
}

// nearPrison reports whether any cell lies within the weapon clearance of
// a prison cell.
func nearPrison(cells, prisons []domain.Cell) bool {
	for _, c := range cells {
		for _, p := range prisons {
			if chebyshev(c, p) <= prisonWeaponClearance {
				return true
			}
		}
	}
	return false
}
