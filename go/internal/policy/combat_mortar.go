package policy

import (
	"math"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// OrderMortar sends a drafted pawn to man a colony mortar that fires at a
// cell (#931).
const OrderMortar CombatOrderKind = "mortar"

// ReasonCounterBattery is a mortar order (#931).
const ReasonCounterBattery CombatOrderReason = "counter_battery"

// CombatMortar is an unroofed player mortar the fight may crew (#931),
// with its verb's range in cells.
type CombatMortar struct {
	ID                 string
	Cell               domain.Cell
	MinRange, MaxRange float64
	// Loaded is the loaded shell's def (#1051), "" when unloaded.
	Loaded string `json:",omitempty"`
}

// The vanilla mortar shells the counter-battery fires (#1051).
const (
	ShellHE         = "Shell_HighExplosive"
	ShellIncendiary = "Shell_Incendiary"
	ShellEMP        = "Shell_EMP"
)

// campClumpRadius is how far, in cells, besiegers count into one camp
// clump around a besieger the mortar aims at.
const campClumpRadius = 5.0

// centipedeSafeRadius: a centipede with a colonist this close has arrived,
// and a mortar's scatter would land on our own.
const centipedeSafeRadius = 10.0

// HostileStructure is a standing hostile building in the census (#930,
// #931): a crashed ship part, a mech-cluster piece, a siege or mech
// mortar, a hive. Def is its ThingDef; Cell one occupied cell; Mortar the
// native def fact (#1148), a turret whose verb fires mortar shells (a
// siege's or a mech cluster's).
type HostileStructure struct {
	ID     domain.PawnID
	Def    string
	Cell   domain.Cell
	Mortar bool
}

func (s HostileStructure) hive() bool { return s.Def == "Hive" }

// fromRange drops a melee target on a hostile structure other than a hive
// (#930): a crashed ship part and its cluster buildings are destroyed
// from range, so the mechs they wake walk into the line instead of
// catching a brawler at the part. Gunners keep theirs.
func fromRange(view CombatView, m *CombatMemory) {
	far := map[domain.PawnID]bool{}
	for _, s := range view.Structures {
		far[s.ID] = !s.hive()
	}
	for i := range m.Roles {
		if !m.Roles[i].Ranged && far[m.Roles[i].Target] {
			m.Roles[i].Target = ""
		}
	}
}

// counterBattery crews each colony mortar with a target in its range
// (#931, #930, #1051): EMP on the nearest enemy mortar (a siege's or a
// mech cluster's), else HE on the siege camp's densest clump (incendiary
// from a second mortar on the same camp, to tie them up firefighting),
// else HE on the nearest approaching centipede, else HE on the nearest
// other non-hive structure. The crew is
// the pawn that manned it at the last stop while still orderable, else
// the nearest orderable free pawn; its role keeps its cell and target
// for when the mortar has nothing to shoot.
func counterBattery(view CombatView, m *CombatMemory) {
	crewed := map[domain.Cell]domain.PawnID{}
	for i := range m.Roles {
		if r := &m.Roles[i]; r.Mortar != nil {
			crewed[*r.Mortar] = r.Pawn
			r.Mortar, r.Aim = nil, nil
		}
	}
	orderable := map[domain.PawnID]bool{}
	for _, id := range view.Orderable {
		orderable[id] = true
	}
	live := view.live()
	cells := map[domain.PawnID]domain.Cell{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			cells[p.ID] = c
		}
	}
	busy := map[domain.PawnID]bool{}
	mortars := slices.Clone(view.Mortars)
	sort.Slice(mortars, func(i, j int) bool { return mortars[i].ID < mortars[j].ID })
	shelled := map[domain.Cell]bool{} // camp aims HE already falls on
	for _, mortar := range mortars {
		aim, shell, ok := mortarAim(view, mortar)
		if !ok {
			continue
		}
		if shell == ShellHE && campAim(view, aim) {
			if shelled[aim] {
				shell = ShellIncendiary
			}
		}
		free := func(id domain.PawnID) bool {
			return orderable[id] && live[id] && !busy[id] && !m.Rescue.carrying(id)
		}
		crew := crewed[mortar.Cell]
		if !free(crew) {
			crew = ""
			best := math.MaxFloat64
			for _, id := range view.Orderable {
				c, known := cells[id]
				if !free(id) || !known {
					continue
				}
				if d := dist(c, mortar.Cell); d < best {
					crew, best = id, d
				}
			}
		}
		if crew == "" {
			continue
		}
		busy[crew] = true
		if shell == ShellHE && campAim(view, aim) {
			shelled[aim] = true
		}
		if slices.Contains(m.NoShells, shell) {
			// Refused for want of it: fire what is loaded (#1051).
			shell = mortar.Loaded
		}
		cell, target := mortar.Cell, aim
		i := slices.IndexFunc(m.Roles, func(r CombatRole) bool { return r.Pawn == crew })
		if i < 0 {
			m.Roles = append(m.Roles, CombatRole{Pawn: crew})
			i = len(m.Roles) - 1
		}
		m.Roles[i].Mortar, m.Roles[i].Aim, m.Roles[i].Shell = &cell, &target, shell
	}
	m.Roles = sortRoles(m.Roles)
}

// mortarAim is the cell mortar fires at and the shell: EMP on the nearest
// enemy mortar in its range, else HE on the camp clump, else HE on the
// nearest centipede, else HE on the nearest other non-hive structure.
func mortarAim(view CombatView, mortar CombatMortar) (domain.Cell, string, bool) {
	inRange := func(c domain.Cell) bool {
		d := dist(c, mortar.Cell)
		return d >= mortar.MinRange && d <= mortar.MaxRange
	}
	nearest := func(cells []domain.Cell) (domain.Cell, bool) {
		var best domain.Cell
		found := false
		for _, c := range cells {
			if inRange(c) && (!found || dist(c, mortar.Cell) < dist(best, mortar.Cell)) {
				best, found = c, true
			}
		}
		return best, found
	}
	var enemy, other []domain.Cell
	for _, s := range view.Structures {
		switch {
		case s.Mortar:
			enemy = append(enemy, s.Cell)
		case !s.hive():
			other = append(other, s.Cell)
		}
	}
	if c, ok := nearest(enemy); ok {
		return c, ShellEMP, true
	}
	if c, ok := campClump(view, inRange); ok {
		return c, ShellHE, true
	}
	if c, ok := nearest(approachingCentipedes(view)); ok {
		return c, ShellHE, true
	}
	if c, ok := nearest(other); ok {
		return c, ShellHE, true
	}
	return domain.Cell{}, "", false
}

// besiegerCells are the cells of the live besiegers (#776).
func besiegerCells(view CombatView) []domain.Cell {
	besiegers := liveBesiegers(view)
	var out []domain.Cell
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			if _, besieger := besiegers[p.ID]; besieger {
				out = append(out, c)
			}
		}
	}
	return out
}

// campClump is the besieger cell in range with the most besiegers within
// campClumpRadius of it (first in id order on a tie).
func campClump(view CombatView, inRange func(domain.Cell) bool) (domain.Cell, bool) {
	cells := besiegerCells(view)
	var best domain.Cell
	most := 0
	for _, c := range cells {
		if !inRange(c) {
			continue
		}
		n := 0
		for _, o := range cells {
			if dist(c, o) <= campClumpRadius {
				n++
			}
		}
		if n > most {
			best, most = c, n
		}
	}
	return best, most > 0
}

// campAim reports an aim on a besieger's cell.
func campAim(view CombatView, aim domain.Cell) bool {
	return slices.Contains(besiegerCells(view), aim)
}

// approachingCentipedes are the live centipedes' cells (#1051): shelled
// while they walk in, before they reach the line inside the mortar's
// minimum range.
func approachingCentipedes(view CombatView) []domain.Cell {
	hostile := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		hostile[domain.PawnID(t.ID)] = !t.Building && !positive(t.Dead) && !positive(t.Downed)
	}
	var ours, out []domain.Cell
	for _, d := range view.Defenders {
		for _, p := range view.Pawns {
			if c, ok := p.Cell.Value(); ok && p.ID == d.ID {
				ours = append(ours, c)
			}
		}
	}
	for _, p := range view.Pawns {
		c, ok := p.Cell.Value()
		if !ok || !hostile[p.ID] || p.Dead || p.Downed || !strings.HasPrefix(p.Kind, "Mech_Centipede") {
			continue
		}
		if !slices.ContainsFunc(ours, func(o domain.Cell) bool { return dist(o, c) <= centipedeSafeRadius }) {
			out = append(out, c)
		}
	}
	return out
}

func dist(a, b domain.Cell) float64 {
	return math.Hypot(float64(a.X-b.X), float64(a.Z-b.Z))
}
