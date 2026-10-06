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
// only) and deepDrill places the ones the plan opened. Trade stays on the bid
// board, which the plan's winner per resource joins as the single bid.

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
}

// resourceSupply returns the Round's plan, built once per observed tick,
// invalidation generation and MaintainResource revision (an admission changes
// what is held, designated and in flight).
func (r *Rounder) resourceSupply(call context.Context, state ControlState, review store.Rounds, goal store.StandardState) (*resourceSupply, error) {
	key := resourceSupplyKey{state.Snapshot, review.Tick, 0, review.Revision, goal.Revision}
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

	if planner.native != nil {
		if out.drill, err = planner.deepDrillReading(call, state, review); err != nil {
			return nil, err
		}
	}
	var reach policy.RemoteWorkRequest
	reachRead := false
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
		need := deficit
		for _, source := range rows {
			if source.Resource != string(resource) {
				continue
			}
			if source.Designated {
				need -= int64(math.Round(source.Yield))
			} else if !held[source.ID] && !(source.Hunt && out.hunts <= 0) {
				row.open = append(row.open, source)
			}
		}
		input.Deficit = max(need, 0)
		if centered {
			input.Candidates = append(input.Candidates, policy.AcquisitionSourceCandidates(resource, row.open, center, domain.Known(need))...)
		}
		input.Candidates = append(input.Candidates, out.drill.candidates(resource, deficit, row)...)
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
func (d *deepDrillReading) candidates(resource policy.Resource, deficit int64, row *resourceSupplyRow) []policy.AcquisitionCandidate {
	if d == nil {
		return nil
	}
	headroom := domain.Unknown[int64]()
	if row.selKnown {
		headroom = domain.Known(row.sel.storage.Capacity)
	}
	var out []policy.AcquisitionCandidate
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
			open, _ := e.Channel.Open.Value()
			if e.Decision == policy.FoodPlanOpen || open && e.Decision != policy.FoodPlanClose {
				work, _ := e.Channel.WorkPerDay.Value()
				budget -= work
			}
		}
	}
	return domain.Known(math.Max(0, budget))
}

// huntSlots is the hunts a goal may still admit: two outstanding at most, none
// without a ranged hunter (noHunter).
func huntSlots(projection observation.ColonyProjection) (slots domain.Fact[int], noHunter bool) {
	slots = domain.Unknown[int]()
	if n, known := projection.PendingHunts.Value(); known {
		slots = domain.Known(max(0, 2-n))
	}
	if pawns, known := projection.WorkPawns.Value(); known {
		if _, ok := policy.HunterFor(policy.Profiles(pawns)); !ok {
			return domain.Known(0), true
		}
	}
	return slots, false
}

// bid posts the plan's winner for resource as the single resource bid and
// reports whether a deep drill or trade bid outranks it.
func (s *resourceSupply) bid(r *Rounder, snapshot domain.GenerationSnapshot, resource policy.Resource, tick domain.Tick) bool {
	kind, score, _ := s.plan.Winner(resource)
	_, yield := r.bids.bid(snapshot, resource, bidResource, score, kind, tick)
	return yield
}

// acquisitions are the census rows the plan opened for resource, best first:
// at most maxCatalogSelection rows and the free hunt slots.
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
	hunts := s.hunts
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
		}
		out = append(out, source)
		if len(out) == policy.MaxCatalogSelection {
			break
		}
	}
	return out
}

func isAcquisitionKind(k policy.CandidateKind) bool {
	return k == policy.CandidateChop || k == policy.CandidateHarvest || k == policy.CandidateHunt
}
