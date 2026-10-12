package policy

import (
	"errors"
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Clustering turns the station graph into room groups, the seed the site
// planner places. It generalises affinityClusters (union-find over weighted
// trips) from role-keyed rooms to stations, and nothing in it is a per-role
// constant:
//
//   - Units. Every graph node is a pool of identical stations. A pool is
//     split, by halves, until each part is lossless (PredictRole keeps every
//     gameplay effect its stations are for) and fits the area limit. Two free
//     beds predict a barracks, which is not the bedroom a bed is for, so beds
//     end as one per group; four medical beds predict the same hospital as
//     one, so they stay together.
//   - Merging. Unit pairs are taken by descending affinity (the graph's flow
//     weights, a required link first) and merged unless the merged group loses
//     an effect a part had (a bench slowed by the wrong role, a bedroom that
//     reads as barracks), would join an enclosed room with an open-sky one, or
//     would not fit the area limit. A cold room beside a station that works in
//     a temperature band is not refused: the decision records the conflict
//     (Conflict) for the search to price.
//   - Intended role. What a station is for is what the game makes of it alone:
//     PredictRole of the single station (a stove is a kitchen, a bed a
//     bedroom, a medical bed a hospital).
//   - Area. A group's area is its stations' footprints (the catalog's shapes)
//     plus the clearance the placer keeps: one open cell per station (its
//     interaction cell, or the neighbour that keeps it reachable), the exterior
//     door threshold and one more per required link (AddRequiredDoors). The
//     limit is the largest room the game's roof and the
//     plan's shell bounds allow: a room is roofed only while every cell lies
//     within RoofMaxSupportDistance of a wall (domain.RoomFootprint), which
//     bounds its depth, and a shell is at most 3844 interior cells and 255 wall
//     cells.
//   - Environment. Cold, clean, enclosed and open-sky requirements come from
//     the catalog's needs of the stations in the group.
//   - Cold is met by a cooling station: a group that needs cold gets the
//     catalog's most efficient cooler (least power draw per heat moved) in its
//     station list, its footprint and one clearance cell in the area, or a
//     ClusterUnsatisfied decision when the catalog has none. Cooler power and
//     wiring are the refrigeration planner's; exposure to cold outside air is
//     the climate envelope's (#2880).
//
// The result is pure data and deterministic: ties break by node id. The caller
// journals Decisions as telemetry decisions.

// EnvRequirement is an environment a group's room must provide.
type EnvRequirement string

const (
	// EnvCold: a station stores things that rot (NeedCold).
	EnvCold EnvRequirement = "cold"
	// EnvClean: a station's effect scales with the room's cleanliness.
	EnvClean EnvRequirement = "clean"
	// EnvEnclosed: a station works only indoors (NeedIndoors).
	EnvEnclosed EnvRequirement = "enclosed"
	// EnvOpenSky: a station cannot stand under a roof (NeedOpenSky).
	EnvOpenSky EnvRequirement = "open_sky"
)

// Verdicts and reasons of clustering decisions.
const (
	ClusterMerge = "merge"
	ClusterKeep  = "keep"
	ClusterSplit = "split"
	// ClusterUnsatisfied: a group needs cold and the catalog has no cooling station.
	ClusterUnsatisfied = "unsatisfied"

	ReasonClusterAffinity    = "cluster_affinity"
	ReasonClusterLostEffect  = "cluster_lost_effect"
	ReasonClusterEnvConflict = "cluster_env_conflict"
	ReasonClusterArea        = "cluster_area"
	ReasonClusterNoCooler    = "cluster_no_cooler"

	// ConflictColdBand is recorded on a decision whose two parts are a cold
	// group and a station that works in a temperature band: cold is kept below
	// the rot-slowing temperature, under the band, so the room costs one or the
	// other. The game gives no exclusion; the search prices it (#2896).
	ConflictColdBand = "cold_vs_temperature_band"
)

// cleanlinessStat is the room stat the surgery chain ends in; the catalog's
// NeedRoomStat names it as Input.
const cleanlinessStat = "Cleanliness"

// ErrCluster marks a graph the catalog cannot cluster.
var ErrCluster = errors.New("cluster stations")

// ClusterInput is everything clustering reads.
type ClusterInput struct {
	Graph   StationGraph
	Catalog StationCatalog
	// RoofDistance is the game's RoofCollapseUtility.RoofMaxSupportDistance
	// (the catalog's roof_max_support_distance).
	RoofDistance float64
}

// GroupStation is the stations of one graph node in a group.
type GroupStation struct {
	Node    string
	Def     string
	Purpose StationPurpose
	Count   int
}

// GroupLink is the wish that two groups be neighbours; Required is a link the
// environment needs demand (cold storage beside the stove).
type GroupLink struct {
	To       int
	Weight   float64
	Required bool
}

// RoomGroup is stations that share a room.
type RoomGroup struct {
	ID       int
	Stations []GroupStation
	// Footprint is the cells the stations stand on, Clearance the open cells
	// the placer keeps, Area their sum.
	Footprint, Clearance, Area int
	// Role is the native role the game would give the room.
	Role RoomRole
	// Planned is the planned room the group's contents decide when it is not
	// the one Role gives (the butchery); empty otherwise (PlannedRoleOfGroup).
	Planned PlannedRole
	// Width is the fixed short side of the group's block, 0 for the squarest
	// (a circulation corridor is a strip).
	Width    int32
	Requires []EnvRequirement
	Links    []GroupLink
	// Costs are the throughput the room loses to the environment its stations
	// do not get; LostEffects the role effects it does not give.
	Costs       []ConflictCost
	LostEffects []LostRoleEffect
}

// ConflictCost is the work ticks a day a conflict costs the stations of one
// graph node: the node's daily work times (1/Factor - 1), Factor being the
// catalog need's rate multiplier (NeedRole x0.8 is a quarter more time;
// NeedTemperature x0.7 outside the band). Kind is NeedRole or
// NeedTemperature; Role is the role a NeedRole station wants. No other unit.
type ConflictCost struct {
	Kind        EnvNeedKind
	Node, Def   string
	Role        RoomRole
	Count       int
	Factor      float64
	TicksPerDay float64
}

// LostRoleEffect is a role effect (sleep, patient or prison mood, throne,
// worship) a room does not give, with the sleepers the graph puts in it. Its
// mood magnitude is unknown until a room is built; #2878 prices it.
type LostRoleEffect struct {
	Effect RoleEffect
	Role   RoomRole
	Pawns  int
}

// ClusterDecision is one clustering decision for the caller to journal. A is
// a graph node; B is the other node of a merge or keep.
type ClusterDecision struct {
	A, B    string
	Verdict string
	Reason  string
	Weight  float64
	// Conflict names an environment conflict between the two parts, if any; it
	// does not stop a merge.
	Conflict string
	// Costs and LostEffects are what a merge would add to its parts (a merge
	// or a keep) or what the unsplit pool loses (a split): data for the search
	// (#2877, #2878); nothing here weighs them against flow.
	Costs       []ConflictCost
	LostEffects []LostRoleEffect
}

// Clustering is the room groups and why they are what they are.
type Clustering struct {
	Groups    []RoomGroup
	Decisions []ClusterDecision
}

// Roles are the distinct predicted roles of the groups, sorted.
func (c Clustering) Roles() []RoomRole {
	var out []RoomRole
	for _, g := range c.Groups {
		if !slices.Contains(out, g.Role) {
			out = append(out, g.Role)
		}
	}
	slices.Sort(out)
	return out
}

// Shell bounds of the plan's room footprint (domain.NewRoomFootprint): at
// most 3844 interior cells and 255 wall cells.
const (
	shellMaxInterior = 3844
	shellMaxWalls    = 255
)

// maxRoomArea is the largest interior area a roofed room can have. A
// rectangle is roofed while its centre lies within roofDistance of a wall, so
// its depth is bounded and its length is bounded by the shell: a w by h
// interior has a wall ring of 2(w+h)+4 cells.
func maxRoomArea(roofDistance float64) (int, error) {
	_, area, err := roomLimits(roofDistance)
	return area, err
}

// roomLimits is the deepest roof-supported square interior and the largest
// roof-supported interior area.
func roomLimits(roofDistance float64) (depth, area int, err error) {
	if roofDistance <= 0 {
		return 0, 0, fmt.Errorf("%w: roof support distance %v", ErrCluster, roofDistance)
	}
	rect := func(w, h int) (domain.RoomFootprint, error) {
		cells := make([]domain.Cell, 0, w*h)
		for z := 1; z <= h; z++ {
			for x := 1; x <= w; x++ {
				cells = append(cells, domain.Cell{X: int32(x), Z: int32(z)})
			}
		}
		return domain.NewRoomFootprint(cells, domain.Cell{X: 1, Z: 0}, domain.South)
	}
	for s := 1; s*s <= shellMaxInterior; s++ {
		f, err := rect(s, s)
		if err != nil || !f.RoofSupported(roofDistance) {
			break
		}
		depth = s
	}
	if depth == 0 {
		return 0, 0, fmt.Errorf("%w: no room is roof supported at distance %v", ErrCluster, roofDistance)
	}
	length := min((shellMaxWalls-4)/2-depth, shellMaxInterior/depth)
	f, err := rect(length, depth)
	if err != nil || !f.RoofSupported(roofDistance) {
		return 0, 0, fmt.Errorf("%w: a %dx%d room is not buildable: %v", ErrCluster, length, depth, err)
	}
	return depth, length * depth, nil
}

// unit is count stations of one graph node.
type unit struct{ node, count int }

type clusterer struct {
	in      ClusterInput
	maxArea int
	// cooler is the cooling station a cold group carries, coolerSize its
	// footprint in cells; hasCooler is false when the catalog has none.
	cooler     string
	coolerSize int
	hasCooler  bool
}

// groupEval is what clustering knows of a candidate group.
type groupEval struct {
	lost      map[string]bool
	footprint int
	clearance int
	area      int
	cold      bool
	band      bool
	enclosed  bool
	openSky   bool
	clean     bool
	// filth: a node's stations spawn filth in the room (StationNode.Filth); closet: every node
	// of the group does.
	filth, closet bool
	role          RoomRole
	costs         []ConflictCost
	effects       []LostRoleEffect
}

func (c *clusterer) node(u unit) StationNode { return c.in.Graph.Nodes[u.node] }

// pieceState is the instance state of a node's stations: the bed state the
// purpose gives a humanlike bed.
func pieceState(purpose StationPurpose, st Station) RoleCondition {
	if !humanlikeBed(st) {
		return CondAlways
	}
	switch purpose {
	case PurposeSleepMed:
		return CondBedMedical
	case PurposeSleepPrison:
		return CondBedPrisoner
	}
	return CondBedFree
}

func (c *clusterer) pieces(us []unit) []RoomPiece {
	var out []RoomPiece
	for _, u := range us {
		n := c.node(u)
		st := c.in.Catalog.Stations[n.Def]
		for range u.count {
			out = append(out, RoomPiece{Def: n.Def, State: pieceState(n.Purpose, st)})
		}
	}
	return out
}

func (c *clusterer) eval(us []unit) (groupEval, error) {
	pieces := c.pieces(us)
	pred, err := PredictRole(c.in.Catalog, pieces)
	if err != nil {
		return groupEval{}, err
	}
	e := groupEval{lost: map[string]bool{}, role: pred.Role}
	var intended []RoomRole
	sleepers := map[RoomRole]int{} // sleepers whose bed is for the role
	for _, p := range pieces {
		alone, err := PredictRole(c.in.Catalog, []RoomPiece{p})
		if err != nil {
			return groupEval{}, err
		}
		if !slices.Contains(intended, alone.Role) {
			intended = append(intended, alone.Role)
		}
		if cp := c.in.Catalog.Stations[p.Def].Capacity; cp.Kind == CapacitySleepers {
			sleepers[alone.Role] += cp.Slots
		}
	}
	for _, role := range intended {
		lost, err := LostEffects(c.in.Catalog, role, pieces)
		if err != nil {
			return groupEval{}, err
		}
		for _, l := range lost {
			key := fmt.Sprintf("%s|%s|%s", l.Effect, l.Role, l.Def)
			if !e.lost[key] && l.Effect != EffectBenchSpeed {
				e.effects = append(e.effects, LostRoleEffect{Effect: l.Effect, Role: l.Role, Pawns: sleepers[l.Role]})
			}
			e.lost[key] = true
		}
	}
	e.closet = len(us) > 0
	for _, u := range us {
		n := c.node(u)
		size := c.in.Catalog.Stations[n.Def].Shape.Size
		e.filth = e.filth || n.Filth
		e.closet = e.closet && n.Filth
		e.footprint += u.count * int(size.X) * int(size.Z)
		e.clearance += u.count
		for _, need := range n.Needs {
			switch need.Kind {
			case NeedCold:
				e.cold = true
			case NeedTemperature:
				e.band = true
			case NeedIndoors:
				e.enclosed = true
			case NeedOpenSky:
				e.openSky = true
			case NeedRoomStat:
				e.clean = e.clean || need.Input == cleanlinessStat
			}
		}
	}
	e.costs = c.costs(us, e)
	if e.cold && c.hasCooler {
		e.footprint += c.coolerSize
		e.clearance++
	}
	e.clearance++ // the door threshold
	e.area = e.footprint + e.clearance
	return e, nil
}

func (e groupEval) requires() []EnvRequirement {
	var out []EnvRequirement
	for _, r := range []struct {
		on  bool
		req EnvRequirement
	}{{e.clean, EnvClean}, {e.cold, EnvCold}, {e.enclosed, EnvEnclosed}, {e.openSky, EnvOpenSky}} {
		if r.on {
			out = append(out, r.req)
		}
	}
	return out
}

// conflict reports two groups whose environments exclude each other: a station
// under an open sky cannot share a roofed room, and a station whose work spawns
// filth shares its room with nothing but other such stations (the filth lowers
// the cleanliness every station and sleeper of the room is read at, a cook's
// food-poisoning chance among them), so it stands in a closet of its own. Cold
// against a temperature band is not an exclusion (coldBandConflict).
func conflict(a, b groupEval) bool {
	return a.enclosed && b.openSky || a.openSky && b.enclosed || (a.filth || b.filth) && !(a.closet && b.closet)
}

// plannedRole is the planned room a group's contents decide over what the
// game predicts: a closet of filth stations the game reads as a kitchen (the
// butcher table scores as one) is the butchery. Empty when the prediction stands.
func (e groupEval) plannedRole() PlannedRole {
	if e.closet && e.role == RoomRoleKitchen {
		return PlannedButchery
	}
	return ""
}

// coldBandConflict reports a cold group beside a station that works in a
// temperature band.
func coldBandConflict(a, b groupEval) bool { return a.cold && b.band || a.band && b.cold }

// ClusterStations groups the stations of in.Graph into rooms.
func ClusterStations(in ClusterInput) (Clustering, error) {
	maxArea, err := maxRoomArea(in.RoofDistance)
	if err != nil {
		return Clustering{}, err
	}
	c := &clusterer{in: in, maxArea: maxArea}
	c.cooler, c.hasCooler = bestCooler(in.Catalog)
	if c.hasCooler {
		size := in.Catalog.Stations[c.cooler].Shape.Size
		c.coolerSize = int(size.X) * int(size.Z)
	}
	for _, n := range in.Graph.Nodes {
		st, ok := in.Catalog.Stations[n.Def]
		if !ok || st.Shape.Size.X <= 0 || st.Shape.Size.Z <= 0 {
			return Clustering{}, fmt.Errorf("%w: node %s has no station shape in the catalog", ErrCluster, n.ID)
		}
	}
	var out Clustering
	var units []unit
	chunks := map[int][]int{} // node index to its unit indexes
	for i, n := range in.Graph.Nodes {
		if n.Count <= 0 {
			continue
		}
		parts, err := c.split(i, n.Count, &out.Decisions)
		if err != nil {
			return Clustering{}, err
		}
		for _, p := range parts {
			chunks[i] = append(chunks[i], len(units))
			units = append(units, p)
		}
	}
	index := map[string]int{}
	for i, n := range in.Graph.Nodes {
		index[n.ID] = i
	}
	type pair struct {
		a, b     int
		weight   float64
		required bool
		na, nb   string
	}
	var pairs []pair
	for _, e := range in.Graph.Edges {
		ia, okA := index[e.A]
		ib, okB := index[e.B]
		if !okA || !okB {
			continue
		}
		totA, totB := float64(in.Graph.Nodes[ia].Count), float64(in.Graph.Nodes[ib].Count)
		for _, ua := range chunks[ia] {
			for _, ub := range chunks[ib] {
				share := float64(units[ua].count) * float64(units[ub].count) / (totA * totB)
				pairs = append(pairs, pair{ua, ub, e.Weight * share, e.Required, e.A, e.B})
			}
		}
	}
	sort.SliceStable(pairs, func(i, j int) bool {
		p, q := pairs[i], pairs[j]
		if p.required != q.required {
			return p.required
		}
		if p.weight != q.weight {
			return p.weight > q.weight
		}
		if p.a != q.a {
			return p.a < q.a
		}
		return p.b < q.b
	})
	parent := make([]int, len(units))
	members := make([][]unit, len(units))
	for i := range units {
		parent[i] = i
		members[i] = []unit{units[i]}
	}
	var find func(int) int
	find = func(i int) int {
		if parent[i] != i {
			parent[i] = find(parent[i])
		}
		return parent[i]
	}
	note := func(d ClusterDecision) {
		if n := len(out.Decisions); n > 0 && out.Decisions[n-1].A == d.A && out.Decisions[n-1].B == d.B &&
			out.Decisions[n-1].Verdict == d.Verdict && out.Decisions[n-1].Reason == d.Reason {
			return
		}
		out.Decisions = append(out.Decisions, d)
	}
	for _, p := range pairs {
		ra, rb := find(p.a), find(p.b)
		if ra == rb {
			continue
		}
		ea, err := c.eval(members[ra])
		if err != nil {
			return Clustering{}, err
		}
		eb, err := c.eval(members[rb])
		if err != nil {
			return Clustering{}, err
		}
		merged := append(slices.Clone(members[ra]), members[rb]...)
		em, err := c.eval(merged)
		if err != nil {
			return Clustering{}, err
		}
		d := ClusterDecision{A: p.na, B: p.nb, Verdict: ClusterKeep, Weight: p.weight,
			Costs: costsAdded(em.costs, ea.costs, eb.costs), LostEffects: effectsAdded(em.effects, ea.effects, eb.effects)}
		if coldBandConflict(ea, eb) {
			d.Conflict = ConflictColdBand
		}
		switch {
		case conflict(ea, eb):
			d.Reason = ReasonClusterEnvConflict
		case em.area > c.maxArea:
			d.Reason = ReasonClusterArea
		case newLoss(em, ea, eb):
			d.Reason = ReasonClusterLostEffect
		default:
			d.Verdict, d.Reason = ClusterMerge, ReasonClusterAffinity
			lo, hi := min(ra, rb), max(ra, rb)
			parent[hi] = lo
			members[lo] = merged
			members[hi] = nil
		}
		note(d)
	}
	var roots []int
	for i := range units {
		if find(i) == i {
			roots = append(roots, i)
		}
	}
	rootGroup := map[int]int{}
	for gi, r := range roots {
		rootGroup[r] = gi
	}
	for gi, r := range roots {
		e, err := c.eval(members[r])
		if err != nil {
			return Clustering{}, err
		}
		g := RoomGroup{ID: gi, Footprint: e.footprint, Clearance: e.clearance, Area: e.area, Role: e.role, Planned: e.plannedRole(), Requires: e.requires(), Costs: e.costs, LostEffects: e.effects}
		byNode := map[int]int{}
		for _, m := range members[r] {
			byNode[m.node] += m.count
		}
		nodes := make([]int, 0, len(byNode))
		for n := range byNode {
			nodes = append(nodes, n)
		}
		slices.Sort(nodes)
		for _, n := range nodes {
			node := in.Graph.Nodes[n]
			g.Stations = append(g.Stations, GroupStation{Node: node.ID, Def: node.Def, Purpose: node.Purpose, Count: byNode[n]})
		}
		if e.cold {
			if c.hasCooler {
				g.Stations = append(g.Stations, GroupStation{Node: "cooler", Def: c.cooler, Purpose: PurposeCool, Count: 1})
			} else {
				out.Decisions = append(out.Decisions, ClusterDecision{A: g.Stations[0].Node, Verdict: ClusterUnsatisfied, Reason: ReasonClusterNoCooler})
			}
		}
		out.Groups = append(out.Groups, g)
	}
	links := map[[2]int]*GroupLink{}
	for _, p := range pairs {
		ga, gb := rootGroup[find(p.a)], rootGroup[find(p.b)]
		if ga == gb {
			continue
		}
		for _, k := range [][2]int{{ga, gb}, {gb, ga}} {
			l := links[k]
			if l == nil {
				l = &GroupLink{To: k[1]}
				links[k] = l
			}
			l.Weight += p.weight
			l.Required = l.Required || p.required
		}
	}
	for k, l := range links {
		out.Groups[k[0]].Links = append(out.Groups[k[0]].Links, *l)
	}
	for i := range out.Groups {
		sort.Slice(out.Groups[i].Links, func(a, b int) bool { return out.Groups[i].Links[a].To < out.Groups[i].Links[b].To })
		out.Groups[i].AddRequiredDoors()
	}
	depth, _, err := roomLimits(in.RoofDistance)
	if err != nil {
		return Clustering{}, err
	}
	for _, r := range in.Graph.Rooms {
		if !r.Joins {
			continue
		}
		var d ClusterDecision
		out.Groups, d = addCirculation(out.Groups, r, depth)
		out.Decisions = append(out.Decisions, d)
	}
	return out, nil
}

// AddRequiredDoors counts the door each required link opens: tiling opens a
// door on every adjacent required pair (layout_tile.go), and a door keeps the
// cell inside it clear, so the group's floor grows by one threshold per
// required link beyond the exterior door eval counted. Other links are wishes;
// a door for one opens only where the room still furnishes. The merge area
// check runs before the links exist, so a merged group can pass it and exceed
// the limit by its required doors.
func (g *RoomGroup) AddRequiredDoors() {
	for _, l := range g.Links {
		if l.Required {
			g.Clearance++
			g.Area++
		}
	}
}

// costs are the work ticks a day the units' stations lose in a room of
// evaluation e: a bench whose NeedRole names another role than the room's, and
// a station with a temperature band in a room kept cold. A unit holds its
// share of the node's daily work (count over the node's count); a need whose
// Factor is not a slowdown costs nothing.
func (c *clusterer) costs(us []unit, e groupEval) []ConflictCost {
	var out []ConflictCost
	for _, u := range us {
		n := c.node(u)
		if n.DailyWork <= 0 || n.Count <= 0 {
			continue
		}
		for _, need := range n.Needs {
			if need.Factor <= 0 || need.Factor >= 1 {
				continue
			}
			var role RoomRole
			switch need.Kind {
			case NeedRole:
				if need.Role == e.role {
					continue
				}
				role = need.Role
			case NeedTemperature:
				if !e.cold {
					continue
				}
			default:
				continue
			}
			ticks := n.DailyWork * float64(u.count) / float64(n.Count) * (1/need.Factor - 1)
			if i := slices.IndexFunc(out, func(x ConflictCost) bool { return x.Kind == need.Kind && x.Node == n.ID && x.Role == role }); i >= 0 {
				out[i].Count += u.count
				out[i].TicksPerDay += ticks
				continue
			}
			out = append(out, ConflictCost{Kind: need.Kind, Node: n.ID, Def: n.Def, Role: role, Count: u.count, Factor: need.Factor, TicksPerDay: ticks})
		}
	}
	sort.Slice(out, func(i, j int) bool {
		a, b := out[i], out[j]
		if a.Node != b.Node {
			return a.Node < b.Node
		}
		if a.Kind != b.Kind {
			return a.Kind < b.Kind
		}
		return a.Role < b.Role
	})
	return out
}

// costsAdded are the costs a merged room has beyond its two parts': per cost
// key, the merged ticks less the parts'.
func costsAdded(merged, a, b []ConflictCost) []ConflictCost {
	var out []ConflictCost
	for _, m := range merged {
		ticks := m.TicksPerDay
		for _, part := range [][]ConflictCost{a, b} {
			for _, p := range part {
				if p.Kind == m.Kind && p.Node == m.Node && p.Role == m.Role {
					ticks -= p.TicksPerDay
				}
			}
		}
		if ticks > 1e-9 {
			m.TicksPerDay = ticks
			out = append(out, m)
		}
	}
	return out
}

// effectsAdded are the lost role effects of a merged room that neither part
// lost.
func effectsAdded(merged, a, b []LostRoleEffect) []LostRoleEffect {
	var out []LostRoleEffect
	for _, m := range merged {
		known := func(list []LostRoleEffect) bool {
			return slices.ContainsFunc(list, func(x LostRoleEffect) bool { return x.Effect == m.Effect && x.Role == m.Role })
		}
		if !known(a) && !known(b) {
			out = append(out, m)
		}
	}
	return out
}

// newLoss reports a merged group that loses an effect neither part lost.
func newLoss(merged, a, b groupEval) bool {
	for k := range merged.lost {
		if !a.lost[k] && !b.lost[k] {
			return true
		}
	}
	return false
}

// split divides a pool of count stations of node n by halves until every part
// is lossless and fits the area limit; a single station is never split.
func (c *clusterer) split(n, count int, decisions *[]ClusterDecision) ([]unit, error) {
	u := unit{node: n, count: count}
	e, err := c.eval([]unit{u})
	if err != nil {
		return nil, err
	}
	reason := ""
	switch {
	case len(e.lost) > 0:
		reason = ReasonClusterLostEffect
	case e.area > c.maxArea:
		reason = ReasonClusterArea
	}
	if reason == "" || count < 2 {
		return []unit{u}, nil
	}
	*decisions = append(*decisions, ClusterDecision{A: c.node(u).ID, Verdict: ClusterSplit, Reason: reason, Costs: e.costs, LostEffects: e.effects})
	left, err := c.split(n, (count+1)/2, decisions)
	if err != nil {
		return nil, err
	}
	right, err := c.split(n, count/2, decisions)
	if err != nil {
		return nil, err
	}
	return append(left, right...), nil
}

// bestCooler is the catalog's cooling station that draws the least power per
// heat moved (the defs' energyPerSecond and power comp), by name when equal.
func bestCooler(cat StationCatalog) (string, bool) {
	best, bestRatio := "", 0.0
	for _, def := range cat.Coolers() {
		st := cat.Stations[def]
		if st.Shape.Size.X <= 0 || st.Shape.Size.Z <= 0 {
			continue
		}
		if ratio := st.Cooling.PowerDraw / st.Cooling.HeatPerSecond; best == "" || ratio < bestRatio {
			best, bestRatio = def, ratio
		}
	}
	return best, best != ""
}
