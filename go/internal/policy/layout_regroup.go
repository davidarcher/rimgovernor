package policy

import (
	"fmt"
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Regrouping (#2878). ClusterStations is the conservative seed: it merges the
// stations that flow together unless the merged room loses an effect, and
// splits nothing it can keep. The embedding (layout_embed.go) moves blocks of a
// fixed group set and cannot change the groups, so the search over the groups
// is an outer loop around it: a move is proposed, the stations are clustered
// again with a per-pair rule (ClusterInput.Pairs, replacing the old global
// gate), the groups are embedded, and the move is kept when the priced total
// falls. The tiling (TileEmbedding) runs once, on the winner.
//
// Moves, each a set of pair rules:
//
//   - merge: two nodes the seed kept apart for a lost bench speed (a bench
//     slowed by the wrong role) share a room, priced by the throughput the
//     merged room loses. A merge that would lose a role effect (a bed with a
//     bench: PredictRole no longer gives the bedroom) is never proposed, and
//     neither are the physical exclusions (open sky against a roof, the area
//     limit); the filth merge is priced below.
//   - split: two nodes of a room that pays a conflict (a band station in a cold
//     room, a lost role effect, a role the game disproved) stand in rooms of their
//     own.
//   - move-a-need: a station class moves from the room that slows it to a room
//     of the role it wants (NeedRole), or out of a cold room (NeedTemperature).
//
// The price is one total in the embedding's score points (planWeights), no new
// constant:
//
//	embedding cost   - Score.Total(): soil, edge, centre, walk, switching, required links
//	throughput       RoomGroup.Costs ticks a day -> work-days (the day) -> points
//	lost role effect LostRoleEffect -> mood points -> break risk (PriceMood) -> work-days -> points
//	role guard       a room the game reads as another role than PredictRole gives
//	                 loses the effect of the predicted role
//
// Work-days become points through the walk, the one place the score already
// prices a movement: planWeights.Walk is a point per cell a movement walks, and a
// cell costs a colonist WorkPrice.CellWalkDays of a work-day (move speed), so a
// work-day is Walk / CellWalkDays points. The colonist's food-upkeep work-day
// (#2945, WorkPrice.WorkDayW) is the unit PriceMood returns its watts in and
// cancels from the points. A lost effect whose mood is unknown (no break model,
// no mood census, an unknown thought) is not priced: it is counted and breaks
// ties between equal totals, fewer first. Nothing here vetoes: a pair stays apart
// only because its total does not fall.
//
// A trial ranks by what it leaves undone (a group with no ground, a required link
// not adjacent), then by the total, then by the lost effects; ties keep the
// current grouping. The moves are listed in a fixed order and sampled with the
// site's seeded stream when more than regroupProposals are open, so the result
// is the same at any thread count (Embed is).

// Regroup move kinds and verdicts.
const (
	RegroupMerge = "merge"
	RegroupSplit = "split"
	RegroupMove  = "move"

	RegroupApplied = "applied"
	RegroupKept    = "kept"
	RegroupSkipped = "skipped"

	ReasonRegroupMerge          = "regroup_merge_total_falls"
	ReasonRegroupSplitConflict  = "regroup_split_conflict_priced"
	ReasonRegroupSplitRoleGuard = "regroup_split_role_guard"
	ReasonRegroupMove           = "regroup_move_need_total_falls"
	ReasonRegroupNoGain         = "regroup_no_move_improves"
	ReasonRegroupUnpriced       = "regroup_work_price_unknown"
	// ReasonRegroupFilthMerge: a butcher joined a room of cooks because the walk it
	// saves outweighs the poisoning it costs; ReasonRegroupFilthKept: it stayed a
	// closet because the poisoning (Poison) costs more than the walk saved (Walk).
	ReasonRegroupFilthMerge = "regroup_filth_merge_walk_beats_poison"
	ReasonRegroupFilthKept  = "regroup_filth_merge_poison_beats_walk"
)

// The search budget, as siteSearchTop / siteSearchIters bound the embedding's:
// moves tried a round and rounds, each trial one clustering and one embedding.
const (
	regroupProposals = siteSearchTop
	regroupRounds    = siteSearchTop / 2
)

// WorkPrice is a colonist's time in the layout's units: WorkDayW the watts a
// work-day is worth (the food upkeep, #2945) and CellWalkDays the work-days a
// colonist spends walking one cell.
type WorkPrice struct{ WorkDayW, CellWalkDays float64 }

// Valid reports a price with both numbers known.
func (p WorkPrice) Valid() bool { return finitePositive(p.WorkDayW) && finitePositive(p.CellWalkDays) }

// Points is a work-day count in score points: a point is a cell walked by a
// movement (planWeights.Walk), a cell a CellWalkDays of a work-day.
func (p WorkPrice) Points(workDays float64) float64 {
	return float64(planWeights.Walk) * workDays / p.CellWalkDays
}

// StationWorkPrice prices a colonist's time from the station facts: the lamp
// price's own work-day (stationWorkDayW) and the walk of a cell at the game's
// ticks a second and a colonist's move speed. Invalid while the food census, the
// colonists, a crop or the game's tick rate is unknown.
func StationWorkPrice(f StationFacts) WorkPrice {
	ticks := f.Catalog.Climate.TicksPerSecond
	if !finitePositive(ticks) {
		return WorkPrice{}
	}
	p := WorkPrice{WorkDayW: stationWorkDayW(f), CellWalkDays: ticks / colonistMoveSpeed / domain.TicksPerDay}
	if !p.Valid() {
		return WorkPrice{}
	}
	return p
}

// RegroupPrice is what the conflicts are priced with. Expected is the colonists'
// mean mood level, 0 to 1; unknown leaves the lost role effects unpriced.
type RegroupPrice struct {
	Work     WorkPrice
	Mood     MoodModel
	Expected domain.Fact[float64]
}

// effectPoints is a lost role effect in score points: its mood points taken from
// each pawn at the planned room's stage, as break risk. 0 while the mood is not
// priced.
func (p RegroupPrice) effectPoints(l LostRoleEffect) float64 {
	level, known := p.Expected.Value()
	lost, ok := p.Mood.LostPoints(l, moodGame.PlannedStage)
	if !known || !ok {
		return 0
	}
	price, ok := PriceMood(MoodPriceInput{Model: p.Mood.Break, WorkDayW: p.Work.WorkDayW, Points: lost, Pawns: l.Pawns, ExpectedMood: level})
	if !ok {
		return 0
	}
	return p.Work.Points(price.WorkDays)
}

// group is the conflicts of one room in points, and the lost effects it counts
// (the guard's included).
func (p RegroupPrice) group(g RoomGroup, guard []LostRoleEffect) (points float64, lost int) {
	ticks := 0.0
	for _, c := range g.Costs {
		ticks += c.TicksPerDay
	}
	points = p.Work.Points(ticks / domain.TicksPerDay)
	for _, l := range slices.Concat(g.LostEffects, guard) {
		points += p.effectPoints(l)
		lost++
	}
	return points, lost
}

// filth is what a room's filth nodes cost it, in points, through the same
// work-day to points rate as every other conflict: the food poisoning (#2955:
// FilthCost.PoisonWorkDays, the hediff's work-days per expected poisoning of the
// meals made while the filth stands) and the cleaning labor (#2988:
// FilthCost.CleaningWorkDays).
func (p RegroupPrice) filth(g RoomGroup) (poison, clean float64) {
	var poisonDays, cleanDays float64
	for _, f := range g.Filth {
		poisonDays += f.PoisonWorkDays
		cleanDays += f.CleaningWorkDays
	}
	return p.Work.Points(poisonDays), p.Work.Points(cleanDays)
}

// RegroupInput is the chain's input up to the embedding. Embed.Groups and
// Embed.Fields are set per trial.
type RegroupInput struct {
	Cluster ClusterInput
	Embed   EmbedInput
	Price   RegroupPrice
	// Mismatches are the standing rooms whose native role is not the one
	// PredictRole gives their contents (GuardNativeRoles).
	Mismatches []RoleMismatch
}

// RegroupDecision is one regrouping decision for the caller to journal: a move
// applied, or why none was. A and B are the nodes of the move's pair rule.
type RegroupDecision struct {
	Kind, Verdict, Reason string
	A, B                  string
	// Before and After are the total (score points, lower is better) of the
	// grouping before and after the move; Lost the lost role effects after it.
	Before, After float64
	Lost          int
	// Walk, Poison and Clean are the move's parts of Before to After, in score
	// points: the change of the embedding's score (the walk it saves is
	// negative), the poisoning price it adds (#2955) and the cleaning labor it
	// adds (#2988); the rest is the conflicts' ticks and lost effects.
	Walk, Poison, Clean float64
}

// Regrouping is the groups the search ended on.
type Regrouping struct {
	Clustering Clustering
	// Embed is the embedding's input over those groups, Embedding its result.
	Embed     EmbedInput
	Embedding Embedding
	// Pairs are the rules that make the grouping (empty for the seed).
	Pairs     []NodePair
	Decisions []RegroupDecision
}

// regroupTrial is one grouping, clustered, embedded and priced.
type regroupTrial struct {
	pairs      []NodePair
	clustering Clustering
	embed      EmbedInput
	embedding  Embedding
	failed     int
	points     float64
	embedPts   float64 // the embedding's part of points
	poisonPts  float64 // the poisoning price's part of points
	cleanPts   float64 // the cleaning labor's part of points
	lost       int
	guarded    map[int]bool // groups that carry a role-guard cost
}

// better ranks trials: fewer undone, a lower total, fewer lost effects.
func (t *regroupTrial) better(o *regroupTrial) bool {
	if t.failed != o.failed {
		return t.failed < o.failed
	}
	if math.Abs(t.points-o.points) > 1e-9 {
		return t.points < o.points
	}
	return t.lost < o.lost
}

// signature names the grouping: each room's nodes and counts.
func (t *regroupTrial) signature() string {
	rooms := make([]string, 0, len(t.clustering.Groups))
	for _, g := range t.clustering.Groups {
		parts := make([]string, 0, len(g.Stations))
		for _, s := range g.Stations {
			parts = append(parts, fmt.Sprintf("%s:%d", s.Node, s.Count))
		}
		rooms = append(rooms, strings.Join(parts, ","))
	}
	slices.Sort(rooms)
	return strings.Join(rooms, "|")
}

type regroupMove struct {
	kind, reason string
	a, b         string
	rules        []NodePair
}

func (r *regrouper) evaluate(pairs []NodePair) (*regroupTrial, error) {
	ci := r.in.Cluster
	ci.Pairs = pairs
	cl, err := ClusterStations(ci)
	if err != nil {
		return nil, err
	}
	in := r.in.Embed
	in.Groups, in.Fields = cl.Groups, FieldEmbedLinks(ci.Graph, cl.Groups)
	emb, err := Embed(in)
	if err != nil {
		return nil, err
	}
	t := &regroupTrial{pairs: pairs, clustering: cl, embed: in, embedding: emb, guarded: map[int]bool{}}
	t.failed = len(emb.Unplaceable()) + emb.Score.UnmetLinks
	t.embedPts = -float64(emb.Score.Total())
	t.points = t.embedPts
	guard := r.guardEffects(cl.Groups)
	for _, g := range cl.Groups {
		points, lost := r.in.Price.group(g, guard[g.ID])
		poison, clean := r.in.Price.filth(g)
		t.points += points + poison + clean
		t.poisonPts += poison
		t.cleanPts += clean
		t.lost += lost
		t.guarded[g.ID] = len(guard[g.ID]) > 0
	}
	return t, nil
}

type regrouper struct{ in RegroupInput }

// mixed reports a room whose stations are for more than one role (each
// station's role alone): the room PredictRole has to weigh, so the one a mismatch
// can be blamed on. A room of one role is the same prediction however it is cut.
func (r *regrouper) mixed(g RoomGroup) bool {
	cat := r.in.Cluster.Catalog
	var roles []RoomRole
	for _, s := range g.Stations {
		if s.Purpose == PurposeCool {
			continue
		}
		pred, err := PredictRole(cat, []RoomPiece{{Def: s.Def, State: pieceState(s.Purpose, cat.Stations[s.Def])}})
		if err != nil {
			return false
		}
		if !slices.Contains(roles, pred.Role) {
			roles = append(roles, pred.Role)
		}
	}
	return len(roles) > 1
}

// guardEffects are, per group, the effect of the role the group predicts when
// the game disproved it for the built room the group is matched to: the room
// stands, and its stations are read as the native role. Only a mixed room
// carries it, since cutting a room of one role cannot change what is predicted.
func (r *regrouper) guardEffects(groups []RoomGroup) map[int][]LostRoleEffect {
	if len(r.in.Mismatches) == 0 {
		return nil
	}
	out := map[int][]LostRoleEffect{}
	for _, m := range MatchGroupRooms(groups, r.in.Embed.Built) {
		g := groups[m.Group]
		for _, mm := range r.in.Mismatches {
			if g.Role != mm.Predicted || !slices.Contains(m.Rooms, mm.Room) || !r.mixed(g) {
				continue
			}
			out[g.ID] = append(out[g.ID], LostRoleEffect{Effect: roleEffects[mm.Predicted], Role: mm.Predicted, Got: mm.Native, Pawns: r.sleepers(g)})
		}
	}
	return out
}

// sleepers are the pawns the group's beds sleep.
func (r *regrouper) sleepers(g RoomGroup) int {
	n := 0
	for _, s := range g.Stations {
		if c := r.in.Cluster.Catalog.Stations[s.Def].Capacity; c.Kind == CapacitySleepers {
			n += c.Slots * s.Count
		}
	}
	return n
}

// Regroup searches the room groups: the seed clustering, then the best move
// that lowers the priced total, until none does or the budget is spent. The
// price must be known (Price.Work.Valid); without it the seed stands, with a
// skipped decision.
func Regroup(in RegroupInput) (Regrouping, error) {
	r := &regrouper{in: in}
	cur, err := r.evaluate(withRules(nil, in.Cluster.Pairs))
	if err != nil {
		return Regrouping{}, err
	}
	var decisions []RegroupDecision
	if !in.Price.Work.Valid() {
		decisions = append(decisions, RegroupDecision{Verdict: RegroupSkipped, Reason: ReasonRegroupUnpriced})
		return cur.regrouping(decisions), nil
	}
	rng := newSearchRand(cur.embedding.Seed)
	refused := map[[2]string]RegroupDecision{} // filth merges priced higher than the closet, by pair
	for range regroupRounds {
		cands := r.candidates(cur)
		var best *regroupTrial
		var bestMove regroupMove
		for _, m := range sample(cands, regroupProposals, &rng) {
			t, err := r.evaluate(withRules(cur.pairs, m.rules))
			if err != nil {
				return Regrouping{}, err
			}
			if m.reason == ReasonRegroupFilthMerge && t.signature() != cur.signature() && !t.better(cur) {
				refused[pairKey(m.a, m.b)] = moveDecision(m, cur, t, RegroupKept, ReasonRegroupFilthKept)
			}
			if t.signature() == cur.signature() || !t.better(cur) || best != nil && !t.better(best) {
				continue
			}
			best, bestMove = t, m
		}
		if best == nil {
			break
		}
		reason := bestMove.reason
		if bestMove.kind == RegroupSplit && r.guardedPair(cur, bestMove) {
			reason = ReasonRegroupSplitRoleGuard
		}
		decisions = append(decisions, moveDecision(bestMove, cur, best, RegroupApplied, reason))
		delete(refused, pairKey(bestMove.a, bestMove.b))
		cur = best
	}
	keys := make([][2]string, 0, len(refused))
	for k := range refused {
		keys = append(keys, k)
	}
	slices.SortFunc(keys, func(a, b [2]string) int { return strings.Compare(a[0]+"|"+a[1], b[0]+"|"+b[1]) })
	for _, k := range keys {
		if ruleOf(cur.pairs, k[0], k[1]) == 0 {
			decisions = append(decisions, refused[k])
		}
	}
	if len(decisions) == 0 {
		decisions = append(decisions, RegroupDecision{Verdict: RegroupKept, Reason: ReasonRegroupNoGain, Before: cur.points, After: cur.points, Lost: cur.lost})
	}
	return cur.regrouping(decisions), nil
}

// moveDecision is the move m taking the grouping from → to.
func moveDecision(m regroupMove, from, to *regroupTrial, verdict, reason string) RegroupDecision {
	return RegroupDecision{Kind: m.kind, Verdict: verdict, Reason: reason, A: m.a, B: m.b, Before: from.points, After: to.points, Lost: to.lost,
		Walk: to.embedPts - from.embedPts, Poison: to.poisonPts - from.poisonPts, Clean: to.cleanPts - from.cleanPts}
}

func (t *regroupTrial) regrouping(decisions []RegroupDecision) Regrouping {
	return Regrouping{Clustering: t.clustering, Embed: t.embed, Embedding: t.embedding, Pairs: t.pairs, Decisions: decisions}
}

// guardedPair reports a split of a room that carries a role-guard cost.
func (r *regrouper) guardedPair(cur *regroupTrial, m regroupMove) bool {
	for _, g := range cur.clustering.Groups {
		if cur.guarded[g.ID] && holdsNode(g, m.a) && holdsNode(g, m.b) {
			return true
		}
	}
	return false
}

// sample is at most n of the moves, in their listed order: all of them, or n
// drawn from the seeded stream.
func sample(moves []regroupMove, n int, rng *searchRand) []regroupMove {
	if len(moves) <= n {
		return moves
	}
	idx := make([]int, len(moves))
	for i := range idx {
		idx[i] = i
	}
	for i := range n {
		j := i + rng.intn(len(idx)-i)
		idx[i], idx[j] = idx[j], idx[i]
	}
	chosen := idx[:n]
	slices.Sort(chosen)
	out := make([]regroupMove, 0, n)
	for _, i := range chosen {
		out = append(out, moves[i])
	}
	return out
}

func pairKey(a, b string) [2]string {
	if b < a {
		a, b = b, a
	}
	return [2]string{a, b}
}

// withRules is the rule set with rules added; a rule replaces any on the same pair.
func withRules(pairs, rules []NodePair) []NodePair {
	replaced := map[[2]string]bool{}
	for _, p := range rules {
		replaced[pairKey(p.A, p.B)] = true
	}
	var out []NodePair
	for _, p := range pairs {
		if !replaced[pairKey(p.A, p.B)] {
			out = append(out, p)
		}
	}
	for _, p := range rules {
		p.A, p.B = pairKey(p.A, p.B)[0], pairKey(p.A, p.B)[1]
		out = append(out, p)
	}
	slices.SortFunc(out, func(a, b NodePair) int {
		if c := strings.Compare(a.A, b.A); c != 0 {
			return c
		}
		return strings.Compare(a.B, b.B)
	})
	return out
}

func ruleOf(pairs []NodePair, a, b string) PairRule {
	for _, p := range pairs {
		if p.names(a, b) {
			return p.Rule
		}
	}
	return 0
}

// roomNodes are the graph nodes a room holds, sorted; the cooling station a cold
// room carries is no node of the graph.
func roomNodes(g RoomGroup) []string {
	var out []string
	for _, s := range g.Stations {
		if s.Purpose != PurposeCool {
			out = append(out, s.Node)
		}
	}
	slices.Sort(out)
	return out
}

func holdsNode(g RoomGroup, node string) bool { return slices.Contains(roomNodes(g), node) }

// candidates are the moves open from a grouping, in a fixed order.
func (r *regrouper) candidates(cur *regroupTrial) []regroupMove {
	var out []regroupMove
	seen := map[string]bool{}
	add := func(m regroupMove) {
		key := fmt.Sprint(m.kind, m.rules)
		if !seen[key] {
			seen[key] = true
			out = append(out, m)
		}
	}
	// merge: pairs the seed kept for a lost bench speed alone.
	for _, d := range cur.clustering.Decisions {
		if d.Verdict != ClusterKeep || d.Reason != ReasonClusterLostEffect || len(d.LostEffects) > 0 || d.A == "" || d.B == "" || d.A == d.B || ruleOf(cur.pairs, d.A, d.B) != 0 {
			continue
		}
		add(regroupMove{kind: RegroupMerge, reason: ReasonRegroupMerge, a: d.A, b: d.B, rules: []NodePair{{A: d.A, B: d.B, Rule: PairTogether}}})
	}
	// merge: a filth node kept in its closet joins the room of the node it flows
	// to, when the walk saved outweighs the poisoning it costs the room's cooks.
	// A filth model that cannot price the poisoning proposes nothing: the merge
	// would look free.
	priced := r.in.Cluster.Catalog.Filth.Priced()
	for _, d := range cur.clustering.Decisions {
		if !priced || d.Verdict != ClusterKeep || d.Reason != ReasonClusterFilthPriced || d.A == "" || d.B == "" || d.A == d.B || ruleOf(cur.pairs, d.A, d.B) != 0 {
			continue
		}
		add(regroupMove{kind: RegroupMerge, reason: ReasonRegroupFilthMerge, a: d.A, b: d.B, rules: []NodePair{{A: d.A, B: d.B, Rule: PairTogether}}})
	}
	groups := cur.clustering.Groups
	for _, g := range groups {
		nodes := roomNodes(g)
		if len(nodes) < 2 {
			continue
		}
		suffers := map[string]bool{}
		for _, c := range g.Costs {
			suffers[c.Node] = true
		}
		whole := len(g.LostEffects) > 0 || cur.guarded[g.ID] // a room that lost a role: any pair may be the cause
		// split: the pair of nodes whose room pays.
		for i, a := range nodes {
			for _, b := range nodes[i+1:] {
				if (whole || suffers[a] || suffers[b]) && ruleOf(cur.pairs, a, b) != PairApart {
					add(regroupMove{kind: RegroupSplit, reason: ReasonRegroupSplitConflict, a: a, b: b, rules: []NodePair{{A: a, B: b, Rule: PairApart}}})
				}
			}
		}
		// move-a-need: a node that pays goes to the room of the role it wants.
		for _, c := range g.Costs {
			for _, h := range groups {
				if h.ID == g.ID || len(roomNodes(h)) == 0 || holdsNode(h, c.Node) || !r.target(c, g, h) {
					continue
				}
				rules := []NodePair{{A: c.Node, B: roomNodes(h)[0], Rule: PairTogether}}
				for _, o := range nodes {
					if o != c.Node {
						rules = append(rules, NodePair{A: c.Node, B: o, Rule: PairApart})
					}
				}
				add(regroupMove{kind: RegroupMove, reason: ReasonRegroupMove, a: c.Node, b: roomNodes(h)[0], rules: rules})
			}
		}
	}
	return out
}

// target reports a room h the station class of cost c may move to from g: a
// room of the role a slowed bench wants, or a room beside g that is not kept cold
// for a band station.
func (r *regrouper) target(c ConflictCost, g, h RoomGroup) bool {
	switch c.Kind {
	case NeedRole:
		return h.Role == c.Role
	case NeedTemperature:
		return !slices.Contains(h.Requires, EnvCold) && slices.ContainsFunc(g.Links, func(l GroupLink) bool { return l.To == h.ID })
	}
	return false
}
