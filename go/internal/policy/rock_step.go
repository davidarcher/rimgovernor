package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RockRole says what a planned structure does with a cell, which decides
// what natural rock on it means.
type RockRole int

const (
	// RockBlocks is a wall or edge cell: natural rock there already blocks,
	// so it is left as rock and nothing waits on it.
	RockBlocks RockRole = iota
	// RockNeedsFloor is a floor, door, interior, corridor or turret/turbine
	// footprint cell: rock there is dug, then built on.
	RockNeedsFloor
)

// RoleCell is one cell of a planned structure with its role.
type RoleCell struct {
	Cell domain.Cell
	Role RockRole
}

// RockStepResult is the shared admission rock step's answer for one plan.
type RockStepResult struct {
	// Dig is the rock to excavate before building, in input order, each
	// cell once.
	Dig []domain.Cell
	// Left is the listed wall-role cells on rock, kept as natural rock; they
	// count as built.
	Left []domain.Cell
}

// RockStep classifies a planned structure's cells against the terrain facts
// in the frame. A needs-floor cell on rock is dug; a blocks cell on rock is
// left as rock. A cell the frame does not list is fogged mountain: when it
// needs a floor it is dug, because roof support is read from the true map
// when the dig is read, and when it blocks nothing is needed. A listed cell
// that is not rock is open and needs nothing. Duplicate cells collapse, and
// needs-floor wins over blocks for the same cell.
func RockStep(planned []RoleCell, cells []SiteCell) RockStepResult {
	site := make(map[domain.Cell]SiteCell, len(cells))
	for _, c := range cells {
		site[c.Cell] = c
	}
	role := make(map[domain.Cell]RockRole, len(planned))
	var order []domain.Cell
	for _, p := range planned {
		prev, seen := role[p.Cell]
		if !seen {
			order = append(order, p.Cell)
			role[p.Cell] = p.Role
		} else if p.Role == RockNeedsFloor && prev == RockBlocks {
			role[p.Cell] = RockNeedsFloor
		}
	}
	var out RockStepResult
	for _, cell := range order {
		c, listed := site[cell]
		if listed && !rockCell(c) {
			continue
		}
		if role[cell] == RockBlocks {
			if listed {
				out.Left = append(out.Left, cell)
			}
			continue
		}
		out.Dig = append(out.Dig, cell)
	}
	return out
}

// MergeRockDigs unions the dig lists of several plans into one excavation
// list: a cell two plans both need is dug once. The result is sorted by Z
// then X so overlapping requests produce the same excavation whatever the
// plan order.
func MergeRockDigs(digs ...[]domain.Cell) []domain.Cell {
	seen := map[domain.Cell]bool{}
	var out []domain.Cell
	for _, d := range digs {
		for _, c := range d {
			if !seen[c] {
				seen[c] = true
				out = append(out, c)
			}
		}
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Z != out[j].Z {
			return out[i].Z < out[j].Z
		}
		return out[i].X < out[j].X
	})
	return out
}
