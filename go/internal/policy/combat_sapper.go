package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TacticSapper answers a sapper or breacher raid: the raid ignores
// the killbox and digs or blasts toward the rooms, so the defenders post
// inside the wall it is about to break instead of holding the line.
const TacticSapper CombatTactic = "sapper"

// Sapper formation constants.
const (
	// sapperInset is how far inside the breach, in cells, the gunners stand.
	sapperInset = 3
	// sapperSpread is the gap between gunners along the wall, in cells.
	sapperSpread = 2
)

// SapperBreach is a predicted breach: the wall cell the sappers
// will open and the room behind it.
type SapperBreach struct {
	// Wall is the room's wall cell nearest the sappers; Inside the floor
	// cell just behind it; In the unit step from Wall into the room.
	Wall, Inside, In domain.Cell
	Room             CombatRoom
}

// liveSappers are the live hostiles flagged sapper or breacher, by threat
// rank.
func liveSappers(view CombatView) []CombatPawnState {
	var out []CombatPawnState
	for _, h := range rankThreats(view) {
		if h.Sapper {
			out = append(out, h)
		}
	}
	return out
}

// predictBreach predicts where the sappers break in. They head for
// the base's rooms, so the target is the frame's room whose centre is
// nearest the live sappers' centroid, and the breach is that room's wall
// cell nearest the centroid (a corner moves onto the nearer side). No
// rooms, no sapper at a known cell, or sappers already inside a room
// predict nothing. Ore is not avoided: the frame has no ore cells.
func predictBreach(view CombatView) (SapperBreach, bool) {
	var sx, sz float64
	n := 0
	for _, h := range liveSappers(view) {
		if c, ok := h.Cell.Value(); ok {
			sx, sz, n = sx+float64(c.X), sz+float64(c.Z), n+1
		}
	}
	if n == 0 || len(view.Rooms) == 0 {
		return SapperBreach{}, false
	}
	cx, cz := sx/float64(n), sz/float64(n)
	best, bestD := -1, math.Inf(1)
	for i, r := range view.Rooms {
		in := r.Interior
		if in.Width <= 0 || in.Height <= 0 {
			continue
		}
		mx, mz := float64(in.X)+float64(in.Width-1)/2, float64(in.Z)+float64(in.Height-1)/2
		if d := math.Hypot(cx-mx, cz-mz); d < bestD {
			best, bestD = i, d
		}
	}
	if best < 0 {
		return SapperBreach{}, false
	}
	room := view.Rooms[best]
	in := room.Interior
	x0, x1, z0, z1 := in.X, in.X+in.Width-1, in.Z, in.Z+in.Height-1
	x := clamp32(int32(math.Round(cx)), x0-1, x1+1)
	z := clamp32(int32(math.Round(cz)), z0-1, z1+1)
	outX, outZ := x < x0 || x > x1, z < z0 || z > z1
	switch {
	case !outX && !outZ:
		return SapperBreach{}, false
	case outX && outZ:
		// A corner: step onto the side the centroid is farther past.
		if math.Abs(cx-float64(clamp32(x, x0, x1))) >= math.Abs(cz-float64(clamp32(z, z0, z1))) {
			z = clamp32(z, z0, z1)
			outZ = false
		} else {
			x = clamp32(x, x0, x1)
			outX = false
		}
	}
	var step domain.Cell
	switch {
	case outX && x < x0:
		step = domain.Cell{X: 1}
	case outX:
		step = domain.Cell{X: -1}
	case z < z0:
		step = domain.Cell{Z: 1}
	default:
		step = domain.Cell{Z: -1}
	}
	wall := domain.Cell{X: x, Z: z}
	return SapperBreach{Wall: wall, Inside: domain.Cell{X: x + step.X, Z: z + step.Z}, In: step, Room: room}, true
}

func clamp32(v, lo, hi int32) int32 {
	return max(lo, min(hi, v))
}

// breachPosts are floor cells of the breach's room depth cells in from the
// wall (fewer when the room is shallower), stride cells apart along the
// wall: straight in first, then alternating either side.
func breachPosts(b SapperBreach, depth, stride int32, n int) []domain.Cell {
	in := b.Room.Interior
	across := domain.Cell{X: b.In.Z * b.In.Z, Z: b.In.X * b.In.X}
	var out []domain.Cell
	for d := depth; d >= 1 && len(out) < n; d-- {
		base := domain.Cell{X: b.Wall.X + b.In.X*d, Z: b.Wall.Z + b.In.Z*d}
		if !b.Room.contains(base) {
			continue
		}
		reach := max(in.Width, in.Height)
		for k := int32(0); k <= reach && len(out) < n; k++ {
			for _, sign := range []int32{1, -1} {
				if k == 0 && sign < 0 || len(out) >= n {
					continue
				}
				c := domain.Cell{X: base.X + sign*k*stride*across.X, Z: base.Z + sign*k*stride*across.Z}
				if b.Room.contains(c) {
					out = append(out, c)
				}
			}
		}
		break
	}
	return out
}

// sapperFormation posts the defenders at the predicted breach:
// gunners on cells sapperInset inside it, sapperSpread apart along the
// wall, on the top-ranked hostile (a sapper, by threat score); brawlers
// on the cells just inside it as blockers with no target, so none walks
// out to fight in the open.
func sapperFormation(view CombatView, b SapperBreach) []CombatRole {
	var gunners, rest []SquadDefenderFacts
	for _, d := range view.Defenders {
		switch {
		case !squadDefenderEligible(d) || !positive(d.Armed):
		case positive(d.RangedEquipped):
			gunners = append(gunners, d)
		default:
			rest = append(rest, d)
		}
	}
	var top domain.PawnID
	if ranked := rankThreats(view); len(ranked) > 0 {
		top = ranked[0].ID
	}
	var roles []CombatRole
	posts := breachPosts(b, sapperInset, sapperSpread, len(gunners))
	for i, g := range gunners {
		role := CombatRole{Pawn: g.ID, Target: top, Ranged: true}
		if i < len(posts) {
			c := posts[i]
			role.Cell = &c
		}
		roles = append(roles, role)
	}
	guards := brawlers(rest)
	cells := breachPosts(b, 1, 1, len(guards))
	for i, g := range guards {
		role := CombatRole{Pawn: g.ID, Duty: DutyBlocker}
		if i < len(cells) {
			c := cells[i]
			role.Cell = &c
		}
		roles = append(roles, role)
	}
	return sortRoles(roles)
}

// reformSapper is the sapper row of the reaction table: a sapper
// raid whose predicted breach is not the one the formation guards (a new
// raid, or the sappers turned toward another wall) re-forms, as does a
// sapper formation whose raid no longer predicts a breach or whose target
// is down.
func reformSapper(view CombatView, m CombatMemory) bool {
	b, ok := predictBreach(view)
	if m.Tactic != TacticSapper {
		return ok
	}
	return !ok || m.SapperBreach == nil || *m.SapperBreach != b.Wall || squadTargetDown(view, m)
}
