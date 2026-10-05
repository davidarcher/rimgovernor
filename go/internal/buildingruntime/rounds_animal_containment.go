package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsAnimalContainmentPlanner composes MaintainAnimalContainment's
// containment-method decision (policy.SelectAnimalContainmentMethod) with the
// build side of the reconciler: the plan's ReservePen is a PlannedRoom with an
// outdoor ring (policy.NextPenStep), reconciled through reconcileRoom like the
// barn and vet room (stageHerdRooms). It is self-contained the way
// RoundsFieldPlanner is, on purpose: the shared shelter/cooking/comfort switch
// is actively edited by parallel building-family slices, and this goal's action
// family needs none of its machinery.
type RoundsAnimalContainmentPlanner struct {
	reviewer *Rounder
	native   RoundsBuildingSource
	// building raises the pen, barn and vet room (stagePen, stageHerdRooms).
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
	building := &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainAnimalContainment, definition: "Wall", shelter: true}
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
			case policy.PenFenceDefinition, policy.PenGateDefinition, policy.PenMarkerDefinition:
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
	case policy.ContainmentExceedsBound:
		return awaitingPlan("pen", "herd_exceeds_planning_limit")
	case policy.ContainmentAwaitingShell:
		return awaitingPlan("pen_shell", "completion")
	}
	return awaitingPlan("pen", string(reason))
}

// animalContainmentDevelopmentGated reports whether an unselected
// low-priority goal must wait for development: only a pen ring that does not
// match the plan does. Once the ring stands, its PenMarker is the step that
// makes it a working pen, so a development row refusing Construction labor (the
// ring's own bottleneck) never strands a finished fence ring without a marker.
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
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, "Fence", "FenceGate", "PenMarker")
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
	// The pen is the plan's ReservePen viewed as an outdoor room: its ring
	// stage and marker come from the same diff the reconciler builds from.
	pen, sited := penOf(facts)
	shellStage, markerStands := policy.ContainmentShellNone, false
	switch {
	case penBuilding:
		shellStage = policy.ContainmentShellPending
	case sited && pen.Ring:
		shellStage, markerStands = policy.ContainmentShellComplete, pen.Marker
	}
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
	case policy.ContainmentWaitingHandler, policy.ContainmentWaitingNativePen,
		policy.ContainmentExceedsBound, policy.ContainmentAwaitingShell, policy.ContainmentMarkerExhausted:
		return RoundsAnimalContainmentResult{Verdict: containmentWait(choice.Reason)}, nil
	case policy.ContainmentBuildShell, policy.ContainmentPlaceMarker:
		return r.stagePen(call, epoch, state, review, goal, read, pen, sited)
	}
	return RoundsAnimalContainmentResult{}, fmt.Errorf("%w: step: unknown containment reason %s", ErrControl, choice.Reason)
}

// penOf is the projection's next pen step; false while the plan holds no pen
// site or a fact it reads (the plan, the construction census, the marker's
// size) is unknown.
func penOf(facts observation.ColonyProjection) (policy.PenStep, bool) {
	plan, pk := facts.LayoutPlan.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	ground, gk := colonyGround(facts)
	if !pk || !ck || !gk {
		return policy.PenStep{}, false
	}
	var marker policy.InteriorPieceDef
	if d, found := animalContainmentDefinition(facts.Definitions, policy.PenMarkerDefinition); found {
		if size, known := d.Size.Value(); known && size.Width >= 1 && size.Height >= 1 {
			marker = policy.InteriorPieceDef{Def: d.Name, Size: domain.Cell{X: size.Width, Z: size.Height}}
		}
	}
	return policy.NextPenStep(plan, plan.GroundWithRock(ground, naturalRock(facts)), census.Buildings, marker)
}

// stagePen builds the pen on the plan's ReservePen: its fence ring, gate and
// marker through the shared build side (reconcileRoom), a lost fence rebuilt
// by the same diff as a first ring. The planner sites the pen, so a ring cell
// the native preview refuses is reported (noSpace) and left to the plan's
// next replan; no other site is tried.
func (r *RoundsAnimalContainmentPlanner) stagePen(call, epoch context.Context, state ControlState, review store.Rounds, goal store.StandardState, read observation.RoundsReading, pen policy.PenStep, sited bool) (RoundsAnimalContainmentResult, error) {
	facts := read.Projection
	if _, known := facts.LayoutPlan.Value(); !known {
		return RoundsAnimalContainmentResult{Verdict: BuildingNoLayoutPlan}, nil
	}
	if !sited {
		if plan, _ := facts.LayoutPlan.Value(); len(plan.HerdRooms(policy.PlannedPen)) == 0 {
			return RoundsAnimalContainmentResult{Verdict: noSpace("pen_enclosure")}, nil
		}
		return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("room_ground")}, nil
	}
	if !pen.Ring {
		fenceDef, fok := animalContainmentDefinition(facts.Definitions, policy.PenFenceDefinition)
		gateDef, gok := animalContainmentDefinition(facts.Definitions, policy.PenGateDefinition)
		if !fok || !gok {
			return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("fence_definitions")}, nil
		}
		favail, fak := fenceDef.Available.Value()
		gavail, gak := gateDef.Available.Value()
		if !fak || !gak {
			return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("fence_availability")}, nil
		}
		if !favail || !gavail {
			return RoundsAnimalContainmentResult{Verdict: awaitingPlan("fence", "unbuildable")}, nil
		}
	}
	if !pen.Marker {
		markerDef, ok := animalContainmentDefinition(facts.Definitions, policy.PenMarkerDefinition)
		if !ok {
			return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("pen_marker_definition")}, nil
		}
		avail, ak := markerDef.Available.Value()
		if !ak {
			return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("pen_marker_availability")}, nil
		}
		if !avail {
			return RoundsAnimalContainmentResult{Verdict: awaitingPlan("pen_marker", "unbuildable")}, nil
		}
	}
	in := pen.Room.Interior
	stock := newPackedStock(r.reviewer.native, boundary.Identity(state.Snapshot))
	result, err := r.building.reconcileRoom(call, epoch, state, review, goal, read, stock, roomReconcile{
		room: pen.Room, template: pen.Template,
		name: fmt.Sprintf("pen-%d-%d", in.X, in.Z), reason: string(pen.Room.Role),
	})
	return RoundsAnimalContainmentResult{Verdict: result.Verdict}, err
}
