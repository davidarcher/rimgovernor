package policy

import "slices"

// Circulation is a station-less room node (RoomNode) that joins the rooms of a
// colony too large to reach through each other (#2911). Nothing here is a
// judgment constant:
//
//   - Need. A private room is one that costs something to cross and is not cold
//     (RoomCrossing: sleepers, cleanliness); nobody may walk through it. Every
//     other room is a public one, a room pawns cross freely or a cold room that
//     is never crossed. Each private room needs a door onto a public room or the
//     ground. When the private rooms outnumber the public ones, no assignment of
//     one distinct public room per private room exists, so some hub carries two
//     bedroom doors (each takes a floor cell and a clear cell the hub's area did
//     not count) and the sleepers walk through the kitchen and the freezer. At or
//     below that count the colony gets no corridor: the base is small enough that
//     every room has a public neighbour of its own.
//   - Joined. The corridor is required beside every private room and every
//     public room one of them has a flow to (the table the sleepers eat at): the
//     door of each opens on the corridor, not on a hub.
//   - Size. The corridor is CirculationWidth wide. Its length is what its two
//     sides hold: each joined room takes its block's long side (blockShapes) of
//     the side plus the wall cell that separates it from the next (frontage). A corridor is
//     at most the longest strip the shell allows (shellMaxWalls); a colony with
//     more rooms to join gets more corridors.
//
// The corridor is walked by everyone, so its crossing facts are empty; the tiler
// joins sealed rooms to it first (throughRank) and opens no flow door from a
// sleeper's room to a hub while it stands (sleepersUseCirculation).

// Verdict and reasons of the circulation decision.
const (
	ClusterCirculate = "circulate"

	ReasonClusterCirculation   = "cluster_circulation"
	ReasonClusterNoCirculation = "cluster_circulation_not_needed"
)

// privateGroup reports a room nobody may walk through that is not cold: it has
// crossing costs to price (RoomCrossing.Mechanisms).
func privateGroup(g RoomGroup) bool {
	c := crossingOf(g)
	return c.Crossable() && c.Mechanisms() > 0
}

// frontage is the length of corridor side a joined group's block takes with
// the wall cell that separates it from the next: its block's long side, for
// the embedder turns a block as the site asks and a corridor sized for the
// short side runs out of side before the last room is beside it.
func frontage(g RoomGroup, depth int) int {
	s := blockShapes(g.Area+1, depth)[0] // the door onto the corridor adds a clear cell
	return int(max(s[0], s[1])) + 1
}

// addCirculation appends the circulation corridors the node asks for to groups
// and links them to the rooms they join. It returns the groups unchanged with a
// ClusterKeep decision when the private rooms do not outnumber the public ones.
func addCirculation(groups []RoomGroup, node RoomNode, depth int) ([]RoomGroup, ClusterDecision) {
	private, public := 0, 0
	for _, g := range groups {
		if privateGroup(g) {
			private++
		} else {
			public++
		}
	}
	d := ClusterDecision{A: node.ID, Verdict: ClusterKeep, Reason: ReasonClusterNoCirculation}
	if private <= public {
		return groups, d
	}
	var joined []int
	for i, g := range groups {
		if privateGroup(g) {
			joined = append(joined, i)
			continue
		}
		for _, l := range g.Links {
			if privateGroup(groups[l.To]) {
				joined = append(joined, i)
				break
			}
		}
	}
	// A strip's longest run: the shell's wall ring is 2(w+h)+4 cells.
	longest := (shellMaxWalls-4)/2 - int(node.Width)
	sides := 2
	var runs [][]int // the joined groups on each corridor
	var cur []int
	used := 0
	for _, i := range joined {
		pitch := frontage(groups[i], depth)
		if len(cur) > 0 && (used+pitch+sides-1)/sides > longest {
			runs = append(runs, cur)
			cur, used = nil, 0
		}
		cur = append(cur, i)
		used += pitch
	}
	runs = append(runs, cur)
	out := slices.Clone(groups)
	for _, run := range runs {
		sum := 0
		for _, i := range run {
			sum += frontage(groups[i], depth)
		}
		length := (sum + sides - 1) / sides
		area := length * int(node.Width)
		g := RoomGroup{ID: len(out), Footprint: 0, Clearance: area, Area: area, Planned: node.Role, Width: node.Width}
		for _, i := range run {
			g.Links = append(g.Links, GroupLink{To: i, Required: true})
			out[i].Links = append(slices.Clone(out[i].Links), GroupLink{To: g.ID, Required: true})
			// The door onto the corridor keeps a threshold clear, as every
			// required link does (AddRequiredDoors).
			out[i].Clearance++
			out[i].Area++
		}
		out = append(out, g)
	}
	for _, i := range joined {
		slices.SortFunc(out[i].Links, func(a, b GroupLink) int { return a.To - b.To })
	}
	d.Verdict, d.Reason = ClusterCirculate, ReasonClusterCirculation
	return out, d
}
