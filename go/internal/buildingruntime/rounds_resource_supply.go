package buildingruntime

import (
	"context"
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The Round's resource supply plan: one policy.PlanSupply over every unmet
// MaintainResource floor, planned after the food plan against the labor it
// leaves. The resource planner executes the mine and produce candidates the
// plan opened, the acquisition planner the chop, harvest and hunt candidates;
// neither ranks on its own. A deep drill is a candidate too (placeable sites
// only) and deepDrill places the ones the plan opened. A caravan's recorded
// offers (tradeOfferBook) are candidates too, and RoundsTradePlanner buys only
// the resources the plan opened for its trader.

// unboundedLabor stands for a work budget the review could not read: the
// workers are unknown, so the plan does not ration labor.
const unboundedLabor = 1e12

// resourceSupplyRow is the facts behind one resource's candidates, kept so the
// executing planners act on exactly what the plan priced.
type resourceSupplyRow struct {
	resource policy.Resource
	target   int64
	deficit  int64
	// sel is the mine selection; selKnown is false when its native read failed.
	sel      sourceSelection
	selKnown bool
	// choice is the bench bill that would produce the resource.
	choice policy.ResourceMethod
	// open are the census rows (chop, harvest, hunt) offered to the plan.
	open []policy.AcquisitionSource
	// busy: a census designation of the resource is still in flight.
	busy bool
	// fields are the new fields the plan priced (#2284) by candidate ID: the
	// crop, cells and patches the field step (#2285) places when the plan opened it.
	fields map[string]policy.FieldPlan
}

type resourceSupply struct {
	plan policy.ResourceSupply
	// order is the unmet floors, worst covered first.
	order []policy.Resource
	rows  map[policy.Resource]*resourceSupplyRow
	// tokens maps each bench id of the census to its write token.
	tokens map[string]string
	// hunts is how many hunts may be admitted.
	hunts int
	// drill is the deep drill gate's reading, nil when no metal is in deficit.
	drill *deepDrillReading
}

type resourceSupplyKey struct {
	snapshot   domain.GenerationSnapshot
	tick       domain.Tick
	generation uint64
	review     uint64
	standard   uint64
	offers     uint64
}

// resourceSupply returns the Round's plan, built once per observed tick,
// invalidation generation and MaintainResource revision (an admission changes
// what is held, designated and in flight).
func (r *Rounder) resourceSupply(call context.Context, state ControlState, review store.Rounds, goal store.StandardState) (*resourceSupply, error) {
	key := resourceSupplyKey{state.Snapshot, review.Tick, 0, review.Revision, goal.Revision, r.tradeOffers.version()}
	s := &r.census
	s.mu.Lock()
	key.generation = s.generation
	if s.supply != nil && s.supplyKey == key {
		cached := s.supply
		s.mu.Unlock()
		return cached, nil
	}
	s.mu.Unlock()
	built, err := r.buildResourceSupply(call, state, review, goal)
	if err != nil {
		return nil, err
	}
	s.mu.Lock()
	s.supply, s.supplyKey = built, key
	s.mu.Unlock()
	return built, nil
}

func (r *Rounder) buildResourceSupply(call context.Context, state ControlState, review store.Rounds, goal store.StandardState) (*resourceSupply, error) {
	expected, projection, err := r.acquisitionReading(call, state, review)
	if err != nil {
		return nil, err
	}
	stock := projection.Facts.Resources
	targets, err := r.resourceTargets(call, state.Snapshot, stock)
	if err != nil {
		return nil, err
	}
	ranked, err := policy.RankResourceTargets(targets, stock)
	if err != nil {
		return nil, err
	}
	serves := policy.ClothingServes(projection.Facts.ClothingMaterials(), targets)
	out := &resourceSupply{rows: map[policy.Resource]*resourceSupplyRow{}, tokens: map[string]string{}}
	planner := &RoundsResourcePlanner{reviewer: r, native: r.resourceNative}
	identity := boundary.Identity(state.Snapshot)

	undispatched := map[string]bool{}
	for _, method := range goal.Methods {
		plan, err := r.player.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return nil, err
		}
		for thing := range undispatchedAcquisitions(plan.Progress) {
			undispatched[thing] = true
		}
	}
	progress, _ := review.ConcernProgress(policy.MaintainResource)
	held := cooledSources(projection.Acquisition, func(id string) bool {
		return acquisitionCooled(progress, r.policy, id, expected.Tick)
	})
	busy := holdWorked(projection.Acquisition, undispatched, held)
	slots, _ := huntSlots(projection)
	out.hunts, _ = slots.Value()
	center, centered := projection.Center().Value()
	rows, _ := projection.Acquisition.Value()
	// Recorded offers are candidates while the trader census is known; the
	// silver above the reserve is what a purchase may spend.
	var offers []policy.TradeOffers
	var spendable, reserve int64
	if traders, known := projection.Facts.Traders.Value(); known {
		silver, silverKnown := projection.Facts.Silver().Value()
		var reserveKnown bool
		reserve, reserveKnown = policy.TradeSilverReserve(projection.Facts.Colonists)
		if silverKnown && reserveKnown {
			offers = r.tradeOffers.fresh(state.Snapshot, expected.Tick, silver, traders)
			spendable = silver
		}
	}

	if planner.native != nil {
		if out.drill, err = planner.deepDrillReading(call, state, review); err != nil {
			return nil, err
		}
	}
	var reach policy.RemoteWorkRequest
	reachRead := false
	fieldPlanner := r.newResourceFieldPlanner(call, state, review, expected, projection)
	var inputs []policy.ResourceSupplyInput
	for _, target := range ranked {
		resource := target.Resource
		deficit := target.Target - resourceCount(stock, resource)
		if resource == "Beer" || deficit <= 0 {
			continue
		}
		row := &resourceSupplyRow{resource: resource, target: target.Target, deficit: deficit, busy: busy[string(resource)]}
		out.order = append(out.order, resource)
		out.rows[resource] = row
		input := policy.ResourceSupplyInput{Resource: resource, Deficit: deficit}
		if serves[resource] == resource {
			input.HorizonDays = policy.ClothingHorizonDays
		}
		if planner.native != nil {
			if !reachRead {
				if reach, err = planner.miningReach(call, state, review.Tick); err != nil {
					return nil, err
				}
				reachRead = true
			}
			if row.choice, _, err = planner.methodChoice(call, state, identity, goal, review, resource, target.Target, stock, nil, out.tokens); err != nil {
				return nil, err
			}
			row.sel, row.selKnown = planner.sourcesForDeficit(call, identity, resource, target.Target, stock, reach)
			if row.selKnown {
				input.Candidates = policy.MineCandidates(resource, row.sel.selected, domain.Known(row.sel.storage.Capacity))
			}
			if produce, found := policy.ProduceCandidate(row.choice, deficit); found {
				input.Candidates = append(input.Candidates, produce)
			}
		}
		// A designated chop or harvest already covers part of the need.
		// A clothing material is also served by its category's other stuffs and
		// by the leather of a hunt, priced at the units the resource gets.
		need := deficit
		var priced []policy.AcquisitionSource
		for _, source := range rows {
			offered, serving := policy.ResourceSourceFor(source, resource, serves)
			if !serving {
				continue
			}
			if source.Designated {
				need -= int64(math.Round(offered.Yield))
			} else if !held[source.ID] && !(source.Hunt && out.hunts <= 0) {
				row.open = append(row.open, source)
				priced = append(priced, offered)
			}
		}
		// Standing fields of the resource's crops count like designated
		// sources; a new field is priced for what is still short.
		need -= fieldPlanner.standing(resource)
		if need > 0 && fieldPlanner.serves(resource) {
			candidate, plan, ok, err := fieldPlanner.candidate(resource, need)
			if err != nil {
				return nil, err
			}
			if ok {
				input.Candidates = append(input.Candidates, candidate)
				row.fields = map[string]policy.FieldPlan{candidate.ID: plan}
				// A field's lead is its grow time: the floor is wanted that far
				// ahead or no field could serve it.
				if lead, _ := candidate.LeadDays.Value(); lead > input.HorizonDays {
					input.HorizonDays = lead
				}
			}
		}
		input.Deficit = max(need, 0)
		if centered {
			input.Candidates = append(input.Candidates, policy.AcquisitionSourceCandidates(resource, priced, center, domain.Known(need))...)
		}
		input.Candidates = append(input.Candidates, out.drill.candidates(resource, deficit, row)...)
		input.Candidates = append(input.Candidates, policy.TradeOfferCandidates(resource, offers, deficit, spendable, reserve)...)
		inputs = append(inputs, input)
	}
	if out.plan, err = policy.PlanResourceSupply(inputs, resourceLabor(projection)); err != nil {
		return nil, fmt.Errorf("%w: buildResourceSupply: %w", ErrControl, err)
	}
	return out, nil
}

// candidates are the catalog rows of the placeable drill sites over resource,
// each yielding the lump up to the deficit. The output lands where the mines'
// does, so it is hauled to that storage's headroom.
func (d *deepDrillReading) candidates(resource policy.Resource, deficit int64, row *resourceSupplyRow) []policy.SupplyCandidate {
	if d == nil {
		return nil
	}
	headroom := domain.Unknown[int64]()
	if row.selKnown {
		headroom = domain.Known(row.sel.storage.Capacity)
	}
	var out []policy.SupplyCandidate
	for _, place := range d.placeable {
		if policy.Resource(place.site.Definition) != resource {
			continue
		}
		if c, ok := policy.DeepDrillCandidate(resource, place.id, min(place.site.Count, deficit), place.distance, headroom); ok {
			out = append(out, c)
		}
	}
	return out
}

// resourceLabor is the daily work budget the food plan leaves: workers *
// 20000 ticks less the work of every channel food keeps open.
func resourceLabor(p observation.ColonyProjection) domain.Fact[float64] {
	workers, known := p.Workers.Value()
	if pawns, ok := p.WorkPawns.Value(); ok {
		workers, known = policy.RoundsWorkers(pawns).Value()
	}
	if !known {
		return domain.Known(unboundedLabor)
	}
	budget := float64(workers) * 20000
	if plan, ok := p.Facts.FoodPlan.Value(); ok {
		for _, e := range plan.Portfolio {
			open, _ := e.Channel.Open().Value()
			if e.Decision == policy.FoodPlanOpen || open && e.Decision != policy.FoodPlanClose {
				work, _ := e.Channel.LaborPerDay.Value()
				budget -= work
			}
		}
	}
	return domain.Known(math.Max(0, budget))
}

// huntSlots is the hunts a goal may still admit: policy.HuntsPerHunter in
// flight per ranged hunter, none without one (noHunter). The nutrition gap
// sets how many of them selection takes.
func huntSlots(projection observation.ColonyProjection) (slots domain.Fact[int], noHunter bool) {
	slots = domain.Unknown[int]()
	pending, pendingKnown := projection.PendingHunts.Value()
	if pawns, known := projection.WorkPawns.Value(); known {
		profiles := policy.Profiles(pawns)
		if _, ok := policy.HunterFor(profiles); !ok {
			return domain.Known(0), true
		}
		if pendingKnown {
			slots = domain.Known(policy.HuntBudget(profiles, pending))
		}
	}
	return slots, false
}

// acquisitions are the census rows the plan opened for resource, best first:
// at most MaxCatalogSelection other rows and the free hunt slots.
func (s *resourceSupply) acquisitions(resource policy.Resource) []policy.AcquisitionSource {
	row := s.rows[resource]
	if row == nil {
		return nil
	}
	byID := map[string]policy.AcquisitionSource{}
	for _, source := range row.open {
		byID[source.ID] = source
	}
	var out []policy.AcquisitionSource
	hunts, others := s.hunts, 0
	for _, e := range s.plan.Opened(resource) {
		source, ok := byID[e.Candidate.ID]
		if !ok || !isAcquisitionKind(e.Candidate.Kind) {
			continue
		}
		if source.Hunt {
			if hunts <= 0 {
				continue
			}
			hunts--
		} else {
			if others == policy.MaxCatalogSelection {
				continue
			}
			others++
		}
		out = append(out, source)
	}
	return out
}

// openedFields are the field plans the supply plan opened, worst covered
// resource first; the field step places each as a MaintainResource shortfall.
func (s *resourceSupply) openedFields() []policy.FieldPlan {
	var out []policy.FieldPlan
	for _, resource := range s.order {
		for _, e := range s.plan.Opened(resource) {
			if plan, ok := s.rows[resource].fields[e.Candidate.ID]; ok && e.Candidate.Kind == policy.CandidateHarvest {
				out = append(out, plan)
			}
		}
	}
	return out
}

// tradeLines are the units of each resource the plan opened to buy from
// trader: what RoundsTradePlanner may stage.
func (s *resourceSupply) tradeLines(trader string) map[policy.Resource]int64 {
	out := map[policy.Resource]int64{}
	for resource := range s.rows {
		for _, e := range s.plan.Opened(resource) {
			if e.Candidate.Kind != policy.CandidateTrade || e.Candidate.ID != policy.TradeCandidateID(trader, resource) {
				continue
			}
			units, _ := e.Candidate.Yields[0].StockCap.Value()
			if e.Wanted > 0 {
				units = min(units, e.Wanted)
			}
			out[resource] += units
		}
	}
	return out
}

func isAcquisitionKind(k policy.CandidateKind) bool {
	return k == policy.CandidateChop || k == policy.CandidateHarvest || k == policy.CandidateHunt
}
