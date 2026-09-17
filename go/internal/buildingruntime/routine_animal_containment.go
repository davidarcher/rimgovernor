package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineAnimalContainmentPlanner composes MaintainAnimalContainment's
// containment-method decision (policy.SelectAnimalContainmentMethod) into a
// durable shell-then-marker build, reusing the existing plain-BuildingAction
// preview/admission path (Fence/FenceGate/PenMarker) rather than folding into
// the shared shelter/cooking/comfort switch (RoutineBuildingPlanner). It is
// self-contained the way RoutineFoodStoragePlanner and RoutineFieldPlanner are,
// on purpose: the shared switch is actively edited by parallel building-family
// slices, and this goal's action family needs none of its machinery.
type RoutineAnimalContainmentPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineBuildingSource
}
type RoutineAnimalContainmentResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineAnimalContainmentPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineAnimalContainmentPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineAnimalContainmentPlanner{reviewer: reviewer, native: native}, nil
}
func (r *RoutineAnimalContainmentPlanner) Step(ctx context.Context) (RoutineAnimalContainmentResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

const (
	animalShellMethod  domain.MethodID = "pen-shell"
	animalMarkerMethod domain.MethodID = "pen-marker"
)

type animalContainmentPlanKind int

const (
	animalContainmentPlanOther animalContainmentPlanKind = iota
	animalContainmentPlanShell
	animalContainmentPlanMarker
)

// animalContainmentPlanKind recovers what a previously committed method built
// from its own actions rather than trusting a naming convention: a shell plan
// is whichever one placed Fence/FenceGate, a marker plan whichever placed
// PenMarker. The shell's bounding box (needed to search inside it for a marker
// spot) is recovered the same way starterRoom recovers the starter shell's
// footprint from durable claims, since native placement legality already
// decided which cells actually became the shell.
func animalContainmentPlanKindOf(spec domain.PlanSpec) (animalContainmentPlanKind, policy.Rectangle) {
	var minX, minZ, maxX, maxZ int32
	have, shell, marker := false, false, false
	for _, action := range spec.Actions() {
		b, ok := action.Building()
		if !ok {
			continue
		}
		switch b.Definition() {
		case "Fence", "FenceGate":
			shell = true
			c := b.Cell()
			if !have {
				minX, minZ, maxX, maxZ, have = c.X, c.Z, c.X, c.Z, true
			} else {
				minX, maxX = min(minX, c.X), max(maxX, c.X)
				minZ, maxZ = min(minZ, c.Z), max(maxZ, c.Z)
			}
		case "PenMarker":
			marker = true
		}
	}
	if shell && have {
		return animalContainmentPlanShell, policy.Rectangle{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1}
	}
	if marker {
		return animalContainmentPlanMarker, policy.Rectangle{}
	}
	return animalContainmentPlanOther, policy.Rectangle{}
}

// animalContainmentPlanComplete matches the completed-shell check
// shelterNativeWorkTicks and starterRoom rely on elsewhere: every action must
// be an observed, resolved, completed effect. An empty plan is never complete.
func animalContainmentPlanComplete(plan store.PlanState) bool {
	if len(plan.Progress) == 0 {
		return false
	}
	for _, p := range plan.Progress {
		v := p.View()
		effect, known := v.Effect.Value()
		if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted {
			return false
		}
	}
	return true
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
	as, ak := a.Stuff.Value()
	bs, bk := b.Stuff.Value()
	switch {
	case ak && bk:
		return as, as == bs
	case !ak && !bk:
		return "", true
	default:
		return "", false
	}
}

func (r *RoutineAnimalContainmentPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineAnimalContainmentResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineAnimalContainmentResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainAnimalContainment {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if goal.Goal.Priority >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Goal == policy.MaintainAnimalContainment && row.Selected
		}
		if !selected {
			return RoutineAnimalContainmentResult{Reason: BuildingMethodRefused}, nil
		}
	}
	shellStage := policy.ContainmentShellNone
	markerAttempted := false
	var shellRoom policy.Rectangle
	haveShellRoom := false
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		kind, room := animalContainmentPlanKindOf(plan.Spec)
		switch kind {
		case animalContainmentPlanShell:
			if domain.GoalWorkOpen(plan.Progress) {
				shellStage = policy.ContainmentShellPending
			} else if animalContainmentPlanComplete(plan) {
				shellStage = policy.ContainmentShellComplete
				shellRoom, haveShellRoom = room, true
			}
		case animalContainmentPlanMarker:
			markerAttempted = true
		}
	}
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineAnimalContainmentResult{}, ErrControl
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, "Fence", "FenceGate", "PenMarker")
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	facts := read.Projection
	animals, animalsKnown := facts.Facts.AnimalUpkeep.Animals.Value()
	if !animalsKnown {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodUnknown}, nil
	}
	workPawns, workKnown := facts.WorkPawns.Value()
	if !workKnown {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodUnknown}, nil
	}
	handlerAvailable := animalHandlerAvailable(workPawns)
	if _, known := handlerAvailable.Value(); !known {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodUnknown}, nil
	}
	choice, err := policy.SelectAnimalContainmentMethod(animals, handlerAvailable, shellStage, markerAttempted)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	switch choice.Reason {
	case policy.ContainmentNoDeficit, policy.ContainmentWaitingHandler, policy.ContainmentWaitingNativePen,
		policy.ContainmentExceedsBound, policy.ContainmentAwaitingShell, policy.ContainmentMarkerExhausted:
		return RoutineAnimalContainmentResult{Reason: RoutineBuildingReason(choice.Reason)}, nil
	case policy.ContainmentBuildShell:
	case policy.ContainmentPlaceMarker:
		if !haveShellRoom {
			return RoutineAnimalContainmentResult{}, ErrControl
		}
	default:
		return RoutineAnimalContainmentResult{}, ErrControl
	}
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	if choice.Reason == policy.ContainmentBuildShell {
		return r.buildShell(call, epoch, state, goal, facts, protected, read)
	}
	return r.placeMarker(call, epoch, state, goal, facts, protected, read, shellRoom)
}

// buildShell proposes the durable Fence-then-FenceGate perimeter for the
// nearest legal 6x6 enclosure. Every candidate site is fully previewed before
// any is admitted; a site whose native preview refuses a cell is abandoned in
// favor of the next, exactly like previewShell abandons a StarterLayouts
// candidate that fails partway through its perimeter.
func (r *RoutineAnimalContainmentPlanner) buildShell(call, epoch context.Context, state ControlState, goal store.GoalState, facts observation.ColonyProjection, protected []domain.Cell, read observation.RoutineReading) (RoutineAnimalContainmentResult, error) {
	p := r.reviewer.player
	fenceDef, fok := animalContainmentDefinition(facts.Definitions, "Fence")
	gateDef, gok := animalContainmentDefinition(facts.Definitions, "FenceGate")
	if !fok || !gok {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodUnknown}, nil
	}
	favail, fak := fenceDef.Available.Value()
	gavail, gak := gateDef.Available.Value()
	if !fak || !gak || !favail || !gavail {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodUnknown}, nil
	}
	stuff, known := animalContainmentStuff(fenceDef, gateDef)
	if !known {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodUnknown}, nil
	}
	sites, err := policy.PenEnclosureSites(policy.PenEnclosureRequest{Bounds: facts.Bounds, Anchor: facts.Center, Cells: facts.Cells, Protected: protected})
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, animalShellMethod)))
	planID := domain.PlanID(fmt.Sprintf("routine-pen-shell-%x", digest[:16]))
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	for _, room := range sites {
		actions, previews, stock, reason, err := r.previewPenShell(call, snapshot, room, stuff, facts)
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		if reason == BuildingMethodUnknown {
			return RoutineAnimalContainmentResult{Reason: reason}, nil
		}
		if reason != "" {
			continue
		}
		plan, err := domain.NewPlan(planID, 1, actions)
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		if p.session.State() != state {
			return RoutineAnimalContainmentResult{}, ErrControl
		}
		last, _, err := r.reviewer.native.Identity(call)
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		actual, err := observation.DecodeIdentity(last)
		if err != nil || !routineBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick) {
			return RoutineAnimalContainmentResult{}, ErrControl
		}
		now := r.reviewer.clock.Now()
		if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
			return RoutineAnimalContainmentResult{}, observation.ErrStale
		}
		decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: animalShellMethod, Plan: plan, Current: snapshot, Tick: facts.Identity.Tick, Bounds: domain.Known(facts.Bounds), Stock: stock, Rules: r.reviewer.rules, Previews: previews, Purpose: policy.Routine})
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		outcome := BuildingMethodRefused
		if decision.Admitted {
			outcome = BuildingMethodAdmitted
		}
		return RoutineAnimalContainmentResult{Reason: outcome, Plan: planID}, nil
	}
	return RoutineAnimalContainmentResult{Reason: BuildingMethodNoSpace}, nil
}

// previewPenShell previews one candidate room's full 6x6 perimeter (one
// FenceGate anchoring the south wall's center, Fence elsewhere). It never
// commits: a rejected or infeasible cell aborts only this candidate.
func (r *RoutineAnimalContainmentPlanner) previewPenShell(ctx context.Context, snapshot domain.GenerationSnapshot, room policy.Rectangle, stuff string, facts observation.ColonyProjection) ([]domain.Action, []policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	door := domain.Cell{X: room.X + room.Width/2, Z: room.Z}
	perimeter := []domain.Cell{door}
	for x := room.X; x < room.X+room.Width; x++ {
		for z := room.Z; z < room.Z+room.Height; z++ {
			cell := domain.Cell{X: x, Z: z}
			if cell != door && (x == room.X || x == room.X+room.Width-1 || z == room.Z || z == room.Z+room.Height-1) {
				perimeter = append(perimeter, cell)
			}
		}
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	var actions []domain.Action
	var previews []policy.Preview
	for i, cell := range perimeter {
		definition := "Fence"
		if i == 0 {
			definition = "FenceGate"
		}
		building, err := domain.NewBuilding(definition, cell, domain.North, stuff)
		if err != nil {
			return nil, nil, policy.StockObservation{}, "", err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), building)
		if err != nil {
			return nil, nil, policy.StockObservation{}, "", err
		}
		preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
		if err != nil {
			return nil, nil, policy.StockObservation{}, "", err
		}
		v := preview.Preview
		if v.Action != action || !v.Snapshot.Matches(snapshot) || v.Tick != facts.Identity.Tick || !preview.Stock.Snapshot.Matches(snapshot) || preview.Stock.Tick != facts.Identity.Tick {
			return nil, nil, policy.StockObservation{}, "", ErrControl
		}
		made, madeKnown := v.MadeFromStuff.Value()
		if !madeKnown || made != (stuff != "") {
			return nil, nil, policy.StockObservation{}, BuildingMethodUnknown, nil
		}
		footprint, fk := v.Footprint.Value()
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		if !fk || len(footprint) != 1 || footprint[0] != cell || !ck || !can || !sk || !safe {
			return nil, nil, policy.StockObservation{}, BuildingMethodNoSpace, nil
		}
		if err := mergeRoutineStock(&stock, preview.Stock, i == 0); err != nil {
			return nil, nil, policy.StockObservation{}, "", err
		}
		actions = append(actions, action)
		previews = append(previews, v)
	}
	return actions, previews, stock, "", nil
}

// placeMarker searches the completed shell's interior for a legal PenMarker
// spot, nearest its northwest interior corner within radius 4.
func (r *RoutineAnimalContainmentPlanner) placeMarker(call, epoch context.Context, state ControlState, goal store.GoalState, facts observation.ColonyProjection, protected []domain.Cell, read observation.RoutineReading, room policy.Rectangle) (RoutineAnimalContainmentResult, error) {
	p := r.reviewer.player
	markerDef, ok := animalContainmentDefinition(facts.Definitions, "PenMarker")
	if !ok {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodUnknown}, nil
	}
	avail, ak := markerDef.Available.Value()
	if !ak || !avail {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodUnknown}, nil
	}
	stuff := ""
	if s, sk := markerDef.Stuff.Value(); sk {
		stuff = s
	}
	var cells []policy.SiteCell
	for _, c := range facts.Cells {
		if c.Cell.X > room.X && c.Cell.X < room.X+room.Width-1 && c.Cell.Z > room.Z && c.Cell.Z < room.Z+room.Height-1 {
			cells = append(cells, c)
		}
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, animalMarkerMethod)))
	planID := domain.PlanID(fmt.Sprintf("routine-pen-marker-%x", digest[:16]))
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	center := domain.Cell{X: room.X + 1, Z: room.Z + 1}
	search, err := policy.NewPlacementSearch(policy.PlacementSearchRequest{Snapshot: snapshot, Tick: facts.Identity.Tick, Bounds: facts.Bounds, Center: center, Cells: cells, Protected: protected, Environment: policy.PlacementAnywhere, Radius: 4, Limit: 64})
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	var chosen policy.Preview
	var chosenStock policy.StockObservation
	found := false
	for i, c := range search.Candidates() {
		building, err := domain.NewBuilding("PenMarker", c, domain.North, stuff)
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", planID, i)), building)
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		preview, _, err := r.native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		v := preview.Preview
		if v.Action != action || !v.Snapshot.Matches(snapshot) || v.Tick != facts.Identity.Tick || !preview.Stock.Snapshot.Matches(snapshot) || preview.Stock.Tick != facts.Identity.Tick {
			return RoutineAnimalContainmentResult{}, ErrControl
		}
		made, mk := v.MadeFromStuff.Value()
		if !mk || made != (stuff != "") {
			continue
		}
		choice, ok, err := search.Select("PenMarker", stuff, []policy.Preview{v})
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		if !ok {
			continue
		}
		chosen, found = choice, true
		chosenStock = stock
		if err := mergeRoutineStock(&chosenStock, preview.Stock, true); err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		break
	}
	if !found {
		return RoutineAnimalContainmentResult{Reason: BuildingMethodNoSpace}, nil
	}
	plan, err := domain.NewPlan(planID, 1, []domain.Action{chosen.Action})
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if p.session.State() != state {
		return RoutineAnimalContainmentResult{}, ErrControl
	}
	last, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	actual, err := observation.DecodeIdentity(last)
	if err != nil || !routineBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick) {
		return RoutineAnimalContainmentResult{}, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineAnimalContainmentResult{}, observation.ErrStale
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: animalMarkerMethod, Plan: plan, Current: snapshot, Tick: facts.Identity.Tick, Bounds: domain.Known(facts.Bounds), Stock: chosenStock, Rules: r.reviewer.rules, Previews: []policy.Preview{chosen}, Purpose: policy.Routine})
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	outcome := BuildingMethodRefused
	if decision.Admitted {
		outcome = BuildingMethodAdmitted
	}
	return RoutineAnimalContainmentResult{Reason: outcome, Plan: planID}, nil
}
