package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// waveMinAnimals is the smallest pack that counts as a psychic wave (#899):
// fewer animals come through one door at a time.
const waveMinAnimals = 6

// waveChoke is one door of the planned rooms a wave's animals approach,
// the cells just inside it, and the animal nearest it.
type waveChoke struct {
	door    domain.Cell
	cells   []domain.Cell
	animals int
	nearest domain.PawnID
}

// waveChokes are the doors a psychic wave approaches (#899): at least
// waveMinAnimals animals and no exploder, then spreadChokes.
func waveChokes(view CombatView) []waveChoke {
	ranked := rankThreats(view)
	if len(ranked) < waveMinAnimals || len(liveExploders(view)) > 0 {
		return nil
	}
	return spreadChokes(view, ranked)
}

// chargeChokes are the doors a shielded melee charge approaches (#1053):
// when most live hostiles are melee with a worn shield, every door they
// approach is a choke, so the charge splits across them. Nil otherwise.
func chargeChokes(view CombatView) []waveChoke {
	ranked := rankThreats(view)
	shielded := 0
	for _, h := range ranked {
		if _, ok := h.Shield.Value(); ok && meleeHostile(h) {
			shielded++
		}
	}
	if 2*shielded <= len(ranked) {
		return nil
	}
	return spreadChokes(view, ranked)
}

// meleeHostile is a hostile whose weapon reaches about one cell: the known
// range, else the weapon def's.
func meleeHostile(h CombatPawnState) bool {
	reach := h.WeaponRange
	if reach <= 0 {
		reach = h.WeaponFacts.Range
	}
	return reach <= 1.5
}

// spreadChokes counts each ranked hostile toward the planned-room door
// nearest it; with two or more doors approached, every approached door is
// a choke, most hostiles first. Nil otherwise.
func spreadChokes(view CombatView, ranked []CombatPawnState) []waveChoke {
	var doors []domain.Cell
	for _, r := range view.Rooms {
		for _, d := range r.Doors {
			if !slices.Contains(doors, d) {
				doors = append(doors, d)
			}
		}
	}
	if len(doors) < 2 {
		return nil
	}
	byDoor := map[domain.Cell]*waveChoke{}
	best := map[domain.Cell]int64{}
	for _, h := range ranked {
		at, ok := h.Cell.Value()
		if !ok {
			continue
		}
		door := doors[nearestIndex(at, doors)]
		c := byDoor[door]
		if c == nil {
			c = &waveChoke{door: door, cells: insideDoor(view, door, at)}
			byDoor[door] = c
		}
		c.animals++
		if d := distance2(at, door); c.nearest == "" || d < best[door] {
			c.nearest, best[door] = h.ID, d
		}
	}
	if len(byDoor) < 2 {
		return nil
	}
	var out []waveChoke
	for _, c := range byDoor {
		out = append(out, *c)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].animals != out[j].animals {
			return out[i].animals > out[j].animals
		}
		return cellLess(out[i].door, out[j].door)
	})
	return out
}

// insideDoor is the blocker cells just inside door, on the side of its
// room away from from (an approaching animal): straight in, then the two
// inside diagonals, each on the room's floor.
func insideDoor(view CombatView, door, from domain.Cell) []domain.Cell {
	var cells []domain.Cell
	far := int64(-1)
	for _, r := range view.Rooms {
		if !slices.Contains(r.Doors, door) {
			continue
		}
		n, ok := outward(r.Interior, door)
		if !ok {
			continue
		}
		in := domain.Cell{X: door.X - n.X, Z: door.Z - n.Z}
		if d := distance2(in, from); d <= far {
			continue
		} else {
			far = d
		}
		t := domain.Cell{X: n.Z, Z: n.X}
		cells = nil
		for _, c := range []domain.Cell{in, {X: in.X + t.X, Z: in.Z + t.Z}, {X: in.X - t.X, Z: in.Z - t.Z}} {
			if r.contains(c) {
				cells = append(cells, c)
			}
		}
	}
	return cells
}

// waveBlockers deals the brawlers, best armored first, round-robin to the
// chokes (most animals first), at most maxChokeBlockers each, onto the
// cells just inside each door; each engages the animal nearest its door.
func waveBlockers(chokes []waveChoke, pool []SquadDefenderFacts) []CombatRole {
	var roles []CombatRole
	next := make([]int, len(chokes))
	for len(pool) > 0 {
		placed := false
		for i := range chokes {
			if len(pool) == 0 || next[i] >= min(maxChokeBlockers, len(chokes[i].cells)) {
				continue
			}
			cell := chokes[i].cells[next[i]]
			next[i]++
			roles = append(roles, CombatRole{Pawn: pool[0].ID, Cell: &cell, Target: chokes[i].nearest, Duty: DutyBlocker})
			pool = pool[1:]
			placed = true
		}
		if !placed {
			break
		}
	}
	return roles
}
