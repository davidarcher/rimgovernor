package policy

import (
	"errors"
	"fmt"
	"slices"
	"sort"
	"strings"

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
	// RockNeedsSky is a generator footprint or catch cell: dug like a
	// floor, and its roof comes off as well.
	RockNeedsSky
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
	// Left is the wall-role cells on rock, kept as natural rock; they count
	// as built. An unlisted (fogged) wall-role cell is unseen mountain and
	// is left too.
	Left []domain.Cell
	// Unroof is the needs-sky cells carrying a removable roof, dug or
	// already open. Unfit is the needs-sky cells that cannot be opened: an
	// unseen cell, an unknown roof or a thick mountain roof.
	Unroof, Unfit []domain.Cell
}

// RockStep classifies a planned structure's cells against the terrain facts
// in the frame. A needs-floor cell on rock is dug; a blocks cell on rock is
// left as rock. A cell the frame does not list is fogged mountain: when it
// needs a floor it is dug, because roof support is read from the true map
// when the dig is read, and when it blocks it is left as rock. A listed cell
// that is not rock is open and needs nothing. A needs-sky cell is dug when
// rock and unroofed when its roof is removable (Unroof), and is Unfit when
// unseen or under a thick roof. Duplicate cells collapse, and
// needs-floor wins over blocks for the same cell.
func RockStep(planned []RoleCell, cells []SiteCell) RockStepResult {
	out, _ := RockStepRoofs(planned, cells, nil)
	return out
}

// RoofRule is what the game's RoofDef row says about a roof. The
// game refuses to designate a no-roof area under a roof whose isThickRoof
// is set (Designator_AreaNoRoof.CanDesignateCell, read with ilspycmd) and
// checks nothing else when a roof is removed: canCollapse does not gate it,
// so it is not carried. Natural is the RoofDef isNatural flag: a mountain
// roof.
type RoofRule struct{ Thick, Natural bool }

// Removable reports whether the roof can be taken off.
func (r RoofRule) Removable() bool { return !r.Thick }

// RoofRules is the load's roof rules by RoofDef name (the bridge catalog's
// RoofRules). A roof not in it is unknown.
type RoofRules map[string]RoofRule

// ErrUnknownRoof marks a needs-sky cell whose roof def is not in the rules.
var ErrUnknownRoof = errors.New("unknown roof def")

// RockStepRoofs is RockStep with the roof rules that decide a needs-sky
// cell: a roof is removable when its RoofDef is not thick. A roofed cell
// whose def is not in roofs is Unfit and the error wraps ErrUnknownRoof,
// naming the def; the result is still complete for the other cells. RockStep
// passes no rules, so it is for plans with no needs-sky cell.
func RockStepRoofs(planned []RoleCell, cells []SiteCell, roofs RoofRules) (RockStepResult, error) {
	var unknown []string
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
		} else if p.Role > prev {
			role[p.Cell] = p.Role
		}
	}
	var out RockStepResult
	for _, cell := range order {
		c, listed := site[cell]
		if role[cell] == RockNeedsSky {
			// The native grid names a roof only where one stands: an unroofed
			// cell reads Roofed=false with the roof name unknown.
			roof, known := c.Roof.Value()
			if roofed, ok := c.Roofed.Value(); ok && !roofed {
				roof, known = "", true
			}
			if !listed || !known {
				out.Unfit = append(out.Unfit, cell)
				continue
			}
			if roof != "" {
				rule, ok := roofs[roof]
				if !ok {
					if !slices.Contains(unknown, roof) {
						unknown = append(unknown, roof)
					}
					out.Unfit = append(out.Unfit, cell)
					continue
				}
				if !rule.Removable() {
					out.Unfit = append(out.Unfit, cell)
					continue
				}
				out.Unroof = append(out.Unroof, cell)
			}
			if rockCell(c) {
				out.Dig = append(out.Dig, cell)
			}
			continue
		}
		if listed && !rockCell(c) {
			continue
		}
		if role[cell] == RockBlocks {
			out.Left = append(out.Left, cell)
			continue
		}
		out.Dig = append(out.Dig, cell)
	}
	if len(unknown) > 0 {
		slices.Sort(unknown)
		return out, fmt.Errorf("%w: %s", ErrUnknownRoof, strings.Join(unknown, ", "))
	}
	return out, nil
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

// RockAccess is the walkable cell a miner reaches a footprint's rock from:
// the first listed cell outside footprint, in Z then X order, that is open
// ground (not occupied, walkable) and shares an edge with a footprint cell.
// False when the footprint is walled in by rock or unseen ground, so the
// caller says the rock cannot be reached instead of guessing a cell.
func RockAccess(footprint []domain.Cell, cells []SiteCell) (domain.Cell, bool) {
	inside := make(map[domain.Cell]bool, len(footprint))
	for _, c := range footprint {
		inside[c] = true
	}
	var open []domain.Cell
	for _, c := range cells {
		walkable, wk := c.Walkable.Value()
		if inside[c.Cell] || c.Occupied() || !wk || !walkable {
			continue
		}
		for _, d := range [4]domain.Cell{{X: 1}, {X: -1}, {Z: 1}, {Z: -1}} {
			if inside[domain.Cell{X: c.Cell.X + d.X, Z: c.Cell.Z + d.Z}] {
				open = append(open, c.Cell)
				break
			}
		}
	}
	if len(open) == 0 {
		return domain.Cell{}, false
	}
	return MergeRockDigs(open)[0], true
}
