package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Flanking: a hold with at least flankMinDefenders defenders and a
// layout choke detaches flankSize gunners to cells beside the approach,
// just inside the choke, square to the main line (the L). They hold fire
// while the raid walks in, and open fire once the lead hostile has passed
// them, so the raid takes fire from the line and from its side.

const (
	// flankMinDefenders is the hold's size below which no one is detached.
	flankMinDefenders = 5
	// flankSize is the detachment's gunner count.
	flankSize = 2
	// flankLateral and flankDepth place the ask's anchor: flankLateral
	// cells to the side of the choke, flankDepth cells toward the colony.
	flankLateral = 4
	flankDepth   = 1
)

// CombatFlank is the hold's flanking detachment. An empty Pawns
// means the game proposed no cells: the fight does not ask again until
// it re-forms.
type CombatFlank struct {
	Pawns []domain.PawnID `json:",omitempty"`
	Cells []domain.Cell   `json:",omitempty"`
	// Open is set once the lead hostile passed the detachment.
	Open bool `json:",omitempty"`
	// Held are the flankers given hold-fire while waiting.
	Held []domain.PawnID `json:",omitempty"`
}

func (f *CombatFlank) clone() *CombatFlank {
	if f == nil {
		return nil
	}
	c := *f
	c.Pawns, c.Cells, c.Held = slices.Clone(f.Pawns), slices.Clone(f.Cells), slices.Clone(f.Held)
	return &c
}

// waiting reports pawn a flanker still holding fire.
func (f *CombatFlank) waiting(pawn domain.PawnID) bool {
	return f != nil && !f.Open && slices.Contains(f.Pawns, pawn)
}

// flankGunners are the hold's gunners the detachment takes: the last
// flankSize ranged line roles in pawn order, or nil when the hold is under
// flankMinDefenders or would keep fewer than flankSize on the line.
func flankGunners(roles []CombatRole) []domain.PawnID {
	if len(roles) < flankMinDefenders {
		return nil
	}
	var gunners []domain.PawnID
	for _, r := range roles {
		if r.Ranged && r.Duty == "" && !r.Retreat && r.Cell != nil && r.Mortar == nil {
			gunners = append(gunners, r.Pawn)
		}
	}
	if len(gunners) < 2*flankSize {
		return nil
	}
	return gunners[len(gunners)-flankSize:]
}

// flankAnchor is the firing_cells ask's from cell beside the approach, and
// the choke the flankers need a line to.
func flankAnchor(view CombatView) (domain.Cell, domain.Cell, bool) {
	layout, ok := view.Layout.Value()
	if !ok {
		return domain.Cell{}, domain.Cell{}, false
	}
	choke, ok := layout.Choke.Value()
	v, vok := towardVector(layout.Toward)
	if !ok || !vok {
		return domain.Cell{}, domain.Cell{}, false
	}
	side := domain.Cell{X: v.Z, Z: -v.X}
	for _, s := range []int32{1, -1} {
		a := domain.Cell{X: choke.X + s*flankLateral*side.X + flankDepth*v.X, Z: choke.Z + s*flankLateral*side.Z + flankDepth*v.Z}
		if a.X >= 0 && a.Z >= 0 {
			return a, choke, true
		}
	}
	return domain.Cell{}, domain.Cell{}, false
}

// flankAsk is the detachment's firing_cells ask: a hold with enough
// gunners and a choke, not flanking yet. It names the shooters and the
// top hostiles too, so the stop's attacks still get their lines.
func flankAsk(view CombatView, m CombatMemory) *GeometryRequest {
	if m.Tactic != TacticHold || m.Flank != nil || m.MechLure || len(flankGunners(m.Roles)) == 0 {
		return nil
	}
	from, choke, ok := flankAnchor(view)
	if !ok {
		return nil
	}
	return firingCellsAsk(view, from, choke)
}

// firingCellsAsk is a firing_cells ask for cells near from with a line to
// target, naming the shooters and the top hostiles too, so the stop's
// attacks still get their lines.
func firingCellsAsk(view CombatView, from, target domain.Cell) *GeometryRequest {
	ask := &GeometryRequest{Propose: RoleFiringCells, From: from, Targets: []domain.Cell{target}, Cells: shooterCells(view)}
	if len(ask.Cells) > maxGeometryCells/2 {
		ask.Cells = ask.Cells[:maxGeometryCells/2]
	}
	for _, h := range rankThreats(view) {
		if len(ask.Hostiles) < maxGeometryHostiles {
			ask.Hostiles = append(ask.Hostiles, h.ID)
		}
	}
	return ask
}

// flankDetach takes the answered proposals nearest the anchor, spaced, off
// the line and every other role's cell, for the flank gunners.
func flankDetach(view CombatView, geometry GeometryReply, m *CombatMemory) {
	m.Flank = &CombatFlank{}
	from, _, ok := flankAnchor(view)
	gunners := flankGunners(m.Roles)
	if !ok || len(gunners) == 0 {
		return
	}
	taken := map[domain.Cell]bool{}
	for _, r := range m.Roles {
		if r.Cell != nil && !slices.Contains(gunners, r.Pawn) {
			taken[*r.Cell] = true
		}
	}
	cells := nearestFree(geometry, from, taken)
	if len(cells) < len(gunners) {
		return
	}
	m.Flank.Pawns, m.Flank.Cells = gunners, cells[:len(gunners)]
}

// nearestFree is the answered proposals not taken, nearest from first,
// spaced a tile apart while they allow it.
func nearestFree(geometry GeometryReply, from domain.Cell, taken map[domain.Cell]bool) []domain.Cell {
	var cells []domain.Cell
	for _, c := range geometry.Proposals {
		if !taken[c] && !slices.Contains(cells, c) {
			cells = append(cells, c)
		}
	}
	slices.SortStableFunc(cells, func(a, b domain.Cell) int {
		da, db := distance2(from, a), distance2(from, b)
		switch {
		case da < db:
			return -1
		case da > db:
			return 1
		}
		return 0
	})
	return spaceCells(cells, 1)
}

// flank moves the detachment to its cells each stop and keeps its targets
// clear until the lead hostile passes it: a live hostile at or beyond the
// detachment's deepest cell along the approach, or one targeting a
// flanker. Then it opens for good, and focus fire picks its targets.
func flank(view CombatView, m *CombatMemory) {
	f := m.Flank
	if f == nil || len(f.Pawns) == 0 {
		return
	}
	layout, _ := view.Layout.Value()
	choke, _ := layout.Choke.Value()
	v, _ := towardVector(layout.Toward)
	project := func(c domain.Cell) int32 { return (c.X-choke.X)*v.X + (c.Z-choke.Z)*v.Z }
	deepest := project(f.Cells[0])
	for _, c := range f.Cells[1:] {
		deepest = max(deepest, project(c))
	}
	for _, h := range rankThreats(view) {
		c, ok := h.Cell.Value()
		if ok && project(c) >= deepest || slices.Contains(f.Pawns, h.Target) {
			f.Open = true
		}
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		j := slices.Index(f.Pawns, r.Pawn)
		if j < 0 || r.Retreat {
			continue
		}
		c := f.Cells[j]
		r.Cell = &c
		if !f.Open {
			r.Target = ""
		}
	}
}

// flankHoldFire is a hold-fire order for each waiting flanker not held yet.
func flankHoldFire(view CombatView, orderable map[domain.PawnID]bool, m *CombatMemory) []CombatOrder {
	f := m.Flank
	if f == nil || f.Open {
		return nil
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	var out []CombatOrder
	for _, id := range f.Pawns {
		s := state[id]
		if !orderable[id] || s.Dead || s.Downed || s.FireMode == HoldFire || s.FireMode == "" && slices.Contains(f.Held, id) {
			continue
		}
		out = append(out, CombatOrder{Pawn: id, Kind: OrderFireMode, FireMode: HoldFire, Reason: ReasonFlank})
		if !slices.Contains(f.Held, id) {
			f.Held = append(f.Held, id)
		}
	}
	return out
}

// ReasonFlank is a flanker's hold-fire while it waits.
const ReasonFlank CombatOrderReason = "flank"
