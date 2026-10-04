package buildingruntime

import (
	"context"
	"fmt"
	"log/slog"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// RoutineAnimalContainmentPlanner composes MaintainAnimalContainment's
// containment-method decision (policy.SelectAnimalContainmentMethod) into a
// durable shell-then-marker build, reusing the existing plain-BuildingAction
// preview/admission path (Fence/FenceGate/PenMarker) rather than folding into
// the shared shelter/cooking/comfort switch (RoutineBuildingPlanner). It is
// self-contained the way RoutineFieldPlanner is,
// on purpose: the shared switch is actively edited by parallel building-family
// slices, and this goal's action family needs none of its machinery.
type RoutineAnimalContainmentPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineBuildingSource
	// building raises the barn and vet room (stageHerdRooms).
	building *RoutineBuildingPlanner
}
type RoutineAnimalContainmentResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutineAnimalContainmentPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineAnimalContainmentPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineAnimalContainmentPlanner: reviewer == nil || native == nil", ErrControl)
	}
	building := &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.MaintainAnimalContainment, definition: "Wall", shelter: true}
	return &RoutineAnimalContainmentPlanner{reviewer: reviewer, native: native, building: building}, nil
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
// PenMarker. The shell's room (needed to search inside it for a marker spot)
// is the PenEnclosureSize square its gate anchors: the gate is the middle of
// the room's first row, and a ring cell left as natural rock has no fence.
func animalContainmentPlanKindOf(spec domain.PlanSpec) (animalContainmentPlanKind, policy.Rectangle) {
	var gate domain.Cell
	shell, haveGate, marker := false, false, false
	for _, action := range spec.Actions() {
		b, ok := action.Building()
		if !ok {
			continue
		}
		switch b.Definition() {
		case "Fence":
			shell = true
		case "FenceGate":
			shell, haveGate, gate = true, true, b.Cell()
		case "PenMarker":
			marker = true
		}
	}
	if shell && haveGate {
		size := policy.PenEnclosureSize
		return animalContainmentPlanShell, policy.Rectangle{X: gate.X - size/2, Z: gate.Z, Width: size, Height: size}
	}
	if marker {
		return animalContainmentPlanMarker, policy.Rectangle{}
	}
	return animalContainmentPlanOther, policy.Rectangle{}
}

// animalContainmentPlanComplete matches the completed-shell check
// shelterNativeWorkTicks relies on elsewhere: every action must
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

// animalContainmentDevelopmentGated reports whether an unselected
// low-priority goal must wait for development: only a new shell does. Once
// a shell stands, its PenMarker is the step that makes it a working pen, so
// a development row refusing Construction labor (the ring's own bottleneck)
// never strands a finished fence ring without a marker.
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

func animalContainmentDevelopmentGated(priority int, selected bool, reason policy.AnimalContainmentReason) bool {
	return priority >= 3 && !selected && reason == policy.ContainmentBuildShell
}

func (r *RoutineAnimalContainmentPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineAnimalContainmentResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineAnimalContainmentResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineAnimalContainmentResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineAnimalContainmentResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainAnimalContainment)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if !workable {
		return RoutineAnimalContainmentResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainAnimalContainment && row.Selected
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
			if store.PlanOpen(plan) {
				shellStage = policy.ContainmentShellPending
			} else if animalContainmentPlanComplete(plan) {
				shellStage = policy.ContainmentShellComplete
				shellRoom, haveShellRoom = room, true
			}
		case animalContainmentPlanMarker:
			markerAttempted = true
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineAnimalContainmentResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
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
		return RoutineAnimalContainmentResult{Verdict: fieldUnavailable("animal_upkeep")}, nil
	}
	workPawns, workKnown := facts.WorkPawns.Value()
	if !workKnown {
		return RoutineAnimalContainmentResult{Verdict: fieldUnavailable("work_pawns")}, nil
	}
	handlerAvailable := animalHandlerAvailable(workPawns)
	if _, known := handlerAvailable.Value(); !known {
		return RoutineAnimalContainmentResult{Verdict: fieldUnavailable("animal_handler")}, nil
	}
	choice, err := policy.SelectAnimalContainmentMethod(animals, handlerAvailable, shellStage, markerAttempted)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if animalContainmentDevelopmentGated(goal.Goal.Priority, selected, choice.Reason) {
		return RoutineAnimalContainmentResult{Verdict: awaitingSlot(string(policy.MaintainAnimalContainment))}, nil
	}
	if choice.Reason == policy.ContainmentNoDeficit {
		// The pen stands (or no animal needs one): the barn and vet room.
		return r.stageHerdRooms(call, epoch, state, review, goal, expected, claims)
	}
	switch choice.Reason {
	case policy.ContainmentWaitingHandler, policy.ContainmentWaitingNativePen,
		policy.ContainmentExceedsBound, policy.ContainmentAwaitingShell, policy.ContainmentMarkerExhausted:
		return RoutineAnimalContainmentResult{Verdict: containmentWait(choice.Reason)}, nil
	case policy.ContainmentBuildShell:
	case policy.ContainmentPlaceMarker:
		if !haveShellRoom {
			return RoutineAnimalContainmentResult{}, fmt.Errorf("%w: step: !haveShellRoom", ErrControl)
		}
	default:
		return RoutineAnimalContainmentResult{}, fmt.Errorf("%w: step: case policy.ContainmentPlaceMarker", ErrControl)
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
		return r.buildShell(call, epoch, state, review, goal, facts, protected, read)
	}
	return r.placeMarker(call, epoch, state, goal, facts, protected, read, shellRoom)
}

// buildShell proposes the durable Fence-then-FenceGate perimeter for the
// nearest legal 6x6 enclosure. Every candidate site is fully previewed before
// any is admitted; a site whose native preview refuses a cell is abandoned in
// favor of the next, exactly like previewShell abandons a planned room
// candidate that fails partway through its perimeter.
func (r *RoutineAnimalContainmentPlanner) buildShell(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, facts observation.ColonyProjection, protected []domain.Cell, read observation.RoutineReading) (RoutineAnimalContainmentResult, error) {
	p := r.reviewer.player
	fenceDef, fok := animalContainmentDefinition(facts.Definitions, "Fence")
	gateDef, gok := animalContainmentDefinition(facts.Definitions, "FenceGate")
	if !fok || !gok {
		return RoutineAnimalContainmentResult{Verdict: fieldUnavailable("fence_definitions")}, nil
	}
	favail, fak := fenceDef.Available.Value()
	gavail, gak := gateDef.Available.Value()
	if !fak || !gak {
		return RoutineAnimalContainmentResult{Verdict: fieldUnavailable("fence_availability")}, nil
	}
	if !favail || !gavail {
		return RoutineAnimalContainmentResult{Verdict: awaitingPlan("fence", "unbuildable")}, nil
	}
	stuff, known := shellSharedStuff(facts, fenceDef, gateDef)
	if !known {
		return RoutineAnimalContainmentResult{Verdict: fieldUnavailable("fence_stuff")}, nil
	}
	sites, err := policy.PenEnclosureSites(policy.PenEnclosureRequest{Bounds: facts.Bounds, Anchor: fieldAnchor(facts), Cells: facts.Cells, Protected: protected})
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	planID := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	for _, room := range sites {
		actions, previews, stock, reason, err := r.previewPenShell(call, snapshot, room, stuff, facts)
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		if reason.Is(RefusalFieldUnavailable) {
			return RoutineAnimalContainmentResult{Verdict: reason}, nil
		}
		if !reason.IsZero() {
			continue
		}
		// The ring is one ungated wave, and like the starter shell it is
		// admitted as Shelter work: fences and gate are placed regardless
		// of stock and hold natively for materials (#602).
		plan, err := domain.NewPlan(planID, 1, actions)
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		if p.session.State() != state {
			return RoutineAnimalContainmentResult{}, fmt.Errorf("%w: buildShell: p.session.State() != state", ErrControl)
		}
		actual, err := stepScope(call, r.reviewer.native)
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		if !routineBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick) {
			return RoutineAnimalContainmentResult{}, fmt.Errorf("%w: buildShell: !routineBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick)", ErrControl)
		}
		now := r.reviewer.clock.Now()
		if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
			return RoutineAnimalContainmentResult{}, observation.ErrStale
		}
		decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Owner: goal, Method: animalShellMethod, Plan: plan, Current: snapshot, Tick: facts.Identity.Tick, Bounds: domain.Known(facts.Bounds), Stock: stock, Previews: previews, Purpose: policy.Shelter})
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		outcome := admissionRefused(decision)
		if decision.Admitted {
			outcome = BuildingReasonAdmitted
		}
		return RoutineAnimalContainmentResult{Verdict: outcome, Plan: planID}, nil
	}
	return r.digShell(call, epoch, state, review, goal, facts, protected, read, stuff)
}

// penShellCells lists a pen ring's building cells with the gate first, in
// the order previewPenShell previews and admits them.
func penShellCells(room policy.Rectangle) (gate domain.Cell, ring []domain.Cell) {
	gate = domain.Cell{X: room.X + room.Width/2, Z: room.Z}
	ring = []domain.Cell{gate}
	for x := room.X; x < room.X+room.Width; x++ {
		for z := room.Z; z < room.Z+room.Height; z++ {
			cell := domain.Cell{X: x, Z: z}
			if cell != gate && (x == room.X || x == room.X+room.Width-1 || z == room.Z || z == room.Z+room.Height-1) {
				ring = append(ring, cell)
			}
		}
	}
	return gate, ring
}

// digShell sites the pen where no open ground fits: the picker sees rock and
// fogged cells near the anchor as open (policy.RockSiteView), a fence cell on
// rock stays rock, and the gate cell and the interior are mined before the
// rest of the ring is built, all in one shell method through the shared rock
// step. The gate opens onto ground the frame lists open, which a miner and
// the animals reach.
func (r *RoutineAnimalContainmentPlanner) digShell(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, facts observation.ColonyProjection, protected []domain.Cell, read observation.RoutineReading, stuff string) (RoutineAnimalContainmentResult, error) {
	anchor := fieldAnchor(facts)
	reach := policy.RockSiteReach
	view := policy.RockSiteView(facts.Cells, facts.Bounds, policy.Rectangle{X: anchor.X - reach, Z: anchor.Z - reach, Width: 2*reach + 1, Height: 2*reach + 1})
	sites, err := policy.PenEnclosureSites(policy.PenEnclosureRequest{Bounds: facts.Bounds, Anchor: anchor, Cells: view, Protected: protected, Entrance: facts.Cells})
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	step := excavationStep{state: state, review: review, goal: goal, facts: facts, read: read.ColonyReading}
	check := func() error {
		if err := r.reviewer.player.current(call, epoch); err != nil {
			return err
		}
		if r.reviewer.player.session.State() != state {
			return fmt.Errorf("%w: digShell: p.session.State() != state", ErrControl)
		}
		return nil
	}
	for _, room := range sites {
		gate, ring := penShellCells(room)
		outside := domain.Cell{X: gate.X, Z: gate.Z - 1}
		var planned []policy.RoleCell
		for _, cell := range ring {
			role := policy.RockBlocks
			if cell == gate {
				role = policy.RockNeedsFloor
			}
			planned = append(planned, policy.RoleCell{Cell: cell, Role: role})
		}
		for x := room.X + 1; x < room.X+room.Width-1; x++ {
			for z := room.Z + 1; z < room.Z+room.Height-1; z++ {
				planned = append(planned, policy.RoleCell{Cell: domain.Cell{X: x, Z: z}, Role: policy.RockNeedsFloor})
			}
		}
		rock := policy.RockStep(planned, facts.Cells)
		if len(rock.Dig) == 0 {
			continue
		}
		left := make(map[domain.Cell]bool, len(rock.Left))
		for _, cell := range rock.Left {
			left[cell] = true
		}
		var buildings []domain.Building
		for _, cell := range ring {
			if left[cell] {
				continue
			}
			definition := "Fence"
			if cell == gate {
				definition = "FenceGate"
			}
			building, err := domain.NewBuilding(definition, cell, domain.North, stuff)
			if err != nil {
				return RoutineAnimalContainmentResult{}, err
			}
			buildings = append(buildings, building)
		}
		result, handled, err := r.building.admitRockStep(call, epoch, step, planned, outside, animalShellMethod, buildings, check)
		if err != nil {
			return RoutineAnimalContainmentResult{}, err
		}
		if !handled {
			continue
		}
		out := RoutineAnimalContainmentResult{Verdict: result.Verdict}
		for _, m := range result.Decision.Goal.Methods {
			if m.Method == animalShellMethod {
				out.Plan = m.Plan
			}
		}
		return out, nil
	}
	return RoutineAnimalContainmentResult{Verdict: noSpace("pen_enclosure")}, nil
}

// previewPenShell previews one candidate room's full 6x6 perimeter (one
// FenceGate anchoring the south wall's center, Fence elsewhere). It never
// commits: a rejected or infeasible cell aborts only this candidate.
func (r *RoutineAnimalContainmentPlanner) previewPenShell(ctx context.Context, snapshot domain.GenerationSnapshot, room policy.Rectangle, stuff string, facts observation.ColonyProjection) ([]domain.Action, []policy.Preview, policy.StockObservation, Verdict, error) {
	_, perimeter := penShellCells(room)
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
			return nil, nil, policy.StockObservation{}, Verdict{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), building)
		if err != nil {
			return nil, nil, policy.StockObservation{}, Verdict{}, err
		}
		preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
		if err != nil {
			return nil, nil, policy.StockObservation{}, Verdict{}, err
		}
		v := preview.Preview
		made, madeKnown := v.MadeFromStuff.Value()
		if !madeKnown || made != (stuff != "") {
			return nil, nil, policy.StockObservation{}, fieldUnavailable("pen_shell_preview"), nil
		}
		footprint, fk := v.Footprint.Value()
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		if !fk || len(footprint) != 1 || footprint[0] != cell || !ck || !can || !sk || !safe {
			return nil, nil, policy.StockObservation{}, noSpace("pen_enclosure"), nil
		}
		if err := mergeRoutineStock(&stock, preview.Stock, i == 0); err != nil {
			return nil, nil, policy.StockObservation{}, Verdict{}, err
		}
		actions = append(actions, action)
		previews = append(previews, v)
	}
	return actions, previews, stock, Verdict{}, nil
}

// placeMarker searches the completed shell's interior for a legal PenMarker
// spot, nearest its northwest interior corner within radius 4.
func (r *RoutineAnimalContainmentPlanner) placeMarker(call, epoch context.Context, state ControlState, goal store.GoalState, facts observation.ColonyProjection, protected []domain.Cell, read observation.RoutineReading, room policy.Rectangle) (RoutineAnimalContainmentResult, error) {
	p := r.reviewer.player
	markerDef, ok := animalContainmentDefinition(facts.Definitions, "PenMarker")
	if !ok {
		return RoutineAnimalContainmentResult{Verdict: fieldUnavailable("pen_marker_definition")}, nil
	}
	avail, ak := markerDef.Available.Value()
	if !ak {
		return RoutineAnimalContainmentResult{Verdict: fieldUnavailable("pen_marker_availability")}, nil
	}
	if !avail {
		return RoutineAnimalContainmentResult{Verdict: awaitingPlan("pen_marker", "unbuildable")}, nil
	}
	stuff := facts.BuildStuff("PenMarker")
	var cells []policy.SiteCell
	for _, c := range facts.Cells {
		if c.Cell.X > room.X && c.Cell.X < room.X+room.Width-1 && c.Cell.Z > room.Z && c.Cell.Z < room.Z+room.Height-1 {
			cells = append(cells, c)
		}
	}
	planID := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	center := domain.Cell{X: room.X + 1, Z: room.Z + 1}
	markerSearch := policy.PlacementSearchRequest{Snapshot: snapshot, Tick: facts.Identity.Tick, Bounds: facts.Bounds, Center: center, Cells: cells, Protected: protected, Environment: policy.PlacementAnywhere, Radius: 4, Limit: 64}
	search, err := policy.NewPlacementSearch(markerSearch)
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
		// A finished ring without a marker is not a pen; say why the
		// interior refused one instead of idling silently.
		slog.Default().Warn("pen marker found no legal interior cell", telemetry.ComponentKey, "animal-containment",
			"shell", fmt.Sprintf("%d,%d %dx%d", room.X, room.Z, room.Width, room.Height), "interior_cells", len(cells), "candidates", len(search.Candidates()))
		return RoutineAnimalContainmentResult{Verdict: noSpace("pen_marker")}, nil
	}
	plan, err := domain.NewPlan(planID, 1, []domain.Action{chosen.Action})
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if p.session.State() != state {
		return RoutineAnimalContainmentResult{}, fmt.Errorf("%w: placeMarker: p.session.State() != state", ErrControl)
	}
	actual, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if !routineBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick) {
		return RoutineAnimalContainmentResult{}, fmt.Errorf("%w: placeMarker: !routineBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick)", ErrControl)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineAnimalContainmentResult{}, observation.ErrStale
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Owner: goal, Method: animalMarkerMethod, Plan: plan, Current: snapshot, Tick: facts.Identity.Tick, Bounds: domain.Known(facts.Bounds), Stock: chosenStock, Previews: []policy.Preview{chosen}, Purpose: policy.Routine})
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	outcome := admissionRefused(decision)
	if decision.Admitted {
		outcome = BuildingReasonAdmitted
	}
	return RoutineAnimalContainmentResult{Verdict: outcome, Plan: planID}, nil
}
