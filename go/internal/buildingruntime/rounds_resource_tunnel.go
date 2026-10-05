package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// tunnelMaxCorridor bounds a corridor-only dig to buried ore (#1074); it
// and the site read stay well inside the 64-cell excavation read.
const tunnelMaxCorridor = 16

// tunnelMemory remembers, per world and resource, the corridor last sited
// to a buried deposit (#1124). A sited corridor becomes durable only when
// its first stage is admitted; a planner cut off before that (an optional
// planner missing the wave cutoff) would otherwise lose it, and the next
// review geometry search, bound to a colony window that follows the
// pawns, may no longer propose it. A restart forgets it, which only
// re-runs the search.
type tunnelMemory struct {
	mu    sync.Mutex
	world string
	sited map[policy.Resource]sitedTunnel
}

type sitedTunnel struct {
	ore    domain.Cell
	target policy.ExcavationTarget
}

func (m *tunnelMemory) get(world string, resource policy.Resource, ore domain.Cell) (policy.ExcavationTarget, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	t, ok := m.sited[resource]
	if m.world != world || !ok || t.ore != ore {
		return policy.ExcavationTarget{}, false
	}
	return t.target, true
}

func (m *tunnelMemory) set(world string, resource policy.Resource, ore domain.Cell, target policy.ExcavationTarget) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.world != world || m.sited == nil {
		m.world, m.sited = world, map[policy.Resource]sitedTunnel{}
	}
	m.sited[resource] = sitedTunnel{ore, target}
}

func (m *tunnelMemory) forget(resource policy.Resource) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.sited, resource)
}

// tunnelToBuriedOre is MaintainResource's corridor dig (#1074): when no mine
// source could be dispatched directly and the resource's nearest deposit is
// buried and in resource reach, a corridor-only excavation
// (policy.CorridorExcavationSites) is driven to it through the
// tunnel routine (stepExcavation), stage by stage under this goal. Once
// the corridor opens, native reports the deposit reachable and the ordinary
// mine acquisition takes over. A project already under way continues before
// any new corridor is sited, and so does a sited corridor whose first stage
// was never admitted (#1124). handled is false when there is no excavation
// read, nothing buried in reach, or a finished corridor and no new one.
func (r *RoundsResourcePlanner) tunnelToBuriedOre(call, epoch context.Context, state ControlState, goal store.StandardState, reviewTick domain.Tick, identity *c.Identity, resource policy.Resource) (RoundsResourceResult, bool, error) {
	source, ok := r.native.(RoundsExcavationSource)
	if !ok {
		return RoundsResourceResult{}, false, nil
	}
	p := r.reviewer.player
	dig := &RoundsBuildingPlanner{reviewer: r.reviewer, concern: policy.MaintainResource, excavation: source}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsResourceResult{}, false, err
	}
	last, _, err := r.native.Identity(call)
	if err != nil {
		return RoundsResourceResult{}, false, err
	}
	expected, err := observation.DecodeIdentity(last)
	if err != nil || !roundsBuildingBoundary(expected, state.Snapshot, reviewTick) {
		return RoundsResourceResult{}, false, fmt.Errorf("%w: tunnelToBuriedOre: boundary", ErrControl)
	}
	reading, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return RoundsResourceResult{}, false, err
	}
	step := excavationStep{state: state, review: review, owner: goal, facts: reading.Projection, read: reading}
	finish := func(result RoundsBuildingResult) (RoundsResourceResult, bool, error) {
		out := RoundsResourceResult{Verdict: result.Verdict}
		for _, m := range result.Decision.Standard.Methods {
			if IsExcavationMethod(m.Method) {
				out.Plan = m.Plan
			}
		}
		return out, true, nil
	}
	// resume steps a known corridor; done reports that it finished (Used)
	// or cannot be finished (Blocked), leaving the deposit to a fresh one.
	resume := func(target policy.ExcavationTarget) (RoundsBuildingResult, bool, error) {
		step.target = target
		result, err := dig.stepExcavation(call, epoch, step)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		return result, result.Verdict.Is(WaitMethodUsed) || result.Verdict.Is(RefusalSiteBlocked), nil
	}
	project, err := dig.excavationProject(call, goal)
	if err != nil {
		return RoundsResourceResult{}, false, err
	}
	if project != nil {
		result, done, err := resume(*project)
		if err != nil {
			return RoundsResourceResult{}, false, err
		}
		if !done {
			return finish(result)
		}
		r.reviewer.tunnels.forget(resource)
	}
	rows, _, _, err := r.native.ReadResourceSources(call, identity, string(resource))
	if err != nil {
		return RoundsResourceResult{}, false, nil
	}
	remote, err := r.miningReach(call, state, reviewTick)
	if err != nil {
		return RoundsResourceResult{}, false, err
	}
	ore, ok := policy.BuriedResourceSource(rows, remote)
	if !ok {
		return RoundsResourceResult{}, false, nil
	}
	world := stockpileWorld(state.Snapshot)
	if sited, ok := r.reviewer.tunnels.get(world, resource, ore.Cell); ok && project == nil {
		result, done, err := resume(sited)
		if err != nil {
			return RoundsResourceResult{}, false, err
		}
		if !done {
			clockSchedulerLog("%s: %s deposit %s at %v is buried; resuming tunnel %s", goal.Standard.ID, resource, ore.ThingID, ore.Cell, sited.Key())
			return finish(result)
		}
		r.reviewer.tunnels.forget(resource)
	}
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: tunnelToBuriedOre: p.session.State() != state", ErrControl)
		}
		return nil
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoundsResourceResult{}, false, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	facts := reading.Projection
	definitions, ok := r.native.(observation.DefinitionSource)
	if !ok {
		return RoundsResourceResult{}, false, errors.New("roof rules: the native source serves no definitions")
	}
	catalog, err := definitions.DefinitionCatalog(call, identity)
	if err != nil {
		return RoundsResourceResult{}, false, err
	}
	roofs, err := catalog.RoofRules()
	if err != nil {
		return RoundsResourceResult{}, false, err
	}
	anchor, planned := facts.Center().Value()
	if !planned {
		return RoundsResourceResult{Verdict: BuildingNoLayoutPlan}, false, nil
	}
	request := policy.ExcavationSiteRequest{Bounds: facts.Bounds, Region: facts.Region, Anchor: anchor, Cells: facts.Cells, Protected: protected, MinCorridor: 1, MaxCorridor: tunnelMaxCorridor, RoofSupport: facts.RoofSupport, Roofs: roofs}
	snap.NoteExcavation(call, request)
	targets, err := policy.CorridorExcavationSites(request, ore.Cell)
	if err != nil {
		return RoundsResourceResult{}, false, err
	}
	snapshot := state.Snapshot
	snapshot.Revision = 1
	for i, target := range targets {
		if i >= excavationCandidates {
			break
		}
		verified, err := dig.verifyExcavation(call, snapshot, facts.Identity.Tick, target, check)
		if err != nil {
			return RoundsResourceResult{}, false, err
		}
		if !verified {
			continue
		}
		clockSchedulerLog("%s: %s deposit %s at %v is buried; tunnelling %s", goal.Standard.ID, resource, ore.ThingID, ore.Cell, target.Key())
		r.reviewer.tunnels.set(world, resource, ore.Cell, target)
		result, _, err := resume(target)
		if err != nil {
			return RoundsResourceResult{}, false, err
		}
		return finish(result)
	}
	clockSchedulerLog("%s: %s deposit %s at %v is buried and no corridor verifies (%d proposed)", goal.Standard.ID, resource, ore.ThingID, ore.Cell, len(targets))
	return RoundsResourceResult{}, false, nil
}
