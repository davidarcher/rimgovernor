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
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// RoutineTidySource previews the re-sited zone and refreshes one zone's
// presence, CAS token and planted count for the old zone's deletion.
type RoutineTidySource interface {
	FieldNative
	ReadZoneDeleteTarget(context.Context, *c.Identity, string) (bridge.ZoneDeleteTarget, bridge.Result, error)
	PreviewZoneDelete(context.Context, *c.Identity, domain.ZoneDelete) (*op.PreviewReply, bridge.Result, error)
}

// RoutineTidyPlanner executes the TidyLayout review's proposal (#611) one
// re-site at a time. A zone re-site runs in two methods under the goal:
// the new zone is admitted first (tidy-<item>-create) and the tidy journaled
// as moving; once the new field reports planted cells (a stockpile as soon
// as it stands, its contents move by ordinary hauling) the old zone is
// deleted through a one-shot zone_delete (tidy-<item>-delete) and the
// tidy journaled done. A replaced shell is one deconstruction method over
// its claimed ring. A refused or retired method journals the tidy
// abandoned so the item is never proposed again.
type RoutineTidyPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineTidySource
}
type RoutineTidyResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

// tidyDeleteNotReady marks a re-site whose new zone is not yet planted.
const tidyDeleteNotReady RoutineBuildingReason = "new_zone_not_planted"

func NewRoutineTidyPlanner(reviewer *RoutineReviewer, native RoutineTidySource) (*RoutineTidyPlanner, error) {
	if reviewer == nil || native == nil || reviewer.native == nil {
		return nil, ErrControl
	}
	return &RoutineTidyPlanner{reviewer, native}, nil
}
func (r *RoutineTidyPlanner) Step(ctx context.Context) (RoutineTidyResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func tidyMethodID(item, phase string) domain.MethodID {
	sum := sha256.Sum256([]byte(item))
	return domain.MethodID(fmt.Sprintf("tidy-%s-%x", phase, sum[:12]))
}
func tidyPlanID(goal store.GoalState, method domain.MethodID) domain.PlanID {
	// The plan id leaves the goal's epoch out on purpose: a re-site outlives
	// an epoch turnover, and finish loads the create plan by this id rather
	// than through the goal's current methods (#611: an epoch turning over
	// under a moving tidy abandoned every re-site the tick it started).
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s", goal.Goal.ID, method)))
	return domain.PlanID(fmt.Sprintf("routine-tidy-%x", digest[:16]))
}

func (r *RoutineTidyPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineTidyResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineTidyResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineTidyResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot || review.Layout == nil {
		return RoutineTidyResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.TidyLayout {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive {
		return RoutineTidyResult{Reason: BuildingMethodNoDeficit}, nil
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineTidyResult{}, ErrControl
	}
	tidies, err := p.journal.LayoutTidies(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	for _, t := range tidies {
		if t.Status == store.LayoutTidyMoving {
			return r.finish(call, epoch, state, goal, expected.Tick, t)
		}
	}
	if goal.Goal.Need != domain.NeedDeficit {
		return RoutineTidyResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineTidyResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineTidyResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	proposal := review.Layout.Proposal
	if proposal == nil {
		return RoutineTidyResult{Reason: BuildingMethodUnknown}, nil
	}
	for _, t := range tidies {
		if t.Item == proposal.Item.ID {
			return RoutineTidyResult{Reason: BuildingMethodUsed}, nil
		}
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if proposal.Item.Kind == policy.TidyShell {
		return r.deconstruct(call, epoch, state, goal, read, *proposal)
	}
	return r.create(call, epoch, state, goal, read, *proposal)
}

// commit journals one goal method after the freshness checks every planner
// makes between its native reads and its write.
func (r *RoutineTidyPlanner) commit(call, epoch context.Context, state ControlState, goal store.GoalState, started observation.RoutineReading, method domain.MethodID, plan domain.PlanSpec) error {
	p := r.reviewer.player
	if err := p.current(call, epoch); err != nil {
		return err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(started.StartedAt) || now.Sub(started.StartedAt) > r.reviewer.maxAge {
		return ErrControl
	}
	_, err := p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan)
	return err
}

// create admits the proposal's new zone and journals the tidy moving.
func (r *RoutineTidyPlanner) create(call, epoch context.Context, state ControlState, goal store.GoalState, read observation.RoutineReading, proposal policy.TidyProposal) (RoutineTidyResult, error) {
	p := r.reviewer.player
	projection := read.Projection
	item := proposal.Item
	method := tidyMethodID(item.ID, "create")
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineTidyResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineTidyResult{}, err
	}
	token, known := projection.ZoneMapToken.Value()
	if !known {
		return RoutineTidyResult{Reason: BuildingMethodUnknown}, nil
	}
	id := tidyPlanID(goal, method)
	// The plan id outlives the goal's epoch: an existing plan is this
	// item's own earlier create, not a fresh one to admit.
	if _, err := p.journal.LoadPlan(call, id); err == nil {
		return RoutineTidyResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineTidyResult{}, err
	}
	snapshot := state.Snapshot
	snapshot.Plan, snapshot.Revision = id, 1
	kind := domain.StockpileZone
	if item.Kind == policy.TidyField {
		kind = domain.GrowingZone
	}
	var cells []domain.Cell
	for x := proposal.Target.X; x < proposal.Target.X+proposal.Target.Width; x++ {
		for z := proposal.Target.Z; z < proposal.Target.Z+proposal.Target.Height; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	value, err := domain.NewZoneCreate(kind, item.Crop, cells)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	reply, refused, err := previewZone(call, r.native, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
	if err != nil {
		return RoutineTidyResult{}, err
	}
	tick := projection.Identity.Tick
	if refused != "" {
		clockSchedulerLog("Tidy: %s %s new site %+v refused: %s", item.Kind, item.ID, proposal.Target, refused)
		if strings.Contains(refused, "zone census changed") {
			// Another planner in this wave (the fields planner, typically)
			// created a zone after the shared census was read: the site is
			// not refused, the token is stale. Retry on a fresh census.
			return RoutineTidyResult{Reason: BuildingMethodRefused}, nil
		}
		if err = r.record(call, state, tick, proposal, store.LayoutTidyAbandoned, ""); err != nil {
			return RoutineTidyResult{}, err
		}
		return RoutineTidyResult{Reason: BuildingMethodRefused}, nil
	}
	v := reply.GetEvaluated()
	if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) != tick {
		return RoutineTidyResult{}, ErrControl
	}
	preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineTidyResult{}, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineTidyResult{}, ErrControl
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: tick}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Rules: r.reviewer.rules, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if !decision.Admitted {
		clockSchedulerLog("Tidy: %s %s not admitted: %+v", item.Kind, item.ID, decision.Refused)
		if err = r.record(call, state, tick, proposal, store.LayoutTidyAbandoned, ""); err != nil {
			return RoutineTidyResult{}, err
		}
		return RoutineTidyResult{Reason: BuildingMethodRefused}, nil
	}
	clockEvent(call, "layout", "tidy", fmt.Sprintf("tidy re-site started: %s", proposal.Explanation), "item", item.ID, "kind", string(item.Kind), "plan", string(id))
	if err = r.record(call, state, tick, proposal, store.LayoutTidyMoving, ""); err != nil {
		return RoutineTidyResult{}, err
	}
	return RoutineTidyResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

func (r *RoutineTidyPlanner) record(call context.Context, state ControlState, tick domain.Tick, proposal policy.TidyProposal, status store.LayoutTidyStatus, newZone string) error {
	return r.reviewer.player.journal.RecordLayoutTidy(call, state.Snapshot, tick, store.LayoutTidy{Item: proposal.Item.ID, Kind: proposal.Item.Kind, Status: status, From: proposal.Item.Footprint, To: proposal.Target, Crop: proposal.Item.Crop, NewZone: newZone, Explanation: proposal.Explanation, Tick: tick})
}

// finish drives a moving tidy: the new zone's create method must have
// completed (else the tidy is abandoned once its plan is closed), the new
// field must be planted, then the old zone is deleted by CAS and the tidy
// journaled done once it is gone.
func (r *RoutineTidyPlanner) finish(call, epoch context.Context, state ControlState, goal store.GoalState, tick domain.Tick, t store.LayoutTidy) (RoutineTidyResult, error) {
	p := r.reviewer.player
	proposal := policy.TidyProposal{Item: policy.TidyItem{Kind: t.Kind, ID: t.Item, Footprint: t.From, Crop: t.Crop, Managed: true}, Target: t.To, Explanation: t.Explanation}
	createMethod, deleteMethod := tidyMethodID(t.Item, "create"), tidyMethodID(t.Item, "delete")
	load := func(method domain.MethodID) (*store.PlanState, error) {
		plan, err := p.journal.LoadPlan(call, tidyPlanID(goal, method))
		if errors.Is(err, store.ErrNotFound) {
			return nil, nil
		}
		if err != nil {
			return nil, err
		}
		return &plan, nil
	}
	created, err := load(createMethod)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	deleting, err := load(deleteMethod)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	newZone := t.NewZone
	if newZone == "" {
		if created == nil {
			// The goal's epoch turned over under the moving tidy: its create
			// method is gone, so the re-site cannot be finished.
			return r.abandon(call, state, tick, proposal)
		}
		if domain.GoalWorkOpen(created.Progress) {
			return RoutineTidyResult{Reason: BuildingMethodExistingWork}, nil
		}
		for _, progress := range created.Progress {
			v := progress.View()
			if id, known := v.Zone.Value(); known && v.Stage == domain.Completed {
				newZone = id
			}
		}
		if newZone == "" {
			return r.abandon(call, state, tick, proposal)
		}
	}
	if deleting != nil {
		if domain.GoalWorkOpen(deleting.Progress) {
			return RoutineTidyResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity := boundary.Identity(state.Snapshot)
	old, _, err := r.native.ReadZoneDeleteTarget(call, identity, t.Item)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if _, err = boundary.Context(old.Context, state.Snapshot); err != nil || old.Context.GetTick() < int64(tick) {
		return RoutineTidyResult{}, ErrControl
	}
	if !old.Present {
		clockEvent(call, "layout", "tidy", fmt.Sprintf("tidy re-site done: %s %s -> %s", t.Kind, t.Item, newZone), "item", t.Item, "kind", string(t.Kind), "new_zone", newZone)
		if err = r.record(call, state, tick, proposal, store.LayoutTidyDone, newZone); err != nil {
			return RoutineTidyResult{}, err
		}
		return RoutineTidyResult{Reason: BuildingMethodAdmitted}, nil
	}
	if deleting != nil {
		// The delete ran and the zone still stands: the CAS token moved or
		// the game refused; a fresh method on the next epoch retries.
		return RoutineTidyResult{Reason: BuildingMethodUsed}, nil
	}
	fresh, _, err := r.native.ReadZoneDeleteTarget(call, identity, newZone)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if _, err = boundary.Context(fresh.Context, state.Snapshot); err != nil {
		return RoutineTidyResult{}, ErrControl
	}
	if !fresh.Present {
		return r.abandon(call, state, tick, proposal)
	}
	if t.Kind == policy.TidyField && fresh.Planted == 0 {
		return RoutineTidyResult{Reason: tidyDeleteNotReady}, nil
	}
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, deleteMethod); err == nil {
		return RoutineTidyResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineTidyResult{}, err
	}
	del, err := domain.NewZoneDelete(t.Item, old.Token)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	preview, _, err := r.native.PreviewZoneDelete(call, identity, del)
	if err != nil {
		var refusal *bridge.NativeFailure
		if errors.As(err, &refusal) {
			clockSchedulerLog("Tidy: delete %s refused at preview: %s", t.Item, refusal.Value.GetDetail())
			return RoutineTidyResult{Reason: BuildingMethodRefused}, nil
		}
		return RoutineTidyResult{}, err
	}
	if v := preview.GetEvaluated(); v == nil || !v.GetAccepted() {
		return RoutineTidyResult{Reason: BuildingMethodRefused}, nil
	}
	id := tidyPlanID(goal, deleteMethod)
	action, err := domain.NewZoneDeleteAction(domain.ActionID(fmt.Sprintf("%s-0", id)), del)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineTidyResult{}, err
	}
	if p.session.State() != state {
		return RoutineTidyResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, deleteMethod, plan); err != nil {
		return RoutineTidyResult{}, err
	}
	if t.NewZone == "" {
		if err = r.record(call, state, tick, proposal, store.LayoutTidyMoving, newZone); err != nil {
			return RoutineTidyResult{}, err
		}
	}
	clockEvent(call, "layout", "tidy", fmt.Sprintf("tidy old zone deletion admitted: %s %s (new zone %s)", t.Kind, t.Item, newZone), "item", t.Item, "plan", string(id))
	return RoutineTidyResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

func (r *RoutineTidyPlanner) abandon(call context.Context, state ControlState, tick domain.Tick, proposal policy.TidyProposal) (RoutineTidyResult, error) {
	clockEvent(call, "layout", "tidy", fmt.Sprintf("tidy re-site abandoned: %s %s", proposal.Item.Kind, proposal.Item.ID), "item", proposal.Item.ID, "kind", string(proposal.Item.Kind))
	if err := r.record(call, state, tick, proposal, store.LayoutTidyAbandoned, ""); err != nil {
		return RoutineTidyResult{}, err
	}
	return RoutineTidyResult{Reason: BuildingMethodRefused}, nil
}

// deconstruct commits one method deconstructing every standing claimed
// building on the replaced shell's ring and journals the tidy done: the
// shell is gone as far as the tidy is concerned once the work is admitted,
// and a room the game merges away is never listed again.
func (r *RoutineTidyPlanner) deconstruct(call, epoch context.Context, state ControlState, goal store.GoalState, read observation.RoutineReading, proposal policy.TidyProposal) (RoutineTidyResult, error) {
	p := r.reviewer.player
	projection := read.Projection
	item := proposal.Item
	method := tidyMethodID(item.ID, "shell")
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineTidyResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineTidyResult{}, err
	}
	census, known := projection.Facts.CurrentConstruction.Value()
	claims, ck := projection.Facts.ConstructionClaims.Value()
	if !known || !census.Colony || !ck {
		return RoutineTidyResult{Reason: BuildingMethodUnknown}, nil
	}
	claimed := map[domain.Cell]bool{}
	for _, claim := range claims {
		for _, cell := range claim.Cells {
			claimed[cell] = true
		}
	}
	ring := item.Footprint
	onRing := func(c domain.Cell) bool {
		return c.X >= ring.X && c.X < ring.X+ring.Width && c.Z >= ring.Z && c.Z < ring.Z+ring.Height && (c.X == ring.X || c.Z == ring.Z || c.X == ring.X+ring.Width-1 || c.Z == ring.Z+ring.Height-1)
	}
	id := tidyPlanID(goal, method)
	var actions []domain.Action
	for _, b := range census.Buildings {
		if len(b.Cells) == 0 || !onRing(b.Cells[0]) || !claimed[b.Cells[0]] {
			continue
		}
		value, err := domain.NewDeconstruction(b.ID, b.Building.Definition(), b.Cells[0])
		if err != nil {
			return RoutineTidyResult{}, err
		}
		action, err := domain.NewDeconstructionAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), value)
		if err != nil {
			return RoutineTidyResult{}, err
		}
		actions = append(actions, action)
	}
	tick := projection.Identity.Tick
	if len(actions) == 0 {
		return r.abandon(call, state, tick, proposal)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	if err = r.commit(call, epoch, state, goal, read, method, plan); err != nil {
		return RoutineTidyResult{}, err
	}
	clockEvent(call, "layout", "tidy", fmt.Sprintf("tidy shell deconstruction admitted: %s", proposal.Explanation), "item", item.ID, "plan", string(id), "walls", len(actions))
	if err = r.record(call, state, tick, proposal, store.LayoutTidyDone, ""); err != nil {
		return RoutineTidyResult{}, err
	}
	return RoutineTidyResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
