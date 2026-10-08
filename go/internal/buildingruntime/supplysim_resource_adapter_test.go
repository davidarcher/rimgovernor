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
// functions, candidates from the real catalog constructors and ranking by
// policy.PlanSupply over every unmet floor.
// It stands in for native reads and the journal only: a source the world
// opens is a dispatched designation, bill, drill or purchase, and a source it
// closes is finished work.
//
// Modelling assumptions (tests assert direction and ordering, never these):
// one catalog row per source whose yield is its remaining stock, distance from
// the spec, headroom unbounded and one evaluation per day at the day's first
// tick.

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
	// cotton field to sow.
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
	Source string
	Kind   policy.CandidateKind
	Score  float64
	// Drill gate evidence at dispatch.
	Deficit  bool
	Research bool
	Power    float64
}

type resHold struct {
	Day    int
	Good   supplysim.Good
	Reason string
}

type resPlanner struct {
	batch    resSupplyBatch
	world    supplysim.World
	spec     map[string]resSpec
	policy   policy.RoundsPolicy
	research bool
	uses     []resUse
	// clothing are the targets that are clothing-material floors, wanted
	// within policy.ClothingHorizonDays.
	clothing map[policy.Resource]bool

	events []resEvent
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
	var woodFloor int64
	for _, f := range v.Floors {
		if f.Good == supplysim.Wood && f.Latched {
			woodFloor = p.policy.WoodTarget
		}
	}
	var admitted []policy.AdmittedCost
	for i, b := range v.Builds {
		for g, left := range b.Left {
			admitted = append(admitted, policy.AdmittedCost{Resource: resDef(g), Action: domain.ActionID(b.Name + string(rune('a'+i))), Count: int64(math.Ceil(left))})
		}
	}
	merge(policy.ConstructionDemand(policy.ConstructionDemandInput{Stock: policy.StockReader{Resources: facts}, Admitted: admitted, WoodFloor: woodFloor}))

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
		p.resource(v, row, have[row.Resource], deficitRunway[row.Resource])
	}
	cmds = append(cmds, p.dispatchSupply(v)...)
	for g, d := range v.Demand {
		p.uses = append(p.uses, resUse{tick: tick + 1, resource: resDef(g), count: int64(math.Round(d))})
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
			tick, p.consumption(tick)))
	}
	return out
}

// resource builds one unmet floor's catalog rows into the day's batch.
func (p *resPlanner) resource(v supplysim.WorldView, row policy.ResourceTarget, have int64, deficitRunway bool) {
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
			return
		}
	}

	headroom := domain.Known(int64(1_000_000))
	var rows []policy.AcquisitionSource
	var mines []policy.ResourceSource
	var resourceExtra []policy.SupplyCandidate
	var produce *policy.SupplyCandidate
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
			// The candidate is the production pricing of a planned field
			// (#2284); the crop's harvest work gives the world's daily labor.
			c, cells := s.Crop, int(s.Crop.Cells)
			crop := policy.CropChoice{Name: id, GrowDays: domain.Known(float64(c.GrowDays)), UnitsPerCell: domain.Known(s.Yields[0].PerUnit),
				HarvestWork: domain.Known(s.Labor * float64(c.GrowDays) / float64(cells))}
			if field, ok := policy.ResourceFieldCandidate(row.Resource, policy.FieldPlan{Crop: crop, Needed: cells, Sites: policy.FarmSitePlan{Cells: cells}}); ok {
				field.ID = id
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
			kind := policy.CandidateLoot
			if spec.Role == resSalvage {
				kind = policy.CandidateSalvage
			}
			n := min(units, deficit)
			resourceExtra = append(resourceExtra, policy.SourceCandidate(kind, id, domain.Known(policy.AcquisitionLaborPerUnit[kind]*float64(max(n, 1))), domain.Known(spec.Distance), true, 75,
				policy.SourceYield(policy.ResourceKey{Def: row.Resource}, n, 0, headroom)))
		case resTrade:
			price := s.Costs[0].PerUnit / s.Yields[0].PerUnit
			n := min(deficit, int64(s.Restock*s.Yields[0].PerUnit), int64(v.Stock[supplysim.Silver]/price))
			if c, ok := policy.TradeCandidate(row.Resource, id, n, price); ok && n > 0 {
				p.batch.add(row.Resource, deficitRunway, c, id)
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
			resourceExtra = append(resourceExtra, c)
		}
	}

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

func (b *resSupplyBatch) add(r policy.Resource, deficit bool, c policy.SupplyCandidate, source string) {
	if b.source == nil {
		b.source, b.good, b.deficit = map[string]string{}, map[string]policy.Resource{}, map[policy.Resource]bool{}
	}
	sc := c
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
		kind := policy.CandidateKind(e.Candidate.Kind)
		if kind == policy.CandidateDeepDrill {
			p.drillScores = append(p.drillScores, e.Score)
		}
		if e.Decision != policy.SupplyOpen {
			continue
		}
		r := b.good[k]
		p.events = append(p.events, resEvent{Day: v.Day, Good: resGood(r), Source: b.source[k], Kind: kind, Score: e.Score,
			Deficit: b.deficit[r], Research: p.research, Power: p.world.Power})
		cmds = append(cmds, supplysim.Command{Kind: supplysim.Open, Source: b.source[k]})
		p.opens++
	}
	return cmds
}

// resUse is one day's demand the planner saw.
type resUse struct {
	tick     domain.Tick
	resource policy.Resource
	count    int64
}

// consumption is the recurring spend over the rate window ending at tick, in
// the ring's shape.
func (p *resPlanner) consumption(tick domain.Tick) domain.Fact[policy.ResourceConsumption] {
	window := min(tick, policy.ResourceRateWindowDays*domain.TicksPerDay)
	out := policy.ResourceConsumption{WindowDays: float64(window) / domain.TicksPerDay, Recurring: map[policy.Resource]int64{}}
	for _, u := range p.uses {
		if u.tick > tick-window && u.tick <= tick {
			out.Recurring[u.resource] += u.count
		}
	}
	return domain.Known(out)
}
