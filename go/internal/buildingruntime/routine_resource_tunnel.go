package buildingruntime

import (
	"context"
	"fmt"

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

// tunnelToBuriedOre is MaintainResource's corridor dig (#1074): when no mine
// source could be dispatched directly and the resource's nearest deposit is
// buried, a corridor-only excavation (policy.CorridorExcavationSites) is
// driven to it through the shelter's excavation routine (stepExcavation),
// stage by stage under this goal. Once the corridor opens, native reports
// the deposit reachable and the ordinary mine acquisition takes over. A
// project already under way continues before any new corridor is sited.
// handled is false when there is no excavation read, nothing buried, or a
// finished corridor and no new one.
func (r *RoutineResourcePlanner) tunnelToBuriedOre(call, epoch context.Context, state ControlState, goal store.GoalState, reviewTick domain.Tick, identity *c.Identity, resource policy.Resource) (RoutineResourceResult, bool, error) {
	source, ok := r.native.(RoutineExcavationSource)
	if !ok {
		return RoutineResourceResult{}, false, nil
	}
	p := r.reviewer.player
	dig := &RoutineBuildingPlanner{reviewer: r.reviewer, goal: policy.MaintainResource, excavation: source}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	last, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	expected, err := observation.DecodeIdentity(last)
	if err != nil || !routineBuildingBoundary(expected, state.Snapshot, reviewTick) {
		return RoutineResourceResult{}, false, fmt.Errorf("%w: tunnelToBuriedOre: boundary", ErrControl)
	}
	reading, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	step := excavationStep{state: state, review: review, goal: goal, facts: reading.Projection, read: reading}
	finish := func(result RoutineBuildingResult) (RoutineResourceResult, bool, error) {
		out := RoutineResourceResult{Reason: result.Reason}
		for _, m := range result.Decision.Goal.Methods {
			if IsExcavationMethod(m.Method) {
				out.Plan = m.Plan
			}
		}
		return out, true, nil
	}
	project, err := dig.excavationProject(call, goal)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	if project != nil {
		step.target = *project
		result, err := dig.stepExcavation(call, epoch, step)
		if err != nil {
			return RoutineResourceResult{}, false, err
		}
		// A finished corridor (Used) or one that cannot be finished
		// (Blocked) leaves the buried deposit to a fresh corridor below.
		if result.Reason != BuildingMethodUsed && result.Reason != BuildingExcavationBlocked {
			return finish(result)
		}
	}
	rows, _, _, err := r.native.ReadResourceSources(call, identity, string(resource))
	if err != nil {
		return RoutineResourceResult{}, false, nil
	}
	remote, err := r.miningReach(call, state, reviewTick)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	ore, ok := policy.BuriedResourceSource(rows, remote)
	if !ok {
		return RoutineResourceResult{}, false, nil
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
		return RoutineResourceResult{}, false, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	facts := reading.Projection
	request := policy.ExcavationSiteRequest{Bounds: facts.Bounds, Region: facts.Region, Anchor: facts.Center, Cells: facts.Cells, Protected: protected, MinCorridor: 1, MaxCorridor: tunnelMaxCorridor}
	snap.NoteExcavation(call, request)
	targets, err := policy.CorridorExcavationSites(request, ore.Cell)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	snapshot := state.Snapshot
	snapshot.Revision = 1
	for i, target := range targets {
		if i >= excavationCandidates {
			break
		}
		verified, err := dig.verifyExcavation(call, snapshot, facts.Identity.Tick, target, false, check)
		if err != nil {
			return RoutineResourceResult{}, false, err
		}
		if !verified {
			continue
		}
		clockSchedulerLog("%s: %s deposit %s at %v is buried; tunnelling %s", goal.Goal.ID, resource, ore.ThingID, ore.Cell, target.Key())
		step.target = target
		result, err := dig.stepExcavation(call, epoch, step)
		if err != nil {
			return RoutineResourceResult{}, false, err
		}
		return finish(result)
	}
	clockSchedulerLog("%s: %s deposit %s at %v is buried and no corridor verifies (%d proposed)", goal.Goal.ID, resource, ore.ThingID, ore.Cell, len(targets))
	return RoutineResourceResult{}, false, nil
}
