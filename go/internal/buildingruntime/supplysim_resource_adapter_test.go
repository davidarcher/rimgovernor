package buildingruntime

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/supplysim"
)

// The resource adapter drives today's MaintainResource acquisition path
// through a supplysim world (epic #2140): demand from the real policy
// functions, candidates from the real catalog constructors, ranking by
// RankResourceCandidates and reconciliation through the real acquisitionBoard.
// It stands in for native reads and the journal only: a source the world
// opens is a dispatched designation, bill, drill or purchase, and a source it
// closes is finished work.
//
// Modelling assumptions (tests assert direction and ordering, never these):
// one catalog row per source whose yield is its remaining stock, distance from
// the spec, headroom unbounded, one evaluation per day at the day's first
// tick, and every bidder posting before any dispatches (the planners run many
// steps a day).

type resRole string

const (
	resChop    resRole = "chop"
	resMine    resRole = "mine"
	resProduce resRole = "produce"
	resDrill   resRole = "drill"
	resTrade   resRole = "trade"
	resLoot    resRole = "loot"
	resSalvage resRole = "salvage"
	// resHunt is a herd whose leather serves a clothing floor; resField a
	// cotton field to sow. Both run through PlanSupply only (the four
	// planners never priced them).
	resHunt  resRole = "hunt"
	resField resRole = "field"
)

// resSpec tags a world source with the catalog kind it is acquired as. A
// source without a spec is unmanaged: the planner never opens or closes it.
type resSpec struct {
	Role     resRole
	Distance float64
}

// resDefs maps simulator goods to the policy definitions the real functions
// key on (stone block floors derive from the chunk definitions).
// bidAcquisition is the acquisition planner's bid of the board the adapter
// models; production posts the supply plan's winner as the one resource bid.
const bidAcquisition acquisitionBidder = "acquisition"

var resDefs = map[supplysim.Good]policy.Resource{
	supplysim.Leather:     "Leather_Plain",
	supplysim.Cotton:      "Cloth",
	supplysim.StoneChunks: "ChunkGranite",
	supplysim.StoneBlocks: "BlocksGranite",
	supplysim.Components:  policy.ComponentResource,
}

func resDef(g supplysim.Good) policy.Resource {
	if d, ok := resDefs[g]; ok {
		return d
	}
	return policy.Resource(g)
}

func resGood(r policy.Resource) supplysim.Good {
	for g, d := range resDefs {
		if d == r {
			return g
		}
	}
	return supplysim.Good(r)
}

type resEvent struct {
	Day    int
	Good   supplysim.Good
	Bidder acquisitionBidder
	Source string
	Kind   policy.AcquisitionKind
	Score  float64
	// Drill gate evidence at dispatch.
	Deficit  bool
	Research bool
	Power    float64
}

type resYield struct {
	Day    int
	Good   supplysim.Good
	Bidder acquisitionBidder
	Rival  acquisitionBid
	Tick   domain.Tick
}

type resHold struct {
	Day    int
	Good   supplysim.Good
	Reason string
}

type resPlanner struct {
	// supply ranks every unmet floor's candidates together through
	// policy.PlanSupply instead of the four planners and the bid board.
	supply   bool
	batch    resSupplyBatch
	world    supplysim.World
	spec     map[string]resSpec
	policy   policy.RoundsPolicy
	research bool
	board    acquisitionBoard
	snapshot domain.GenerationSnapshot
	uses     []policy.ResourceUse
	// clothing are the targets that are clothing-material floors, wanted
	// within policy.ClothingHorizonDays.
	clothing map[policy.Resource]bool

	events []resEvent
	yields []resYield
	holds  []resHold
	// drillScores is each deep drill bid the ranker produced.
	drillScores []float64
	opens       int
}

func newResPlanner(w supplysim.World, spec map[string]resSpec, targets map[policy.Resource]int64, research bool) *resPlanner {
	p := policy.DefaultRoundsPolicy()
	p.ResourceTargets = targets
	p.StoneBlockTarget = policy.DefaultStoneBlockTarget
	for _, f := range w.Floors {
		if f.Good == supplysim.Wood {
			p.WoodMin, p.WoodTarget, p.WoodMax = int64(f.Min), int64(f.Target), int64(f.Max)
		}
	}
	return &resPlanner{world: w, spec: spec, policy: p, research: research}
}

func (p *resPlanner) source(id string) supplysim.Source {
	for _, s := range p.world.Sources {
		if s.ID == id {
			return s
		}
	}
	return supplysim.Source{}
}

// goodOf is the good a managed source is run for: its first yield, or the
// clothing material of a hunt (its co-yield) or field.
func (p *resPlanner) goodOf(s supplysim.Source, spec resSpec) supplysim.Good {
	if spec.Role == resHunt {
		return s.Yields[len(s.Yields)-1].Good
	}
	return s.Yields[0].Good
}

func (p *resPlanner) yieldsGood(s supplysim.Source, g supplysim.Good) bool {
	return len(s.Yields) > 0 && p.goodOf(s, p.spec[s.ID]) == g
}

func (p *resPlanner) stopped(id string, day int) bool {
	for _, sh := range p.world.Shocks {
		if sh.Kind == supplysim.StopWork && sh.Source == id && day >= sh.Day && day < sh.Day+sh.Days {
			return true
		}
	}
	return false
}

// present is a caravan in the colony today.
func (p *resPlanner) present(s supplysim.Source, v supplysim.SourceView, day int) bool {
	return !v.Removed && s.Window.Active(day) && !p.stopped(s.ID, day)
}

func (p *resPlanner) hold(day int, g supplysim.Good, reason string) {
	p.holds = append(p.holds, resHold{day, g, reason})
}

func (p *resPlanner) Plan(v supplysim.WorldView) []supplysim.Command {
	tick := v.Tick
	stock := make([]policy.Amount, 0, len(v.Stock))
	have := map[policy.Resource]int64{}
	for g, n := range v.Stock {
		c := int64(math.Floor(n + 1e-9))
		stock = append(stock, policy.Amount{Resource: resDef(g), Count: c})
		have[resDef(g)] = c
	}
	sort.Slice(stock, func(i, j int) bool { return stock[i].Resource < stock[j].Resource })
	facts := domain.Known(stock)

	needs := map[policy.Resource]int64{}
	merge := func(m map[policy.Resource]int64) {
		for r, n := range m {
			needs[r] = max(needs[r], n)
		}
	}
	for _, f := range v.Floors {
		if f.Good == supplysim.Wood {
			merge(policy.WoodFloorNeeds(policy.WoodFloor(f.Latched, p.policy)))
		}
	}
	var deps []policy.DevelopmentDependency
	for i, b := range v.Builds {
		for g, left := range b.Left {
			deps = append(deps, policy.DevelopmentDependency{Prerequisite: policy.MaintainResource, Resource: resDef(g),
				Costs:     []policy.DependencyCost{{Action: domain.ActionID(b.Name + string(rune('a'+i))), Count: int64(math.Ceil(left))}},
				Available: domain.Known(have[resDef(g)]), Observed: tick})
		}
	}
	merge(policy.DependencyResourceNeeds(deps))

	runways := p.runways(v, have, tick)
	merge(policy.ResourceRunwayTargets(runways))
	targets, err := p.policy.EffectiveResourceTargets(facts, needs)
	if err != nil {
		panic(err)
	}
	deficitRunway := map[policy.Resource]bool{}
	for _, r := range runways {
		if d, ok := r.Deficit.Value(); ok && d {
			deficitRunway[r.Resource] = true
		}
	}

	var cmds []supplysim.Command
	// Finished work: a source whose resource has no unmet target is done.
	for _, sv := range v.Sources {
		spec, managed := p.spec[sv.ID]
		if !managed || !sv.Open || sv.Removed {
			continue
		}
		s := p.source(sv.ID)
		r := resDef(p.goodOf(s, spec))
		done := targets[r] == 0 || have[r] >= targets[r]
		if spec.Role == resTrade {
			done = done || !p.present(s, sv, v.Day)
		}
		if done {
			cmds = append(cmds, supplysim.Command{Kind: supplysim.Close, Source: sv.ID})
		}
	}

	ranked, err := policy.RankResourceTargets(targets, facts)
	if err != nil {
		panic(err)
	}
	for _, row := range ranked {
		cmds = append(cmds, p.resource(v, row, have[row.Resource], deficitRunway[row.Resource], tick)...)
	}
	if p.supply {
		cmds = append(cmds, p.dispatchSupply(v)...)
	}
	for g, d := range v.Demand {
		p.uses = append(p.uses, policy.ResourceUse{Tick: tick + 1, Resource: resDef(g), Count: domain.Known(int64(math.Round(d)))})
	}
	return cmds
}

// runways is the forecast the store builds for steel, components and plasteel
// from the consumption the planner has seen.
func (p *resPlanner) runways(v supplysim.WorldView, have map[policy.Resource]int64, tick domain.Tick) []policy.ResourceRunway {
	var out []policy.ResourceRunway
	for _, r := range []policy.Resource{"Steel", policy.ComponentResource, "Plasteel"} {
		var rows []policy.ResourceSource
		for _, sv := range v.Sources {
			s := p.source(sv.ID)
			if p.spec[sv.ID].Role == resMine && resDef(s.Yields[0].Good) == r && !sv.Removed {
				rows = append(rows, policy.ResourceSource{ThingID: sv.ID, Yield: int64(sv.Stock), Method: policy.ResourceSourceMine,
					Safety: policy.MineSafetyOpenSurface, Buried: s.Lead > 0})
			}
		}
		out = append(out, policy.ForecastResourceRunway(r, domain.Known(have[r]), policy.SurfaceOre(rows), p.policy.ResourceTargets[r],
			policy.ResourceHistory{End: tick, Uses: p.uses}))
	}
	return out
}

type resBid struct {
	score     float64
	kind      policy.AcquisitionKind
	sources   []string
	candidate bool
}

// resource evaluates one unmet floor the way the four planners do: each
// builds its catalog rows, posts its best score, and dispatches only when no
// other planner holds a fresh strictly higher bid.
func (p *resPlanner) resource(v supplysim.WorldView, row policy.ResourceTarget, have int64, deficitRunway bool, tick domain.Tick) []supplysim.Command {
	g := resGood(row.Resource)
	deficit := row.Target - have
	views := map[string]supplysim.SourceView{}
	var yielding []string
	for _, sv := range v.Sources {
		views[sv.ID] = sv
		s := p.source(sv.ID)
		if _, managed := p.spec[sv.ID]; managed && p.yieldsGood(s, g) && !sv.Removed {
			yielding = append(yielding, sv.ID)
		}
	}
	live := func(id string) bool {
		s, sv := p.source(id), views[id]
		if p.spec[id].Role == resTrade {
			return p.present(s, sv, v.Day)
		}
		return !s.Finite || sv.Stock >= 1
	}
	for _, id := range yielding {
		if views[id].Open && live(id) {
			p.hold(v.Day, g, "existing_work")
			return nil
		}
	}

	headroom := domain.Known(int64(1_000_000))
	var rows []policy.AcquisitionSource
	var mines []policy.ResourceSource
	var resourceExtra []policy.AcquisitionCandidate
	var produce *policy.AcquisitionCandidate
	trade := resBid{}
	for _, id := range yielding {
		s, sv, spec := p.source(id), views[id], p.spec[id]
		if sv.Open || !live(id) {
			continue
		}
		units := int64(math.Floor(sv.Stock))
		switch spec.Role {
		case resChop:
			rows = append(rows, policy.AcquisitionSource{ID: id, Resource: string(row.Resource), Tree: true, Yield: float64(units), Cell: domain.Cell{X: int32(spec.Distance)}})
		case resHunt:
			// A lone-hunt row priced at the leather of the animals left.
			leather := s.Yields[len(s.Yields)-1].PerUnit * float64(units)
			deer := policy.AcquisitionSource{ID: id, Resource: "Corpse_Deer", Hunt: true, Food: true, Yield: 1, Products: []policy.SourceProduct{{Def: row.Resource, Amount: leather}}}
			if priced, ok := policy.ResourceSourceFor(deer, row.Resource, map[policy.Resource]policy.Resource{row.Resource: row.Resource}); ok {
				rows = append(rows, priced)
			}
		case resField:
			// The zone and the sowing are upfront work; the first harvest is
			// the crop's grow time away.
			c, cells := s.Crop, s.Crop.Cells
			if field, ok := policy.FieldHarvestCandidate(row.Resource, id, policy.FieldHarvest{Cells: int64(cells), GrowDays: float64(c.GrowDays), UnitsPerCell: s.Yields[0].PerUnit,
				SetupTicks: cells * 20, WorkPerDay: s.Labor}); ok {
				p.batch.addField(row.Resource, deficitRunway, field, id)
			}
		case resMine:
			mines = append(mines, policy.ResourceSource{ThingID: id, Yield: units, Distance: spec.Distance, Method: policy.ResourceSourceMine, Safety: policy.MineSafetyOpenSurface})
		case resProduce:
			in := s.Costs[0]
			// The bill needs its input in stock.
			can := int64(math.Floor(v.Stock[in.Good] / in.PerUnit * s.Yields[0].PerUnit))
			if can <= 0 {
				continue
			}
			if c, ok := policy.ProduceCandidate(policy.ResourceMethod{Kind: policy.ResourceMethodProduce, Bench: id, Recipe: "cut", Resource: row.Resource}, min(deficit, can)); ok {
				c.ID = id
				produce = &c
			}
		case resLoot, resSalvage:
			if v.Threat {
				continue
			}
			kind := policy.AcquisitionLoot
			if spec.Role == resSalvage {
				kind = policy.AcquisitionSalvage
			}
			n := min(units, deficit)
			resourceExtra = append(resourceExtra, policy.AcquisitionCandidate{ID: id, Kind: kind,
				Yields:       []policy.AcquisitionYield{{ResourceQuantity: policy.ResourceQuantity{Key: policy.ResourceKey{Def: row.Resource}, Count: n}, Headroom: headroom}},
				PathDistance: domain.Known(spec.Distance), Labor: domain.Known(policy.AcquisitionLaborPerUnit[kind] * float64(max(n, 1))),
				NeedsHaul: true, UnitsPerTrip: 75})
		case resTrade:
			price := s.Costs[0].PerUnit / s.Yields[0].PerUnit
			n := min(deficit, int64(s.Restock*s.Yields[0].PerUnit), int64(v.Stock[supplysim.Silver]/price))
			if c, ok := policy.TradeCandidate(row.Resource, id, n, price); ok && n > 0 && p.supply {
				p.batch.add(row.Resource, deficitRunway, c, id)
			} else if ok && n > 0 {
				ranked := p.rank(row.Resource, deficit, []policy.AcquisitionCandidate{c})
				if len(ranked) > 0 {
					trade = resBid{ranked[0].Score, policy.AcquisitionTrade, []string{id}, true}
				}
			}
		case resDrill:
			// Gate of deepDrillSites: research and a scanner, a forecast
			// deficit on steel or plasteel with stock under target.
			if !p.research || !deficitRunway || p.world.Power <= 0 || row.Resource != "Steel" && row.Resource != "Plasteel" {
				continue
			}
			// The drill is a candidate of the resource planner's pool: its
			// metal lands in the storage the mines' does.
			c, ok := policy.DeepDrillCandidate(row.Resource, id, min(units, deficit), spec.Distance, headroom)
			if !ok {
				continue
			}
			if ranked := p.rank(row.Resource, deficit, []policy.AcquisitionCandidate{c}); len(ranked) > 0 {
				p.drillScores = append(p.drillScores, ranked[0].Score)
			} else {
				p.drillScores = append(p.drillScores, 0)
			}
			resourceExtra = append(resourceExtra, c)
		}
	}

	if p.supply {
		for _, c := range policy.AcquisitionSourceCandidates(row.Resource, rows, domain.Cell{}, headroom) {
			p.batch.add(row.Resource, deficitRunway, c, c.ID)
		}
		for _, c := range policy.MineCandidates(row.Resource, mines, headroom) {
			p.batch.add(row.Resource, deficitRunway, c, c.ID)
		}
		if produce != nil {
			p.batch.add(row.Resource, deficitRunway, *produce, produce.ID)
		}
		for _, c := range resourceExtra {
			p.batch.add(row.Resource, deficitRunway, c, c.ID)
		}
		horizon := 0.0
		if p.clothing[row.Resource] {
			horizon = policy.ClothingHorizonDays
		}
		p.batch.demand(row.Resource, deficit, horizon)
		if !p.batch.has(row.Resource) {
			p.hold(v.Day, g, "no_source")
		}
		return nil
	}
	// Acquisition planner: chop, harvest and hunt rows.
	acq := resBid{}
	if len(rows) > 0 {
		picked, best, err := policy.SelectCatalogAcquisition(rows, row.Resource, deficit, domain.Cell{}, nil, 0)
		if err != nil {
			panic(err)
		}
		acq = resBid{score: best.Score, kind: best.Kind, candidate: len(picked) > 0}
		for _, r := range picked {
			acq.sources = append(acq.sources, r.ID)
		}
	}
	// Resource planner: mine and bill against each other, with windfalls.
	res := resBid{}
	cands := policy.MineCandidates(row.Resource, mines, headroom)
	if produce != nil {
		cands = append(cands, *produce)
	}
	cands = append(cands, resourceExtra...)
	if len(cands) > 0 {
		if ranked := p.rank(row.Resource, deficit, cands); len(ranked) > 0 {
			res = resBid{ranked[0].Score, ranked[0].Kind, []string{ranked[0].ID}, true}
		}
	}

	if !trade.candidate && !acq.candidate && !res.candidate {
		p.hold(v.Day, g, "no_source")
		for _, b := range []acquisitionBidder{bidTrade, bidAcquisition, bidResource} {
			p.board.bid(p.snapshot, row.Resource, b, 0, "", tick)
		}
		return nil
	}

	order := []struct {
		who acquisitionBidder
		bid resBid
	}{{bidTrade, trade}, {bidAcquisition, acq}, {bidResource, res}}
	// Every planner posts first; the second pass is the step that dispatches.
	for _, o := range order {
		p.board.bid(p.snapshot, row.Resource, o.who, o.bid.score, o.bid.kind, tick)
	}
	var cmds []supplysim.Command
	for _, o := range order {
		if !o.bid.candidate {
			continue
		}
		if rival, yield := p.board.bid(p.snapshot, row.Resource, o.who, o.bid.score, o.bid.kind, tick); yield {
			p.yields = append(p.yields, resYield{v.Day, g, o.who, rival, tick})
			continue
		}
		for _, id := range o.bid.sources {
			p.events = append(p.events, resEvent{Day: v.Day, Good: g, Bidder: o.who, Source: id, Kind: o.bid.kind, Score: o.bid.score,
				Deficit: deficitRunway, Research: p.research, Power: p.world.Power})
			cmds = append(cmds, supplysim.Command{Kind: supplysim.Open, Source: id})
			p.opens++
		}
	}
	return cmds
}

func (p *resPlanner) rank(r policy.Resource, deficit int64, cands []policy.AcquisitionCandidate) []policy.AcquisitionScore {
	ranked, err := policy.RankResourceCandidates(policy.ResourceDeficitDemand(r, deficit), cands, policy.AcquisitionCompetition{})
	if err != nil {
		panic(err)
	}
	return ranked
}

// resSupplyBatch collects every unmet floor's catalog rows for one PlanSupply
// call, so the labor budget is shared across resources.
type resSupplyBatch struct {
	demands []policy.SupplyDemand
	cands   []policy.SupplyCandidate
	source  map[string]string
	good    map[string]policy.Resource
	deficit map[policy.Resource]bool
}

func (b *resSupplyBatch) key(kind policy.CandidateKind, id string) string {
	return string(kind) + "/" + id
}

func (b *resSupplyBatch) add(r policy.Resource, deficit bool, c policy.AcquisitionCandidate, source string) {
	if b.source == nil {
		b.source, b.good, b.deficit = map[string]string{}, map[string]policy.Resource{}, map[policy.Resource]bool{}
	}
	sc := policy.SupplyCandidateOfAcquisition(c)
	k := b.key(sc.Kind, sc.ID)
	b.source[k], b.good[k], b.deficit[r] = source, r, deficit
	b.cands = append(b.cands, sc)
}

func (b *resSupplyBatch) has(r policy.Resource) bool {
	for _, g := range b.good {
		if g == r {
			return true
		}
	}
	return false
}

func (b *resSupplyBatch) demand(r policy.Resource, deficit int64, horizon float64) {
	if deficit > 0 {
		b.demands = append(b.demands, policy.SupplyDemand{Good: policy.ResourceKey{Def: r}, Units: deficit, Priority: 1, HorizonDays: horizon})
	}
}

// addField batches a candidate already in supply form (a field with a lead).
func (b *resSupplyBatch) addField(r policy.Resource, deficit bool, c policy.SupplyCandidate, source string) {
	if b.source == nil {
		b.source, b.good, b.deficit = map[string]string{}, map[string]policy.Resource{}, map[policy.Resource]bool{}
	}
	k := b.key(c.Kind, c.ID)
	b.source[k], b.good[k], b.deficit[r] = source, r, deficit
	b.cands = append(b.cands, c)
}

// dispatchSupply ranks the day's batch and opens what PlanSupply opens.
func (p *resPlanner) dispatchSupply(v supplysim.WorldView) []supplysim.Command {
	b := p.batch
	p.batch = resSupplyBatch{}
	if len(b.cands) == 0 {
		return nil
	}
	plan, err := policy.PlanSupply(policy.SupplyPlanRequest{Demands: domain.Known(b.demands), Candidates: domain.Known(b.cands), Labor: domain.Known(float64(p.world.Workers) * 20000)})
	if err != nil {
		panic(err)
	}
	var cmds []supplysim.Command
	for _, e := range plan.Portfolio {
		k := b.key(e.Candidate.Kind, e.Candidate.ID)
		kind := policy.AcquisitionKind(e.Candidate.Kind)
		if kind == policy.AcquisitionDeepDrill {
			p.drillScores = append(p.drillScores, e.Score)
		}
		if e.Decision != policy.SupplyOpen {
			continue
		}
		r := b.good[k]
		p.events = append(p.events, resEvent{Day: v.Day, Good: resGood(r), Bidder: bidResource, Source: b.source[k], Kind: kind, Score: e.Score,
			Deficit: b.deficit[r], Research: p.research, Power: p.world.Power})
		cmds = append(cmds, supplysim.Command{Kind: supplysim.Open, Source: b.source[k]})
		p.opens++
	}
	return cmds
}
