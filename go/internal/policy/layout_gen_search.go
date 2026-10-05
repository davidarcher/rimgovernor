package policy

import (
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Deterministic local search (#1957, epic #1938). A generated base plan is
// varied by a fixed number of operators and a variation is kept when its
// core score (planScorer.core) is strictly better, so the searched plan
// never scores below the plan it started from. Every choice comes from a
// splitmix64 stream seeded by the site's seed cell, so the same input gives
// the same plan on any box and at any thread count; the iteration count is
// the budget, and wall time (siteSearchHang) only cuts a hung run short.
//
// Operators:
//   - moveCluster: an affinity cluster (layout_gen_cluster.go) is lifted
//     out and placed again on ground that has the old slot struck from it,
//     so it lands in the next slot (the other side of the hallway
//     included);
//   - resiteWing: a bedroom wing is sited again with its old column (a
//     radius of columns, or its whole side of the hallway) struck.
//
// Hallways are trimmed back to what their rooms and wings need after a
// move, so a room taken from a far end shortens the base.

// searchRand is a splitmix64 stream.
type searchRand uint64

func newSearchRand(seed domain.Cell) searchRand {
	return searchRand(uint64(uint32(seed.X))<<32 | uint64(uint32(seed.Z)) ^ 0x9e3779b97f4a7c15)
}

func (r *searchRand) next() uint64 {
	*r += 0x9e3779b97f4a7c15
	z := uint64(*r)
	z = (z ^ z>>30) * 0xbf58476d1ce4e5b9
	z = (z ^ z>>27) * 0x94d049bb133111eb
	return z ^ z>>31
}

// intn is a value in 0..n-1.
func (r *searchRand) intn(n int) int { return int(r.next() % uint64(n)) }

// search runs iters operators over plan, a base plan generated on g (before
// finish), and returns the best plan it saw.
func (g coreGrid) search(sc planScorer, plan LayoutPlan, seed domain.Cell, iters int) LayoutPlan {
	rng := newSearchRand(seed)
	cur, curScore := plan, sc.core(plan)
	deadline := time.Now().Add(siteSearchHang)
	for range iters {
		if time.Now().After(deadline) {
			break
		}
		cand, ok := g.vary(cur, &rng)
		if !ok {
			continue
		}
		if s := sc.core(cand); s.Better(curScore) {
			cur, curScore = cand, s
		}
	}
	return cur
}

// vary applies one operator to plan.
func (g coreGrid) vary(plan LayoutPlan, rng *searchRand) (LayoutPlan, bool) {
	var out LayoutPlan
	var ok bool
	switch rng.intn(7) {
	case 0, 1:
		out, ok = g.resiteWing(plan, rng)
	default:
		out, ok = g.moveCluster(plan, rng)
	}
	if !ok {
		return plan, false
	}
	return routedEntrances(out), true
}

// searchRole reports the roles the operators move: the base rooms.
func searchRole(role PlannedRole) bool {
	for _, r := range coreBaseRooms {
		if r == role {
			return true
		}
	}
	return false
}

// moveCluster lifts one affinity cluster out of plan and places it again
// on ground with its old first slot, padded by a random margin, struck.
func (g coreGrid) moveCluster(plan LayoutPlan, rng *searchRand) (LayoutPlan, bool) {
	var roles []PlannedRole
	seen := map[PlannedRole]bool{}
	for _, r := range plan.Rooms {
		if searchRole(r.Role) && !seen[r.Role] && !g.fixed[r.Interior] {
			seen[r.Role] = true
			roles = append(roles, r.Role)
		}
	}
	clusters := affinityClusters(roles)
	if len(clusters) == 0 {
		return plan, false
	}
	c := clusters[rng.intn(len(clusters))]
	taken := map[PlannedRole]bool{}
	var kept []PlannedRoom
	var first *PlannedRoom
	in := map[PlannedRole]bool{}
	for _, role := range c.roles {
		in[role] = true
	}
	for _, r := range plan.Rooms {
		if in[r.Role] && !taken[r.Role] && !g.fixed[r.Interior] {
			taken[r.Role] = true
			if first == nil {
				r := r
				first = &r
			}
			continue
		}
		kept = append(kept, r)
	}
	if first == nil {
		return plan, false
	}
	ground := g.clone()
	ground.carveBedroomWings(plan.Wings)
	ground.carveSuiteWings(plan.Wings)
	ground.carve(pad(roomWalls(*first), int32(rng.intn(4))))
	base := plan
	base.Rooms = kept
	base.Spine = trimSpine(base)
	spine, rooms, placed := ground.placeCluster(base.Spine, kept, plan.Wings, c)
	if placed != len(c.roles) {
		return plan, false
	}
	plan.Rooms = rooms
	plan.Spine = spine
	plan.Spine = trimSpine(plan)
	return plan, true
}

// resiteWing takes one bedroom wing out of plan and sites it again, with
// its old side of the hallway struck within a random column radius, or
// altogether (the mirrored wing). The new wing must hold at least as many
// rooms.
func (g coreGrid) resiteWing(plan LayoutPlan, rng *searchRand) (LayoutPlan, bool) {
	var idx []int
	for _, i := range bedroomWings(plan.Wings) {
		if !wingPinned(plan.Wings[i], g.fixed) {
			idx = append(idx, i)
		}
	}
	if len(idx) == 0 {
		return plan, false
	}
	k := idx[rng.intn(len(idx))]
	old := plan.Wings[k]
	of := frameOf(old)
	mirror := rng.intn(2) == 0
	radius := int32(rng.intn(12))
	var others []Wing
	others = append(others, plan.Wings[:k]...)
	others = append(others, plan.Wings[k+1:]...)
	base := plan
	base.Wings = others
	spine := trimSpine(base)
	skip := func(f wingFrame) bool {
		return f.sign == of.sign && f.horiz == of.horiz && (mirror || f.cx >= of.cx-radius && f.cx <= of.cx+radius)
	}
	spine, wings, ok := g.siteBedWing(spine, plan.Rooms, others, of.size, skip)
	if !ok || len(wings[len(wings)-1].Rooms) < len(old.Rooms) {
		return plan, false
	}
	plan.Spine, plan.Wings = spine, wings
	return plan, true
}

// trimSpine shortens every east-west hallway to the cells its rooms'
// doors, its wings' corridors and the hallways crossing it need. A hallway
// that serves nothing is left as it is.
func trimSpine(plan LayoutPlan) []SpineSegment {
	spine := append([]SpineSegment(nil), plan.Spine...)
	for i, s := range spine {
		if !alongX(s) {
			continue
		}
		lo0, hi0 := min(s.From.X, s.To.X), max(s.From.X, s.To.X)
		var lo, hi int32
		need := false
		reach := func(a, b int32) {
			if !need {
				lo, hi, need = a, b, true
			}
			lo, hi = min(lo, a), max(hi, b)
		}
		for _, r := range plan.Rooms {
			if onSegment(r, s) {
				reach(r.Interior.X-1, r.Interior.X+r.Interior.Width)
			}
		}
		for _, w := range plan.Wings {
			f := frameOf(w)
			if !f.horiz && f.z0 == s.From.Z && f.cx >= lo0 && f.cx <= hi0 {
				reach(f.cx-1, f.cx+1)
			}
		}
		for _, t := range plan.Spine {
			if alongX(t) {
				continue
			}
			zlo, zhi := min(t.From.Z, t.To.Z), max(t.From.Z, t.To.Z)
			if t.From.X >= lo0 && t.From.X <= hi0 && s.From.Z >= zlo-1 && s.From.Z <= zhi+1 {
				reach(t.From.X-1, t.From.X+1)
			}
		}
		if need {
			lo, hi = max(lo, lo0), min(hi, hi0)
			if s.From.X > s.To.X {
				lo, hi = hi, lo
			}
			spine[i].From.X, spine[i].To.X = lo, hi
		}
	}
	return spine
}
