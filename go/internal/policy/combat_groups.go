package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Divide and conquer (#1064): a raid split into groups on different flanks
// gets one squad per group. The group nearest the layout choke (the line's
// middle without one) keeps the hold; each other group gets a share of the
// line's gunners proportional to its hostile count, posted at firing cells
// the game proposes around the midpoint of the group's centroid and home
// (the line's middle), and a fallback cell one step toward home that the
// squad takes, for good, once a group hostile closes to groupFallbackRange.

const (
	// groupGap is the single-link gap between hostiles of one group: a
	// hostile within groupGap cells (8-way) of a group member joins it.
	groupGap = 8
	// groupMinHostiles is the smallest cluster that counts as a group;
	// stragglers stay the main line's.
	groupMinHostiles = 2
	// groupFallbackRange is the 8-way distance at which a group hostile
	// drives the squad back to its fallback cells.
	groupFallbackRange = 3
)

// CombatGroup is one raid group's squad (#1064). Hostiles are the group's
// members when it was given its squad; an empty Pawns means the line had
// no gunner to spare or the game proposed no cells, and the fight does
// not ask again for that group until it re-forms.
type CombatGroup struct {
	Hostiles []domain.PawnID `json:",omitempty"`
	Pawns    []domain.PawnID `json:",omitempty"`
	Cells    []domain.Cell   `json:",omitempty"`
	Fallback []domain.Cell   `json:",omitempty"`
	FellBack bool            `json:",omitempty"`
	Centroid domain.Cell
}

type raidGroup struct {
	hostiles []domain.PawnID
	cells    []domain.Cell
	centroid domain.Cell
}

// raidGroups clusters the live hostiles with known cells, single-link at
// groupGap, keeping clusters of groupMinHostiles or more, in id order of
// their first member.
func raidGroups(view CombatView) []raidGroup {
	var hs []CombatPawnState
	for _, h := range rankThreats(view) {
		if _, ok := h.Cell.Value(); ok {
			hs = append(hs, h)
		}
	}
	slices.SortFunc(hs, func(a, b CombatPawnState) int {
		switch {
		case a.ID < b.ID:
			return -1
		case a.ID > b.ID:
			return 1
		}
		return 0
	})
	group := make([]int, len(hs))
	for i := range group {
		group[i] = i
	}
	find := func(i int) int {
		for group[i] != i {
			i = group[i]
		}
		return i
	}
	for i := range hs {
		for j := i + 1; j < len(hs); j++ {
			a, _ := hs[i].Cell.Value()
			b, _ := hs[j].Cell.Value()
			if within(a, b, groupGap) {
				ri, rj := find(i), find(j)
				group[max(ri, rj)] = min(ri, rj)
			}
		}
	}
	var out []raidGroup
	index := map[int]int{}
	for i, h := range hs {
		r := find(i)
		k, ok := index[r]
		if !ok {
			k = len(out)
			index[r] = k
			out = append(out, raidGroup{})
		}
		c, _ := h.Cell.Value()
		out[k].hostiles = append(out[k].hostiles, h.ID)
		out[k].cells = append(out[k].cells, c)
	}
	out = slices.DeleteFunc(out, func(g raidGroup) bool { return len(g.hostiles) < groupMinHostiles })
	for i := range out {
		var x, z int64
		for _, c := range out[i].cells {
			x, z = x+int64(c.X), z+int64(c.Z)
		}
		n := int64(len(out[i].cells))
		out[i].centroid = domain.Cell{X: int32(x / n), Z: int32(z / n)}
	}
	return out
}

// groupHome is the layout line's middle cell, and its choke (or that
// middle again without one).
func groupHome(view CombatView) (home, choke domain.Cell, ok bool) {
	layout, ok := view.Layout.Value()
	if !ok || len(layout.Firing) == 0 {
		return domain.Cell{}, domain.Cell{}, false
	}
	var x, z int64
	for _, c := range layout.Firing {
		x, z = x+int64(c.X), z+int64(c.Z)
	}
	n := int64(len(layout.Firing))
	home = domain.Cell{X: int32(x / n), Z: int32(z / n)}
	if c, known := layout.Choke.Value(); known {
		return home, c, true
	}
	return home, home, true
}

// sideGroups are the raid's groups other than the one nearest the choke,
// that no squad answers yet; nil unless the raid is split.
func sideGroups(view CombatView, m CombatMemory) []raidGroup {
	_, choke, ok := groupHome(view)
	groups := raidGroups(view)
	if !ok || len(groups) < 2 {
		return nil
	}
	main := 0
	for i, g := range groups {
		if distance2(g.centroid, choke) < distance2(groups[main].centroid, choke) {
			main = i
		}
	}
	var out []raidGroup
	for i, g := range groups {
		answered := slices.ContainsFunc(m.Groups, func(s CombatGroup) bool {
			return slices.ContainsFunc(g.hostiles, func(h domain.PawnID) bool { return slices.Contains(s.Hostiles, h) })
		})
		if i != main && !answered {
			out = append(out, g)
		}
	}
	return out
}

// groupAnchor is midway between a group's centroid and home.
func groupAnchor(home domain.Cell, g raidGroup) domain.Cell {
	return domain.Cell{X: (home.X + g.centroid.X) / 2, Z: (home.Z + g.centroid.Z) / 2}
}

// groupAsk is the next unanswered side group's firing_cells ask on a hold.
func groupAsk(view CombatView, m CombatMemory) *GeometryRequest {
	if m.Tactic != TacticHold || m.MechLure {
		return nil
	}
	side := sideGroups(view, m)
	home, _, ok := groupHome(view)
	if len(side) == 0 || !ok {
		return nil
	}
	return firingCellsAsk(view, groupAnchor(home, side[0]), side[0].centroid)
}

// groupGunners are the hold's line gunners no flank or squad has taken,
// in pawn order.
func groupGunners(m CombatMemory) []domain.PawnID {
	var out []domain.PawnID
	for _, r := range m.Roles {
		if !r.Ranged || r.Duty != "" || r.Retreat || r.Cell == nil || r.Mortar != nil ||
			m.Flank != nil && slices.Contains(m.Flank.Pawns, r.Pawn) || slices.ContainsFunc(m.Groups, func(g CombatGroup) bool { return slices.Contains(g.Pawns, r.Pawn) }) {
			continue
		}
		out = append(out, r.Pawn)
	}
	return out
}

// groupDetach gives the first side group its squad from the answered
// proposals: the free gunners times the group's share of the grouped
// hostiles, at least one, always leaving one on the main line.
func groupDetach(view CombatView, geometry GeometryReply, m *CombatMemory) {
	side := sideGroups(view, *m)
	home, _, ok := groupHome(view)
	if len(side) == 0 || !ok {
		return
	}
	g := side[0]
	s := CombatGroup{Hostiles: g.hostiles, Centroid: g.centroid}
	defer func() { m.Groups = append(m.Groups, s) }()
	total := 0
	for _, rg := range raidGroups(view) {
		total += len(rg.hostiles)
	}
	gunners := groupGunners(*m)
	n := max(1, len(gunners)*len(g.hostiles)/total)
	n = min(n, len(gunners)-1)
	if n <= 0 {
		return
	}
	taken := map[domain.Cell]bool{}
	for _, r := range m.Roles {
		if r.Cell != nil {
			taken[*r.Cell] = true
		}
	}
	cells := nearestFree(geometry, groupAnchor(home, g), taken)
	n = min(n, len(cells))
	if n == 0 {
		return
	}
	s.Pawns, s.Cells = gunners[len(gunners)-n:], cells[:n]
	for _, c := range s.Cells {
		s.Fallback = append(s.Fallback, domain.Cell{X: c.X + sign(home.X-c.X), Z: c.Z + sign(home.Z-c.Z)})
	}
}

// groupSquads posts each squad on its cells (its fallback cells once a
// live group hostile closes to groupFallbackRange) and points it at its
// group's top-ranked live hostile.
func groupSquads(view CombatView, m *CombatMemory) {
	ranked := rankThreats(view)
	for gi := range m.Groups {
		g := &m.Groups[gi]
		if len(g.Pawns) == 0 {
			continue
		}
		var target domain.PawnID
		for _, h := range ranked {
			if !slices.Contains(g.Hostiles, h.ID) {
				continue
			}
			if target == "" {
				target = h.ID
			}
			if c, ok := h.Cell.Value(); ok && slices.ContainsFunc(g.Cells, func(s domain.Cell) bool { return within(s, c, groupFallbackRange) }) {
				g.FellBack = true
			}
		}
		for i := range m.Roles {
			r := &m.Roles[i]
			j := slices.Index(g.Pawns, r.Pawn)
			if j < 0 {
				continue
			}
			c := g.Cells[j]
			if g.FellBack {
				c = g.Fallback[j]
			}
			r.Cell, r.Retreat = &c, g.FellBack
			if target != "" {
				r.Target = target
			}
		}
	}
}
