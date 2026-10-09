package policy

import (
	"math"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// OrderManMortar sends a drafted pawn to man the colony mortar on Cell;
// OrderMortarFire, which names no pawn, aims that mortar at Aim with
// Shell.
const (
	OrderManMortar  CombatOrderKind = "man_mortar"
	OrderMortarFire CombatOrderKind = "mortar_fire"
)

// ReasonCounterBattery is a mortar order.
const ReasonCounterBattery CombatOrderReason = "counter_battery"

// CombatMortar is an unroofed player mortar the fight may crew,
// with its verb's range in cells.
type CombatMortar struct {
	ID                 string
	Cell               domain.Cell
	MinRange, MaxRange float64
	// Loaded is the loaded shell's def, "" when unloaded.
	Loaded string `json:",omitempty"`
}

// ShellKind is what a mortar shell does when it lands, the kinds the
// counter-battery fires: blast, burn or stun.
type ShellKind int

const (
	ShellHE ShellKind = iota + 1
	ShellIncendiary
	ShellEMP
)

// MortarShells are the shell defs of the load that do each kind of damage,
// classified from their projectile's damage def rows
// (bridge.DefinitionCatalog.MortarShells says how). A kind the load has no
// shell for, or states ambiguously, is "": the counter-battery fires what
// the mortar holds instead and the armory stocks none.
type MortarShells struct {
	HE, Incendiary, EMP string
}

// Def is the shell def of kind, "" when the load has none.
func (s MortarShells) Def(kind ShellKind) string {
	switch kind {
	case ShellHE:
		return s.HE
	case ShellIncendiary:
		return s.Incendiary
	case ShellEMP:
		return s.EMP
	}
	return ""
}

// campClumpRadius is how far, in cells, besiegers count into one camp
// clump around a besieger the mortar aims at.
const campClumpRadius = 5.0

// MortarSafeRadius is the friendly danger radius: a target
// with a colonist this close has arrived, and a mortar's scatter would
// land on our own.
const MortarSafeRadius = 10.0

// HostileStructure is a standing hostile building in the census: a crashed ship part, a mech-cluster piece, a siege or mech
// mortar, a hive. Def is its ThingDef; Cell one occupied cell; Mortar the
// native def fact, a turret whose verb fires mortar shells (a
// siege's or a mech cluster's).
type HostileStructure struct {
	ID     domain.PawnID
	Def    string
	Cell   domain.Cell
	Mortar bool
}

func (s HostileStructure) hive() bool { return s.Def == "Hive" }

// fromRange drops a melee target on a hostile structure other than a hive:
// a crashed ship part and its cluster buildings are destroyed
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

// counterBattery crews each colony mortar with a target in its range:
// EMP on the nearest enemy mortar (a siege's or a
// mech cluster's), else HE on the siege camp's densest clump (incendiary
// from a second mortar on the same camp, to tie them up firefighting),
// else HE on the nearest approaching centipede, else HE on the nearest
// other non-hive structure. The crew is
// the pawn that manned it at the last stop while still orderable, else
// the nearest orderable free pawn; its role keeps its cell and target
// for when the mortar has nothing to shoot. It returns the mortars crewed
// at the last stop that now have nothing to aim at, each with its
// last crew, so the fight clears their forced target and releases the crew.
func counterBattery(view CombatView, prev []CombatRole, m *CombatMemory) []mortarStand {
	crewed := map[domain.Cell]domain.PawnID{}
	for _, r := range prev {
		// The last stop's crews, which a re-formation this stop (a siege
		// breaking camp) has already dropped from m.
		if r.Mortar != nil {
			crewed[*r.Mortar] = r.Pawn
		}
	}
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
	var stood []mortarStand
	for _, mortar := range mortars {
		aim, kind, ok := mortarAim(view, mortar)
		if !ok {
			if crew, was := crewed[mortar.Cell]; was {
				stood = append(stood, mortarStand{Cell: mortar.Cell, Crew: crew})
			}
			continue
		}
		if kind == ShellHE && campAim(view, aim) {
			if shelled[aim] {
				kind = ShellIncendiary
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
		if kind == ShellHE && campAim(view, aim) {
			shelled[aim] = true
		}
		// A kind the load has no shell for, or one refused for want of it,
		// fires what is loaded.
		shell := view.Shells.Def(kind)
		if shell == "" || slices.Contains(m.NoShells, shell) {
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
	return stood
}

// mortarStand is a mortar stood down: its aim is gone (the siege
// broke camp, the target died), so its forced target is cleared and Crew,
// its last crew, is released to its role.
type mortarStand struct {
	Cell domain.Cell
	Crew domain.PawnID
}

// standDown is the orders of the mortars stood down: a pawnless
// mortar_fire with no target clears each forced target, and a crew still
// manning it with no other order this stop is stopped, ending ManTurret;
// its role (the Mortar fields cleared by counterBattery) orders it on.
func standDown(stood []mortarStand, orders []CombatOrder, orderable map[domain.PawnID]bool, state map[domain.PawnID]CombatPawnState) []CombatOrder {
	for _, s := range stood {
		orders = append(orders, CombatOrder{Kind: OrderMortarFire, Cell: s.Cell, Clear: true, Reason: ReasonCounterBattery})
		ordered := slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Pawn == s.Crew })
		if orderable[s.Crew] && !ordered && state[s.Crew].Job == "ManTurret" {
			orders = append(orders, CombatOrder{Pawn: s.Crew, Kind: OrderStop, Reason: ReasonCounterBattery})
		}
	}
	return orders
}

// mortarAim is the cell mortar fires at and the shell: EMP on the nearest
// enemy mortar in its range, else HE on a psychic ritual's caster,
// else HE on the camp clump, else HE on the
// nearest centipede, else HE on the nearest other non-hive structure.
func mortarAim(view CombatView, mortar CombatMortar) (domain.Cell, ShellKind, bool) {
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
	if c, ok := nearest(ritualCasterCells(view)); ok {
		return c, ShellHE, true
	}
	if c, ok := campClump(view, inRange); ok {
		if mechAt(view, c) {
			return c, ShellEMP, true
		}
		return c, ShellHE, true
	}
	if c, ok := nearest(approachingCentipedes(view)); ok {
		return c, ShellHE, true
	}
	if c, ok := nearest(other); ok {
		return c, ShellHE, true
	}
	return domain.Cell{}, 0, false
}

// besiegerCells are the cells of the live besiegers camped at the siege:
// a siege still travelling in is a moving raid the mortar
// leaves alone, and one that breaks camp to assault drops out, so the
// crew stops. A besieger within MortarSafeRadius of a colonist is never
// aimed at.
func besiegerCells(view CombatView) []domain.Cell {
	besiegers := liveBesiegers(view)
	ours := colonistCells(view)
	var out []domain.Cell
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			if toil, besieger := besiegers[p.ID]; besieger && toil == siegeCampToil && !nearAny(ours, c) {
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

// approachingCentipedes are the live centipedes' cells: shelled
// while they walk in, before they reach the line inside the mortar's
// minimum range.
func approachingCentipedes(view CombatView) []domain.Cell {
	hostile := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		hostile[domain.PawnID(t.ID)] = !t.Building && !positive(t.Dead) && !positive(t.Downed)
	}
	ours := colonistCells(view)
	var out []domain.Cell
	for _, p := range view.Pawns {
		c, ok := p.Cell.Value()
		if !ok || !hostile[p.ID] || p.Dead || p.Downed || !strings.HasPrefix(p.Kind, "Mech_Centipede") {
			continue
		}
		if !nearAny(ours, c) {
			out = append(out, c)
		}
	}
	return out
}

// colonistCells are the defenders' known cells.
func colonistCells(view CombatView) []domain.Cell {
	var ours []domain.Cell
	for _, d := range view.Defenders {
		for _, p := range view.Pawns {
			if c, ok := p.Cell.Value(); ok && p.ID == d.ID {
				ours = append(ours, c)
			}
		}
	}
	return ours
}

// nearAny reports a cell within MortarSafeRadius of one of ours.
func nearAny(ours []domain.Cell, c domain.Cell) bool {
	return slices.ContainsFunc(ours, func(o domain.Cell) bool { return dist(o, c) <= MortarSafeRadius })
}

// mechAt reports a mechanoid standing on c: EMP, not HE, on a
// camp of mechs.
func mechAt(view CombatView, c domain.Cell) bool {
	return slices.ContainsFunc(view.Pawns, func(p CombatPawnState) bool {
		at, ok := p.Cell.Value()
		return ok && at == c && p.Mech
	})
}

func dist(a, b domain.Cell) float64 {
	return math.Hypot(float64(a.X-b.X), float64(a.Z-b.Z))
}
