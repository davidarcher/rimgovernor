package policy

import (
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The interior objective scores a furniture layout in one region. It is the
// measure a seeded local search improves (#2869): hard constraints a layout
// must meet, then soft terms reported one by one. There is deliberately no
// scalar total here: no term has a game-derived weight against another, so
// layouts compare per term (NoWorseThan) until a search needs one and a
// DefTables entry states the judgment.
//
// Hard: pieces on region cells and overlapping nothing, doors only on the
// layout's candidate cells and each opening into the region, door thresholds
// clear, interaction cells open, all free floor reachable from the doors,
// required counts met.
//
// Soft: free floor (the game's Space stat counts open cells), facility links
// (the FacilityLink rules of the catalog: bed with end table and dresser,
// hospital bed with vitals monitor, research bench with analyzer, work bench
// with tool cabinet, each within its MaxDistance), flow-weighted walking
// distance (InteriorFlows), and beauty when the caller knows it.

// InteriorObjective is the input to ScoreInterior.
type InteriorObjective struct {
	Region Region
	// Candidates are the wall cells the layout lets a door stand on; nil
	// accepts any cell outside the region.
	Candidates []domain.Cell
	// Doors are the chosen doors, each outside the region beside a region
	// cell.
	Doors  []domain.Cell
	Pieces []InteriorPiece
	// Required is the least count of each definition the layout must hold.
	Required map[string]int
	Shapes   PieceShapes
	// Flows weights the walks between stations; nil has no walk term.
	Flows InteriorFlows
	// Beauty is the Beauty stat of each definition, from the game's defs
	// (the station catalog does not carry it yet, so it is unknown when nil
	// or when a definition is missing; unknowns are listed, not guessed).
	Beauty map[string]float64
	// Dining names the chair, which colonists walk over.
	Dining DiningFurniture
}

// FlowEnd is one end of a flow edge: a door (any chosen door) or a piece.
type FlowEnd struct {
	Door bool
	Slot string
}

// FlowEdge is a pull between two stations: Weight is how much traffic
// (trips, recipe or item flow) goes between them.
type FlowEdge struct {
	From, To FlowEnd
	Weight   float64
	// Informational marks traffic of people (meals, sleep, rest) that is
	// reported but not gated; recipe and item flows are gated.
	Informational bool
}

// InteriorFlows says which stations colonists walk between.
type InteriorFlows interface {
	Edges(pieces []InteriorPiece) []FlowEdge
}

// GraphFlows reads the flows from the station graph (#2870): each graph edge
// is the daily movements between two station nodes (ingredients store to
// stove, meals stove to table and bed to table, products to the store). A
// node's pieces in the room are those of its definition. An edge between two
// nodes both in the room is a walk between their pieces, the weight shared
// over the piece pairs. An edge whose far node has no piece in the room is
// traffic that leaves by a door, shared over the near node's pieces: a bed
// walks to a door by its meal flow, a stove to the door by the ingredients it
// fetches and the meals it carries out. No weight here is a constant: all of
// it is the graph's. Required (zero weight) links carry no traffic and add
// nothing to the walk.
//
// A bed is not a definition: one Bed def serves the colonist, the patient and
// the prisoner, and the hospital places a plain bed set medical. So a sleep
// node (any StationPurpose of sleeping) takes the room's beds by the room's
// purpose, Sleep, whatever their def (Furniture says which are beds); a
// prison cell's beds take no colonist meal flow and a bedroom's none of the
// patients'. A room with no Sleep purpose has no bed flows.
//
// An edge with a sleep end is the meal, sleep and rest traffic of people and
// is Informational: reported, not gated. The rest are recipe and item flows
// (ingredients in, products out, meals carried from the stove).
type GraphFlows struct {
	Graph StationGraph
	// Sleep is the purpose of this room's beds; empty when it holds none.
	Sleep StationPurpose
	// Furniture says which definitions are beds.
	Furniture RoomFurniture
}

// SleepPurposeOf is the sleep purpose of the beds a room role holds: the
// bedroom's are the colonists', the hospital's medical, the prison cell's the
// prisoners'. Other roles hold no bed worth a flow.
func SleepPurposeOf(role RoomRole) StationPurpose {
	switch role {
	case RoomRoleBedroom:
		return PurposeSleep
	case RoomRoleHospital:
		return PurposeSleepMed
	case RoomRolePrisonCell:
		return PurposeSleepPrison
	}
	return ""
}

func (p StationPurpose) sleeps() bool {
	switch p {
	case PurposeSleep, PurposeSleepMed, PurposeSleepPrison, PurposeSleepBaby, PurposeSleepAnimal:
		return true
	}
	return false
}

// Edges implements InteriorFlows.
func (g GraphFlows) Edges(pieces []InteriorPiece) []FlowEdge {
	slots := func(n StationNode) []string {
		var out []string
		for _, p := range pieces {
			if n.Purpose.sleeps() {
				if n.Purpose == g.Sleep && g.Furniture.IsBed(p.Def) {
					out = append(out, p.Slot)
				}
			} else if p.Def == n.Def {
				out = append(out, p.Slot)
			}
		}
		return out
	}
	var out []FlowEdge
	for _, e := range g.Graph.Edges {
		na, okA := g.Graph.Node(e.A)
		nb, okB := g.Graph.Node(e.B)
		if e.Weight <= 0 || !okA || !okB {
			continue
		}
		a, b := slots(na), slots(nb)
		info := na.Purpose.sleeps() || nb.Purpose.sleeps()
		switch {
		case len(a) > 0 && len(b) > 0:
			w := e.Weight / float64(len(a)*len(b))
			for _, from := range a {
				for _, to := range b {
					if from != to {
						out = append(out, FlowEdge{From: FlowEnd{Slot: from}, To: FlowEnd{Slot: to}, Weight: w, Informational: info})
					}
				}
			}
		case len(a) > 0 || len(b) > 0:
			near := append(a, b...)
			for _, s := range near {
				out = append(out, FlowEdge{From: FlowEnd{Door: true}, To: FlowEnd{Slot: s}, Weight: e.Weight / float64(len(near)), Informational: info})
			}
		}
	}
	return out
}

// InteriorScore is a layout's measure: Violations are the hard constraints it
// breaks, the rest the soft terms.
type InteriorScore struct {
	Violations []string
	// Cells is the region's area and FreeCells the cells no piece covers.
	Cells, FreeCells int
	// LinksWanted counts facility-serviceable targets and LinksServed those
	// with a facility in range.
	LinksWanted, LinksServed int
	// Walk is the weight-averaged walking distance in steps over the gated
	// (recipe and item) flow edges, WalkEdges of them; 0 with no edges.
	Walk      float64
	WalkEdges int
	// DoorWalk and PairWalk split Walk: the average over the edges that end
	// at a door and over those between two pieces. Both are gated.
	DoorWalk, PairWalk float64
	// InfoWalk is the same average over the informational edges (meals,
	// sleep, rest), InfoEdges of them: reported, never gated.
	InfoWalk  float64
	InfoEdges int
	// Beauty sums the known Beauty of the pieces; Unknown lists the
	// definitions whose Beauty was not known.
	Beauty  float64
	Unknown []string
	// Pieces is the count of pieces placed.
	Pieces int
}

// Feasible reports whether every hard constraint holds.
func (s InteriorScore) Feasible() bool { return len(s.Violations) == 0 }

// Free is the free floor fraction.
func (s InteriorScore) Free() float64 {
	if s.Cells == 0 {
		return 0
	}
	return float64(s.FreeCells) / float64(s.Cells)
}

// Links is the fraction of link targets served; 1 when nothing is wanted.
func (s InteriorScore) Links() float64 {
	if s.LinksWanted == 0 {
		return 1
	}
	return float64(s.LinksServed) / float64(s.LinksWanted)
}

// NoWorseThan reports whether s is at least as good as o on every soft term.
// Beauty compares only when neither side has unknowns.
func (s InteriorScore) NoWorseThan(o InteriorScore) bool {
	return len(s.WorseTerms(o)) == 0
}

// WorseTerms names the gated soft terms on which s is worse than o: free
// floor, facility links, pair walk and door walk (of recipe and item flows),
// and beauty when known. Informational walking is not compared.
func (s InteriorScore) WorseTerms(o InteriorScore) []string {
	const eps = 1e-9
	var out []string
	if s.Free() < o.Free()-eps {
		out = append(out, "free")
	}
	if s.Links() < o.Links()-eps {
		out = append(out, "links")
	}
	if s.PairWalk > o.PairWalk+eps {
		out = append(out, "pair_walk")
	}
	if s.DoorWalk > o.DoorWalk+eps {
		out = append(out, "door_walk")
	}
	if len(s.Unknown) == 0 && len(o.Unknown) == 0 && s.Beauty < o.Beauty-eps {
		out = append(out, "beauty")
	}
	return out
}

// ScoreInterior measures a layout against the objective.
func ScoreInterior(in InteriorObjective) InteriorScore {
	g := in.Region
	s := InteriorScore{Cells: g.Area(), Pieces: len(in.Pieces)}
	bad := func(format string, a ...any) { s.Violations = append(s.Violations, fmt.Sprintf(format, a...)) }

	blocked := map[domain.Cell]string{}
	counts := map[string]int{}
	for _, p := range in.Pieces {
		counts[p.Def]++
		for _, c := range rectCells(p.Rect) {
			if !g.Contains(c) {
				bad("%s: cell %v outside the region", p.Slot, c)
				continue
			}
			if other, taken := blocked[c]; taken {
				bad("%s and %s overlap at %v", other, p.Slot, c)
			}
			blocked[c] = p.Slot
		}
	}
	s.FreeCells = s.Cells - len(blocked)

	var thresholds []domain.Cell
	candidate := map[domain.Cell]bool{}
	for _, c := range in.Candidates {
		candidate[c] = true
	}
	isDoor := map[domain.Cell]bool{}
	for _, d := range in.Doors {
		isDoor[d] = true
		if in.Candidates != nil && !candidate[d] {
			bad("door %v is not a candidate door cell", d)
		}
		opens := false
		for _, n := range neighbours(d) {
			if g.Contains(n) {
				opens = true
				thresholds = append(thresholds, n)
				if p, taken := blocked[n]; taken {
					bad("%s stands inside the door at %v", p, d)
				}
			}
		}
		if g.Contains(d) || !opens {
			bad("door %v does not open into the region", d)
		}
	}
	if len(in.Doors) == 0 {
		bad("no door")
	}

	free := func(c domain.Cell) bool { _, taken := blocked[c]; return g.Contains(c) && !taken }
	var reach map[domain.Cell]int
	if len(thresholds) > 0 {
		reach = flood(free, thresholds)
		for _, c := range g.Cells() {
			if free(c) {
				if _, ok := reach[c]; !ok {
					bad("floor at %v is cut off from the doors", c)
				}
			}
		}
	}
	// Chairs are walked over, so a table ringed by its chairs is reached.
	chairs := map[string]bool{}
	for _, p := range in.Pieces {
		chairs[p.Slot] = in.Dining.IsChair(p.Def)
	}
	walkable := func(c domain.Cell) bool {
		if free(c) {
			return true
		}
		slot, taken := blocked[c]
		return taken && chairs[slot]
	}
	stand := map[string][]domain.Cell{}
	for _, p := range in.Pieces {
		if p.Clear > 0 {
			for _, c := range p.Reserves() {
				if _, taken := blocked[c]; taken || !g.Contains(c) {
					bad("%s: lane cell %v is not open floor", p.Slot, c)
				}
			}
		}
		if c, ok := p.Interaction(); ok {
			if _, taken := blocked[c]; taken || !g.Contains(c) && !isDoor[c] {
				bad("%s: interaction cell %v is not open floor", p.Slot, c)
			}
			stand[p.Slot] = []domain.Cell{c}
			continue
		}
		for _, c := range rectCells(p.Rect) {
			for _, n := range neighbours(c) {
				if walkable(n) {
					stand[p.Slot] = append(stand[p.Slot], n)
				}
			}
		}
	}
	defs := make([]string, 0, len(in.Required))
	for def := range in.Required {
		defs = append(defs, def)
	}
	sort.Strings(defs)
	for _, def := range defs {
		if counts[def] < in.Required[def] {
			bad("%s: %d of the %d required", def, counts[def], in.Required[def])
		}
	}

	s.LinksWanted, s.LinksServed = facilityLinks(in)
	s.walkDistance(in, walkable, thresholds, stand)
	s.Beauty, s.Unknown = pieceBeauty(in)
	return s
}

func neighbours(c domain.Cell) [4]domain.Cell {
	return [4]domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}}
}

// flood is the orthogonal step distance from the nearest source over free
// cells; sources that are not free are skipped.
func flood(free func(domain.Cell) bool, sources []domain.Cell) map[domain.Cell]int {
	dist := map[domain.Cell]int{}
	var queue []domain.Cell
	for _, c := range sources {
		if _, seen := dist[c]; !seen && free(c) {
			dist[c] = 0
			queue = append(queue, c)
		}
	}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		for _, n := range neighbours(c) {
			if _, seen := dist[n]; !seen && free(n) {
				dist[n] = dist[c] + 1
				queue = append(queue, n)
			}
		}
	}
	return dist
}

// walkDistance fills the walking terms: the gated average over recipe and
// item edges (with its door and pair parts) and the informational average.
func (s *InteriorScore) walkDistance(in InteriorObjective, free func(domain.Cell) bool, thresholds []domain.Cell, stand map[string][]domain.Cell) {
	flows := in.Flows
	if flows == nil {
		return
	}
	ends := func(e FlowEnd) []domain.Cell {
		if e.Door {
			return thresholds
		}
		return stand[e.Slot]
	}
	var sum, weight, doorSum, doorWeight, infoSum, infoWeight float64
	for _, e := range flows.Edges(in.Pieces) {
		from, to := ends(e.From), ends(e.To)
		if e.Weight <= 0 || len(from) == 0 || len(to) == 0 {
			continue
		}
		dist := flood(free, from)
		best, found := math.MaxInt, false
		for _, c := range to {
			if d, ok := dist[c]; ok && d < best {
				best, found = d, true
			}
		}
		if !found {
			continue
		}
		cost := e.Weight * float64(best)
		if e.Informational {
			infoSum += cost
			infoWeight += e.Weight
			s.InfoEdges++
			continue
		}
		sum += cost
		weight += e.Weight
		if e.From.Door || e.To.Door {
			doorSum += cost
			doorWeight += e.Weight
		}
		s.WalkEdges++
	}
	avg := func(s, w float64) float64 {
		if w == 0 {
			return 0
		}
		return s / w
	}
	s.Walk, s.DoorWalk, s.PairWalk = avg(sum, weight), avg(doorSum, doorWeight), avg(sum-doorSum, weight-doorWeight)
	s.InfoWalk = avg(infoSum, infoWeight)
}

func pieceBeauty(in InteriorObjective) (float64, []string) {
	var total float64
	unknown := map[string]bool{}
	for _, p := range in.Pieces {
		b, ok := in.Beauty[p.Def]
		if !ok {
			unknown[p.Def] = true
			continue
		}
		total += b
	}
	var out []string
	for def := range unknown {
		out = append(out, def)
	}
	sort.Strings(out)
	return total, out
}

// facilityRule pairs a catalog FacilityLink with the pieces it serves.
type facilityRule struct {
	link   FacilityLink
	target func(InteriorPiece) bool
}

// facilityRules reads which pieces each of the catalog's facility links
// serves: beds take the end table and dresser, hospital beds the monitor,
// research benches the analyzer and workshop benches the cabinet. The links
// themselves (distance, simultaneous, adjacency) are catalog rows.
func facilityRules(shapes PieceShapes) []facilityRule {
	f := shapes.Furniture
	family := func(role RoomRole) func(InteriorPiece) bool {
		return func(p InteriorPiece) bool {
			d, ok := shapes.Get(p.Def)
			return ok && d.Family == role
		}
	}
	bed := func(p InteriorPiece) bool { return f.IsBed(p.Def) }
	return []facilityRule{
		{f.EndTable, bed}, {f.Dresser, bed},
		{f.Monitor, family(RoomRoleHospital)},
		{f.Analyzer, family(RoomRoleLaboratory)},
		{f.Cabinet, family(RoomRoleWorkshop)},
	}
}

// facilityLinks counts the targets of every active rule and those a facility
// serves. A rule is active when the layout or its requirements hold the
// facility; a facility serves at most MaxSimultaneous targets, each target
// taking the nearest facility with capacity.
func facilityLinks(in InteriorObjective) (wanted, served int) {
	// A piece placed beside another (a chair at its table) is one link,
	// served when it faces and touches one.
	for _, p := range in.Pieces {
		if p.Beside != "" {
			wanted++
			if p.faces(in.Pieces) {
				served++
			}
		}
	}
	for _, rule := range facilityRules(in.Shapes) {
		l := rule.link
		if l.Def == "" {
			continue
		}
		var facilities, targets []InteriorPiece
		for _, p := range in.Pieces {
			if p.Def == l.Def {
				facilities = append(facilities, p)
			} else if rule.target(p) {
				targets = append(targets, p)
			}
		}
		if len(facilities) == 0 && in.Required[l.Def] == 0 {
			continue
		}
		load := make([]int32, len(facilities))
		for _, t := range targets {
			wanted++
			best, bestD := -1, 0.0
			for i, f := range facilities {
				if load[i] >= max(l.MaxSimultaneous, 1) || !linkReaches(l, f, t) {
					continue
				}
				if d := centreDistance(f.Rect, t.Rect); best < 0 || d < bestD {
					best, bestD = i, d
				}
			}
			if best >= 0 {
				load[best]++
				served++
			}
		}
	}
	return wanted, served
}

func rectCentre(r Rectangle) (float64, float64) {
	return float64(r.X) + float64(r.Width)/2, float64(r.Z) + float64(r.Height)/2
}

func centreDistance(a, b Rectangle) float64 {
	ax, az := rectCentre(a)
	bx, bz := rectCentre(b)
	return math.Hypot(ax-bx, az-bz)
}

// linkReaches reports whether facility f links target t under link l: in
// range, and when required touching the target (beside its head for a bed,
// whose anchor is the head cell).
func linkReaches(l FacilityLink, f, t InteriorPiece) bool {
	if l.MaxDistance > 0 && centreDistance(f.Rect, t.Rect) > l.MaxDistance {
		return false
	}
	if l.CardinalToHead {
		head := t.Anchor()
		for _, n := range neighbours(head) {
			if rectContains(f.Rect, n) {
				return true
			}
		}
		return false
	}
	if l.Adjacent {
		for _, c := range rectCells(f.Rect) {
			for _, n := range neighbours(c) {
				if rectContains(t.Rect, n) {
					return true
				}
			}
		}
		return false
	}
	return true
}
