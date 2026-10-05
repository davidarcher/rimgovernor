package policy

import "sort"

// Affinity clusters (#1955, epic #1938). The affinity graph is the weighted
// trip table (routeTrips) plus the besideRoles pairs, which want a shared
// wall and a Link door. Its connected components over the non-housing base
// rooms are the clusters: kitchen-freezer-dining-butchery, workshop-storage,
// and the rooms nothing ties to another (the hospital, which only wants an
// entrance, the shelter, the tomb). A cluster is placed as a unit.

// besideAffinity is the weight of a besideRoles edge: the strongest tie,
// since the pair shares a wall and a door.
const besideAffinity = 5

// roleCluster is rooms that belong together, in placement order (a room
// after the neighbour whose wall it takes), with the affinity weight that
// ranks the cluster's claim on the centre of the base.
type roleCluster struct {
	roles  []ModuleRole
	weight int
}

// roleAffinity is every role's total edge weight in the graph: its trips
// (the entrance and housing ends included) and its besideRoles ties to a
// role in set.
func roleAffinity(set map[ModuleRole]bool) map[ModuleRole]int {
	w := map[ModuleRole]int{}
	for _, t := range routeTrips {
		w[t.from] += t.weight
		w[t.to] += t.weight
	}
	for role, rule := range besideRoles {
		nb := rule.neighbour
		if set[role] && set[nb] {
			w[role] += besideAffinity
			w[nb] += besideAffinity
		}
	}
	return w
}

// affinityClusters groups roles (distinct, in centrality order) into the
// connected components of the affinity graph, heaviest first, ties in roles
// order.
func affinityClusters(roles []ModuleRole) []roleCluster {
	set := map[ModuleRole]bool{}
	index := map[ModuleRole]int{}
	for i, r := range roles {
		set[r], index[r] = true, i
	}
	parent := map[ModuleRole]ModuleRole{}
	var find func(ModuleRole) ModuleRole
	find = func(r ModuleRole) ModuleRole {
		if p, ok := parent[r]; ok && p != r {
			parent[r] = find(p)
			return parent[r]
		}
		parent[r] = r
		return r
	}
	union := func(a, b ModuleRole) {
		if set[a] && set[b] {
			if ra, rb := find(a), find(b); ra != rb {
				parent[rb] = ra
			}
		}
	}
	for _, t := range routeTrips {
		union(t.from, t.to)
	}
	for role, rule := range besideRoles {
		nb := rule.neighbour
		union(role, nb)
	}
	byRoot := map[ModuleRole][]ModuleRole{}
	for _, r := range roles {
		byRoot[find(r)] = append(byRoot[find(r)], r)
	}
	aff := roleAffinity(set)
	var out []roleCluster
	for _, members := range byRoot {
		c := roleCluster{roles: members}
		in := map[ModuleRole]bool{}
		for _, r := range members {
			in[r] = true
			c.weight += aff[r]
		}
		depth := func(r ModuleRole) int {
			d := 0
			for nb, ok := besideOf(r); ok && in[nb]; nb, ok = besideOf(nb) {
				d++
			}
			return d
		}
		sort.SliceStable(c.roles, func(i, j int) bool {
			a, b := c.roles[i], c.roles[j]
			if da, db := depth(a), depth(b); da != db {
				return da < db
			}
			return index[a] < index[b]
		})
		out = append(out, c)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].weight != out[j].weight {
			return out[i].weight > out[j].weight
		}
		return index[out[i].roles[0]] < index[out[j].roles[0]]
	})
	return out
}

// clusterTries bounds how many slots one cluster tries for its root before
// it settles for the best split placement it saw.
const clusterTries = 6

// placeCluster places c's rooms on g, as a unit: the rooms are placed in
// order (each besideRoles member against its neighbour, packed on a shared
// wall with a Link door), and when a member could not take its neighbour's
// side, the root's slot is struck from the ground and the whole cluster is
// tried again from the next slot, up to clusterTries. A split cluster is
// kept only when no slot kept it whole, so every room that fits is still
// placed. It returns the grown plan and how many of c's rooms it placed.
func (g coreGrid) placeCluster(spine []SpineSegment, rooms []LayoutRoom, wings []Wing, c roleCluster) ([]SpineSegment, []LayoutRoom, int) {
	type attempt struct {
		spine  []SpineSegment
		rooms  []LayoutRoom
		placed int
		split  int
	}
	var best *attempt
	ground := g
	for try := 0; try < clusterTries; try++ {
		a := attempt{spine: spine, rooms: rooms}
		first := -1
		for _, role := range c.roles {
			var placed bool
			a.spine, a.rooms, placed, _ = ground.placeRole(a.spine, a.rooms, wings, role, coreRoomSize[role])
			if placed {
				a.placed++
				if first < 0 {
					first = len(a.rooms) - 1
				}
			}
		}
		a.split = splitPairs(a.rooms[len(rooms):], c)
		if best == nil || a.placed > best.placed || a.placed == best.placed && a.split < best.split {
			best = &a
		}
		if a.placed == len(c.roles) && a.split == 0 || first < 0 || len(c.roles) < 2 {
			break
		}
		// Strike the root's slot, so the next try puts the cluster elsewhere.
		if try == 0 {
			ground = g.clone()
		}
		ground.carve(roomWalls(a.rooms[first]))
	}
	return best.spine, best.rooms, best.placed
}

// splitPairs counts the besideRoles pairs inside c, among the rooms placed
// for it, whose rooms do not share a wall.
func splitPairs(placed []LayoutRoom, c roleCluster) int {
	at := map[ModuleRole]Rectangle{}
	for _, r := range placed {
		at[r.Role] = r.Interior
	}
	n := 0
	for _, role := range c.roles {
		nb, ok := besideOf(role)
		a, okA := at[role]
		b, okB := at[nb]
		if ok && okA && okB && !sharesWall(a, b) {
			n++
		}
	}
	return n
}

// sharesWall reports interiors a and b one wall apart, side by side or
// back to back, with their spans overlapping.
func sharesWall(a, b Rectangle) bool {
	overlapX := a.X < b.X+b.Width && b.X < a.X+a.Width
	overlapZ := a.Z < b.Z+b.Height && b.Z < a.Z+a.Height
	nextX := a.X+a.Width+1 == b.X || b.X+b.Width+1 == a.X
	nextZ := a.Z+a.Height+1 == b.Z || b.Z+b.Height+1 == a.Z
	return nextX && overlapZ || nextZ && overlapX
}
