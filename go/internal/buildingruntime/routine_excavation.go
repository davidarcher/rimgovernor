package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoutineExcavationSource verifies a proposed excavation target against the
// live map: per-cell rock/eligibility, site-level roof support after the
// counterfactual removal, and a miner who can reach the access cell. General
// colony facts (visible cells, roof defs, definitions) come from the shared
// RoutineBuildingSource read.
type RoutineExcavationSource interface {
	ReadExcavationSite(context.Context, *c.Identity, []domain.Cell, domain.Cell) (bridge.ExcavationSite, bridge.Result, error)
}

const (
	excavationPlanPrefix  = "routine-excavation"
	excavationStagePrefix = "excavation-stage-"
	excavationDoorMethod  = domain.MethodID("excavation-door")
	excavationStageLimit  = 8
	excavationStageBound  = 64
	// excavationStallTicks bounds how long a stage action may stay held
	// (unsupported, changed geometry, no way in) before the planner cancels
	// it so the project can be reviewed against the geometry that changed
	// under it; an in-flight stage otherwise reads as open work forever.
	excavationStallTicks   = 2500
	excavationCandidates   = 4
	excavationInteriorSize = 7
	// excavationRoundRadius sizes the round room a neolithic colony digs:
	// 49 cells like the 7×7 rectangle, within the roof support radius.
	excavationRoundRadius = 4
)

// excavationShapes follows shelterStyle: a neolithic colony digs a round
// room first and falls back to the rectangle where the circle is blocked;
// everyone else prefers the rectangle with the circle as fallback. Either
// way the shapes are tried at every face in this order.
func excavationShapes(facts observation.ColonyProjection) []policy.ExcavationShape {
	rectangle := policy.RectangleShape(excavationInteriorSize, excavationInteriorSize)
	round := policy.EllipseShape(excavationRoundRadius, excavationRoundRadius, domain.EllipseNorthSouth)
	if shelterStyle(facts) == policy.ShelterHut {
		return []policy.ExcavationShape{round, rectangle}
	}
	return []policy.ExcavationShape{rectangle, round}
}

func excavationStageMethod(stage int) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("%s%d", excavationStagePrefix, stage))
}

// excavationPlanID scopes a stage plan to the goal epoch that admitted it:
// an invalidated goal's successor re-adopts the same target (same key) and
// must not collide with the retired stage plans.
func excavationPlanID(goal store.GoalState, target policy.ExcavationTarget, suffix string) domain.PlanID {
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d", goal.Goal.ID, goal.Goal.Epoch)))
	return domain.PlanID(fmt.Sprintf("%s-%s-%x-%s", excavationPlanPrefix, target.Key(), digest[:4], suffix))
}

// excavationPlanTarget recovers the target from a stage plan identity so an
// in-progress project survives restarts and retired stage plans without a
// separate table.
func excavationPlanTarget(plan domain.PlanID) (policy.ExcavationTarget, error) {
	rest, ok := strings.CutPrefix(string(plan), excavationPlanPrefix+"-")
	if !ok {
		return policy.ExcavationTarget{}, errors.New("not an excavation plan")
	}
	parts := strings.Split(rest, "-")
	if len(parts) < 3 {
		return policy.ExcavationTarget{}, errors.New("not an excavation plan")
	}
	return policy.ParseExcavationKey(strings.Join(parts[:len(parts)-2], "-"))
}

// ExcavationPlanTarget exposes the stage plan identity scheme to acceptance
// tooling that verifies excavation projects from the durable journal alone.
func ExcavationPlanTarget(plan domain.PlanID) (policy.ExcavationTarget, error) {
	return excavationPlanTarget(plan)
}

// ExcavationStageMethod and ExcavationDoorMethod name the per-stage and door
// methods an excavation project commits under its goal.
func ExcavationStageMethod(stage int) domain.MethodID { return excavationStageMethod(stage) }
func ExcavationDoorMethod() domain.MethodID           { return excavationDoorMethod }

// IsExcavationPlan reports whether plan belongs to the routine excavation
// planner (stage or door).
func IsExcavationPlan(plan domain.PlanID) bool {
	return strings.HasPrefix(string(plan), excavationPlanPrefix+"-")
}

// excavationProject reports the target of the goal's current excavation
// project: the one the most recently admitted stage under this goal epoch
// carries, since a re-sited project continues its stage count under a new
// target key.
func (r *RoutineBuildingPlanner) excavationProject(call context.Context, goal store.GoalState) (*policy.ExcavationTarget, error) {
	var latest *domain.GoalMethod
	for stage := 0; stage <= excavationStageBound; stage++ {
		method, err := r.reviewer.player.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, excavationStageMethod(stage))
		if errors.Is(err, store.ErrNotFound) {
			break
		}
		if err != nil {
			return nil, err
		}
		latest = &method
	}
	if latest == nil {
		return nil, nil
	}
	target, err := excavationPlanTarget(latest.Plan)
	if err != nil {
		return nil, ErrControl
	}
	return &target, nil
}

// readExcavationSite reads the target under the current observation and
// insists the reply belongs to the same generation and tick as the colony
// facts the stage was planned from.
func (r *RoutineBuildingPlanner) readExcavationSite(call context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, cells []domain.Cell, access domain.Cell, check func() error) (bridge.ExcavationSite, error) {
	if err := check(); err != nil {
		return bridge.ExcavationSite{}, err
	}
	site, _, err := r.excavation.ReadExcavationSite(call, boundary.Identity(snapshot), cells, access)
	if err != nil {
		return bridge.ExcavationSite{}, err
	}
	if err := check(); err != nil {
		return bridge.ExcavationSite{}, err
	}
	current, err := boundary.Context(site.Context, snapshot)
	if err != nil || current.Native != snapshot.Native || domain.Tick(site.Context.GetTick()) != tick {
		return bridge.ExcavationSite{}, ErrControl
	}
	return site, nil
}

// excavationCandidate verifies the best geometric target against the live
// map. A target is acceptable when every visible cell is eligible rock, the
// counterfactual removal is not known to be unsupported, and a miner can
// reach the access cell now.
func (r *RoutineBuildingPlanner) excavationCandidate(call context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) (*policy.ExcavationTarget, error) {
	// A project whose goal was replaced mid-way is resumed from its durable
	// stage plans before any fresh face is considered: the colony window
	// follows the pawns, so a half-dug room can fall outside the geometry
	// search entirely while the native site read still verifies it.
	if previous, err := r.previousExcavation(call); err != nil {
		return nil, err
	} else if previous != nil {
		verified, err := r.verifyExcavation(call, snapshot, facts.Identity.Tick, *previous, true, check)
		if err != nil {
			return nil, err
		}
		if verified {
			return previous, nil
		}
	}
	targets, err := policy.ExcavationSites(policy.ExcavationSiteRequest{Bounds: facts.Bounds, Region: facts.Region, Anchor: facts.Center, Cells: facts.Cells, Protected: protected, Shapes: excavationShapes(facts), MinCorridor: 2, MaxCorridor: 4})
	if err != nil {
		return nil, err
	}
	for i, target := range targets {
		if i >= excavationCandidates {
			break
		}
		verified, err := r.verifyExcavation(call, snapshot, facts.Identity.Tick, target, false, check)
		if err != nil {
			return nil, err
		}
		if verified {
			return &target, nil
		}
	}
	return nil, nil
}

// previousExcavation is the target of the most recently planned excavation
// plan under any goal, or nil when none was ever planned or the latest is a
// door plan whose every action completed: that project is finished, and a
// later shelter need starts a new one.
func (r *RoutineBuildingPlanner) previousExcavation(call context.Context) (*policy.ExcavationTarget, error) {
	journal := r.reviewer.player.journal
	plan, err := journal.LatestPlanWithPrefix(call, excavationPlanPrefix+"-")
	if errors.Is(err, store.ErrNotFound) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	target, err := excavationPlanTarget(plan)
	if err != nil {
		return nil, ErrControl
	}
	if strings.HasSuffix(string(plan), "-door") {
		state, err := journal.LoadPlan(call, plan)
		if err != nil {
			return nil, err
		}
		finished := len(state.Progress) > 0
		for _, progress := range state.Progress {
			if effect, known := progress.View().Effect.Value(); !known || effect != domain.EffectCompleted {
				finished = false
			}
		}
		if finished {
			return nil, nil
		}
	}
	return &target, nil
}

// verifyExcavation reads the target under the current observation. A fresh
// candidate is acceptable when every visible cell is eligible rock or
// already cleared, the counterfactual removal is not known to be
// unsupported, and a miner can reach the access cell now. A resumed project
// is judged by what still lets it finish: its way in must be open (access
// reachable, corridor and entrance cleared or diggable) and its roof still
// held; hazards the fog revealed inside the room are dug around, and a
// missing miner is waited for rather than a reason to abandon the dig. A
// resumed project may be fully cleared (its door is still owed); a fresh
// candidate is only proposed by geometry that saw rock.
func (r *RoutineBuildingPlanner) verifyExcavation(call context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, target policy.ExcavationTarget, resumed bool, check func() error) (bool, error) {
	site, err := r.readExcavationSite(call, snapshot, tick, target.Cells(), target.Access, check)
	if err != nil {
		return false, err
	}
	if site.Support == policy.ExcavationSupportUnsupported && !site.CollapsePending || !site.AccessReachable || !resumed && !site.WorkerAvailable {
		clockSchedulerLog("excavation target %s rejected: support=%d (%s) worker=%v access=%v", target.Key(), site.Support, site.SupportBlocker, site.WorkerAvailable, site.AccessReachable)
		return false, nil
	}
	review := policy.ReviewExcavation(target, excavationStates(site), excavationStageLimit)
	if !resumed && len(review.Kept) > 0 || !review.Corridor {
		clockSchedulerLog("excavation target %s rejected: kept=%v corridor=%v", target.Key(), review.Kept, review.Corridor)
		return false, nil
	}
	return true, nil
}

// previewShelter chooses between the open-site starter shell and an
// excavated room. It returns the shell's previews when the shell wins, or a
// non-nil target when excavation does.
func (r *RoutineBuildingPlanner) previewShelter(call context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, *policy.ExcavationTarget, error) {
	target, err := r.excavationCandidate(call, snapshot, facts, protected, check)
	if err != nil {
		return nil, policy.StockObservation{}, "", nil, err
	}
	selected, stock, reason, err := r.previewShell(call, snapshot, facts, protected, check)
	if err != nil {
		return nil, policy.StockObservation{}, "", nil, err
	}
	var shell *policy.StarterLayout
	if reason == "" {
		shell = &policy.StarterLayout{Room: previewRectangle(selected)}
	}
	if policy.ChooseExcavation(facts.Center, shell, target) {
		return nil, policy.StockObservation{}, "", target, nil
	}
	return selected, stock, reason, nil, nil
}

// previewRectangle is the bounding box of the previews' footprints: for a
// starter shell, exactly the room.
func previewRectangle(previews []policy.Preview) policy.Rectangle {
	var min, max domain.Cell
	first := true
	for _, p := range previews {
		footprint, _ := p.Footprint.Value()
		for _, cell := range footprint {
			if first {
				min, max, first = cell, cell, false
				continue
			}
			min.X, min.Z = minInt32(min.X, cell.X), minInt32(min.Z, cell.Z)
			max.X, max.Z = maxInt32(max.X, cell.X), maxInt32(max.Z, cell.Z)
		}
	}
	if first {
		return policy.Rectangle{}
	}
	return policy.Rectangle{X: min.X, Z: min.Z, Width: max.X - min.X + 1, Height: max.Z - min.Z + 1}
}

func minInt32(a, b int32) int32 {
	if a < b {
		return a
	}
	return b
}

func maxInt32(a, b int32) int32 {
	if a > b {
		return a
	}
	return b
}

// excavationStep carries one review's facts into stepExcavation.
type excavationStep struct {
	state  ControlState
	review store.RoutineReview
	goal   store.GoalState
	facts  observation.ColonyProjection
	read   observation.ColonyReading
	target policy.ExcavationTarget
}

// excavationStates projects a site read onto the review's cell states:
// open walkable ground is cleared; a visible cell that is neither cleared
// nor natively eligible (a structure the fog hid, deep water, protected
// rock) is blocked and kept.
func excavationStates(site bridge.ExcavationSite) []policy.ExcavationCellState {
	states := make([]policy.ExcavationCellState, 0, len(site.Cells))
	for _, cell := range site.Cells {
		cleared := !cell.Fogged && cell.Definition == "" && cell.Walkable
		states = append(states, policy.ExcavationCellState{Cell: cell.Cell, Fogged: cell.Fogged, Cleared: cleared, Blocked: !cell.Fogged && !cleared && !cell.Eligible, Eligible: cell.Eligible, Rock: cell.Definition})
	}
	return states
}

// stepExcavation decides what the bound project owes next from a fresh site
// read: the next frontier stage while diggable rock remains, the door once
// every cell is cleared or kept, nothing once the door exists. Three changes
// end the project instead (BuildingExcavationBlocked, after which the caller
// re-plans from the geometry the pawns actually opened -- another dig, or the
// open-site shell): a roof no longer held once the remaining rock is gone
// (the room was breached from outside; nothing sound can be finished), a
// way in that closed (the access cell walled off, a corridor cell that
// unfogged into something that cannot be mined), and a stage whose own
// removal native reports unsupported. A pending collapse and an unknown
// verdict hold the project for the next observation instead. Stage
// sequencing rides on GoalWorkOpen in step; this never runs while a stage
// plan is open.
func (r *RoutineBuildingPlanner) stepExcavation(call, epoch context.Context, s excavationStep) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	journal := p.journal
	if _, err := journal.LoadGoalMethod(call, s.goal.Goal.ID, s.goal.Goal.Epoch, excavationDoorMethod); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, err
	}
	stage := 0
	for ; stage <= excavationStageBound; stage++ {
		if _, err := journal.LoadGoalMethod(call, s.goal.Goal.ID, s.goal.Goal.Epoch, excavationStageMethod(stage)); errors.Is(err, store.ErrNotFound) {
			break
		} else if err != nil {
			return RoutineBuildingResult{}, err
		}
	}
	if stage > excavationStageBound {
		return RoutineBuildingResult{Reason: BuildingMethodExhausted}, nil
	}
	if !routineDefinitionsAvailable(s.facts, []string{"Door"}, true) {
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	}
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != s.state {
			return ErrControl
		}
		return nil
	}
	snapshot := s.state.Snapshot
	snapshot.Revision = 1
	site, err := r.readExcavationSite(call, snapshot, s.facts.Identity.Tick, s.target.Cells(), s.target.Access, check)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	definitions := map[domain.Cell]string{}
	for _, cell := range site.Cells {
		definitions[cell.Cell] = cell.Definition
	}
	review := policy.ReviewExcavation(s.target, excavationStates(site), excavationStageLimit)
	clockSchedulerLog("excavation stage %d for %s: next=%v kept=%v remaining=%d unknown=%v complete=%v corridor=%v support=%d (%s) collapse=%v worker=%v access=%v", stage, s.target.Key(), review.Stage, review.Kept, review.Remaining, review.Unknown, review.Complete, review.Corridor, site.Support, site.SupportBlocker, site.CollapsePending, site.WorkerAvailable, site.AccessReachable)
	if site.CollapsePending {
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	}
	if !review.Corridor || !site.AccessReachable {
		return RoutineBuildingResult{Reason: BuildingExcavationBlocked}, nil
	}
	if review.Complete {
		snapshot.Plan = excavationPlanID(s.goal, s.target, "door")
		return r.admitExcavationDoor(call, epoch, s, snapshot, check)
	}
	if site.Support == policy.ExcavationSupportUnsupported {
		return RoutineBuildingResult{Reason: BuildingExcavationBlocked}, nil
	}
	next := review.Stage
	if len(next) == 0 {
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	}
	if site.Support != policy.ExcavationSupportSupported {
		// The whole remaining target may run past visible geometry; the
		// stage itself must be supported outright.
		stageSite, err := r.readExcavationSite(call, snapshot, s.facts.Identity.Tick, next, s.target.Access, check)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		clockSchedulerLog("excavation stage %d support=%d (%s)", stage, stageSite.Support, stageSite.SupportBlocker)
		switch stageSite.Support {
		case policy.ExcavationSupportSupported:
		case policy.ExcavationSupportUnsupported:
			if stageSite.CollapsePending {
				return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
			}
			return RoutineBuildingResult{Reason: BuildingExcavationBlocked}, nil
		default:
			return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
		}
	}
	if !site.WorkerAvailable {
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	}
	snapshot.Plan = excavationPlanID(s.goal, s.target, fmt.Sprint(stage))
	actions := make([]domain.Action, 0, len(next))
	for i, cell := range next {
		excavation, err := domain.NewExcavation(cell, definitions[cell])
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		action, err := domain.NewExcavationAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), excavation)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, actions)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	return r.admitExcavation(call, epoch, s, snapshot, excavationStageMethod(stage), plan, nil, policy.StockObservation{Snapshot: snapshot, Tick: s.facts.Identity.Tick}, check)
}

// cancelStalledExcavation cancels the stage actions of the goal's open
// excavation plans that have sat held for excavationStallTicks or longer
// (a native refusal at the cell, lost support, no way to the access cell),
// so the stage closes and the next review can re-site or abandon the
// project instead of re-inspecting the same refusal forever. Cancelling
// the plan's pending work is the only durable change; cells the pawns
// already opened stay cleared and are never designated again.
func cancelStalledExcavation(ctx context.Context, journal *store.Store, goal store.GoalState, now domain.Tick) error {
	for _, method := range goal.Methods {
		if !IsExcavationPlan(method.Plan) {
			continue
		}
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		if !domain.GoalWorkOpen(plan.Progress) {
			continue
		}
		for _, progress := range plan.Progress {
			v := progress.View()
			if progress.Action().Kind() != domain.ExcavationAction || v.Stage != domain.Pending && v.Stage != domain.Prepared {
				continue
			}
			hold, ok := v.FreshHold()
			if !ok || int64(now-hold.Since) < excavationStallTicks {
				continue
			}
			stalled := false
			for _, reason := range hold.Reasons() {
				stalled = stalled || reason == domain.HeldExcavationUnsupported || reason == domain.HeldExcavationGeometryChanged || reason == domain.HeldNotReady
			}
			if !stalled {
				continue
			}
			if _, err = journal.Cancel(ctx, method.Plan, v.Action); err != nil {
				return err
			}
		}
	}
	return nil
}

// admitExcavationDoor closes the finished room with one wooden door at the
// corridor's end so the interior becomes a proper indoor room.
func (r *RoutineBuildingPlanner) admitExcavationDoor(call, epoch context.Context, s excavationStep, snapshot domain.GenerationSnapshot, check func() error) (RoutineBuildingResult, error) {
	building, err := domain.NewBuilding("Door", s.target.Door, domain.North, "WoodLog")
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	action, err := domain.NewBuildingAction(domain.ActionID(snapshot.Plan+"-0"), building)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if err := check(); err != nil {
		return RoutineBuildingResult{}, err
	}
	preview, _, err := r.native.PreviewBuilding(call, action, snapshot)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if err := check(); err != nil {
		return RoutineBuildingResult{}, err
	}
	v := preview.Preview
	if v.Action != action || !v.Snapshot.Matches(snapshot) || !v.Tick.FreshFor(s.facts.Identity.Tick) || !preview.Stock.Snapshot.Matches(snapshot) || !preview.Stock.Tick.FreshFor(s.facts.Identity.Tick) {
		return RoutineBuildingResult{}, ErrControl
	}
	stuff, known := v.MadeFromStuff.Value()
	if !known || !stuff {
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	}
	footprint, known := v.Footprint.Value()
	can, canKnown := v.CanPlace.Value()
	safe, safeKnown := v.SafeToPlace.Value()
	if !known || len(footprint) != 1 || footprint[0] != s.target.Door || !canKnown || !can || !safeKnown || !safe {
		return RoutineBuildingResult{Reason: BuildingMethodNoSpace}, nil
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: s.facts.Identity.Tick}
	if err := mergeRoutineStock(&stock, preview.Stock, true); err != nil {
		return RoutineBuildingResult{}, err
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	return r.admitExcavation(call, epoch, s, snapshot, excavationDoorMethod, plan, []policy.Preview{v}, stock, check)
}

// admitExcavation repeats step's boundary, staleness and review checks before
// handing the bundle to the shared admission transaction.
func (r *RoutineBuildingPlanner) admitExcavation(call, epoch context.Context, s excavationStep, snapshot domain.GenerationSnapshot, method domain.MethodID, plan domain.PlanSpec, previews []policy.Preview, stock policy.StockObservation, check func() error) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	last, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	actual, err := observation.DecodeIdentity(last)
	if err != nil || !routineBuildingBoundary(actual, s.state.Snapshot, s.facts.Identity.Tick) {
		return RoutineBuildingResult{}, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(s.read.StartedAt) || now.Sub(s.read.StartedAt) > r.reviewer.maxAge {
		return RoutineBuildingResult{}, observation.ErrStale
	}
	if err = check(); err != nil {
		return RoutineBuildingResult{}, err
	}
	latest, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if latest.Revision != s.review.Revision || !latest.Enabled {
		return RoutineBuildingResult{}, ErrControl
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: s.goal.Goal.ID, Revision: s.goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: s.facts.Identity.Tick, Bounds: domain.Known(s.facts.Bounds), Stock: stock, Rules: r.reviewer.rules, Previews: previews, Purpose: policy.Routine})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	reason := BuildingMethodRefused
	if decision.Admitted {
		reason = BuildingMethodAdmitted
	}
	return RoutineBuildingResult{Reason: reason, Decision: decision}, nil
}
