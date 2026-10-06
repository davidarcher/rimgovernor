package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsAnimalContainmentPlanner composes MaintainAnimalContainment's
// containment-method decision (policy.SelectAnimalContainmentMethod) with the
// build side of the reconciler: the one PenMarker that claims the yard inside
// the defensive wall once it is closed and its lane fenced (stagePaddock, #2233);
// the barn and vet room (stageHerdRooms) are raised meanwhile. It is self-contained the way
// RoundsFieldPlanner is, on purpose: the shared shelter/cooking/comfort switch
// is actively edited by parallel building-family slices, and this goal's action
// family needs none of its machinery.
type RoundsAnimalContainmentPlanner struct {
	reviewer *Rounder
	native   RoundsBuildingSource
	// building raises the marker, barn and vet room (stagePaddock, stageHerdRooms).
	building *RoundsBuildingPlanner
}
type RoundsAnimalContainmentResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsAnimalContainmentPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsAnimalContainmentPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsAnimalContainmentPlanner: reviewer == nil || native == nil", ErrControl)
	}
	building := &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainAnimalContainment}
	return &RoundsAnimalContainmentPlanner{reviewer: reviewer, native: native, building: building}, nil
}

// penPlanOpen reports whether the plan is still building the pen: it places a
// fence, gate or marker and has not finished. The pen is not reconciled again
// until it has, or the diff would offer cells already under construction.
func penPlanOpen(spec domain.PlanSpec, plan store.PlanState) bool {
	if !store.PlanOpen(plan) {
		return false
	}
	for _, action := range spec.Actions() {
		if b, ok := action.Building(); ok {
			switch b.Definition() {
			case policy.PenMarkerDefinition:
				return true
			}
		}
	}
	return false
}

// animalHandlerAvailable ports the enabled-Handling-worker prerequisite:
// dead/downed/drafted/mental-state pawns are already excluded from Available,
// so this only asks whether any remaining pawn has an enabled, prioritized
// Handling work assignment. Any unresolved availability or work fact makes the
// whole census unknown rather than silently skipping that pawn.
func animalHandlerAvailable(pawns []policy.WorkPawn) domain.Fact[bool] {
	known := true
	available := false
	for _, p := range pawns {
		avail, ak := p.Available.Value()
		if !ak {
			known = false
			continue
		}
		if !avail {
			continue
		}
		work, wk := p.Work.Value()
		if !wk {
			known = false
			continue
		}
		for _, w := range work {
			if w.Work == "Handling" && !w.Disabled && w.Priority > 0 {
				available = true
			}
		}
	}
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(available)
}

func animalContainmentDefinition(definitions []observation.PlanningDefinition, name string) (observation.PlanningDefinition, bool) {
	for _, d := range definitions {
		if d.Name == name {
			return d, true
		}
	}
	return observation.PlanningDefinition{}, false
}

// animalContainmentStuff ports enclosure_site's shared-material requirement:
// Fence and FenceGate must observe the same optional stuff, known or not.
func animalContainmentStuff(a, b observation.PlanningDefinition) (string, bool) {
	return observation.SharedStuff(a, b)
}

// shellSharedStuff is the one stuff a shell raised from a and b is built
// from. The tier ladder's wall stuff comes first (wood at Camp, stone blocks
// after), then the cheapest stuff the stock covers; only a colony stocking
// none falls back to the cheapest allowed whatever the stock. Ranking by
// market value alone picked Bioferrite, which no colony holds.
func shellSharedStuff(facts observation.ColonyProjection, a, b observation.PlanningDefinition) (string, bool) {
	if want := shellStyle(facts).WallStuff(domain.ShellRun); want != "" && len(a.StuffOptions) > 0 && a.MakeableFrom(want) && b.MakeableFrom(want) {
		return want, true
	}
	if stock, known := facts.Stock(); known {
		if price, err := a.StuffChoice(observation.CheapestStuff, stock); err == nil && b.MakeableFrom(price.Stuff) {
			return price.Stuff, true
		}
	}
	return animalContainmentStuff(a, b)
}

// containmentWait is the refusal of a containment step that waits on the
// handler, the native pen or the shell it needs.
func containmentWait(reason policy.AnimalContainmentReason) Verdict {
	switch reason {
	case policy.ContainmentWaitingHandler:
		return noWorker("animal_handler")
	case policy.ContainmentWaitingNativePen:
		return awaitingPlan("native_pen", "delivery")
	case policy.ContainmentMarkerExhausted:
		return awaitingPlan("native_pen", "marker_placed")
	case policy.ContainmentBuildShell, policy.ContainmentAwaitingShell:
		return awaitingPlan("paddock", "wall_closed")
	}
	return awaitingPlan("pen", string(reason))
}

// animalContainmentDevelopmentGated reports whether an unselected
// low-priority goal must wait for development: only an unclosed wall does. Once
// the wall stands and its lane is fenced, the PenMarker is the step that makes
// the yard a working pen, so a development row refusing Construction labor
// never strands a closed yard without a marker.
func animalContainmentDevelopmentGated(priority int, selected bool, reason policy.AnimalContainmentReason) bool {
	return priority >= 3 && !selected && reason == policy.ContainmentBuildShell
}

func (r *RoundsAnimalContainmentPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsAnimalContainmentResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsAnimalContainmentResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsAnimalContainmentResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsAnimalContainmentResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainAnimalContainment)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	if !workable {
		return RoundsAnimalContainmentResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Concern == policy.MaintainAnimalContainment && row.Selected
	}
	penBuilding := false
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsAnimalContainmentResult{}, err
		}
		penBuilding = penBuilding || penPlanOpen(plan.Spec, plan)
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsAnimalContainmentResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, "PenMarker")
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	facts := read.Projection
	animals, animalsKnown := facts.Facts.AnimalUpkeep.Animals.Value()
	if !animalsKnown {
		return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("animal_upkeep")}, nil
	}
	workPawns, workKnown := facts.WorkPawns.Value()
	if !workKnown {
		return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("work_pawns")}, nil
	}
	handlerAvailable := animalHandlerAvailable(workPawns)
	if _, known := handlerAvailable.Value(); !known {
		return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("animal_handler")}, nil
	}
	// The pen is the yard inside the defensive wall (#2233): the shell stage
	// is the ring standing with its lane fenced, the marker the one step
	// the yard then needs.
	shellStage, paddock, sited, err := r.paddockStageOf(call, state, facts, penBuilding)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	markerStands := shellStage == policy.ContainmentShellComplete && paddock.Marker
	choice, err := policy.SelectAnimalContainmentMethod(animals, handlerAvailable, shellStage, markerStands)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	if animalContainmentDevelopmentGated(goal.Standard.Priority, selected, choice.Reason) {
		return RoundsAnimalContainmentResult{Verdict: awaitingSlot(string(policy.MaintainAnimalContainment))}, nil
	}
	if choice.Reason == policy.ContainmentNoDeficit {
		// The pen stands (or no animal needs one): the barn and vet room.
		return r.stageHerdRooms(call, epoch, state, review, goal, expected, claims)
	}
	switch choice.Reason {
	case policy.ContainmentBuildShell, policy.ContainmentAwaitingShell:
		// Barn-bound until the wall closes: the barn is raised meanwhile.
		if result, err := r.stageHerdRooms(call, epoch, state, review, goal, expected, claims); err != nil || result.Verdict != BuildingReasonNoDeficit {
			return result, err
		}
		return RoundsAnimalContainmentResult{Verdict: containmentWait(choice.Reason)}, nil
	case policy.ContainmentWaitingHandler, policy.ContainmentWaitingNativePen,
		policy.ContainmentMarkerExhausted:
		return RoundsAnimalContainmentResult{Verdict: containmentWait(choice.Reason)}, nil
	case policy.ContainmentPlaceMarker:
		if !sited {
			return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("room_ground")}, nil
		}
		return r.stagePaddock(call, epoch, state, review, goal, read, paddock)
	}
	return RoundsAnimalContainmentResult{}, fmt.Errorf("%w: step: unknown containment reason %s", ErrControl, choice.Reason)
}
