package buildingruntime

import (
	"context"
	"fmt"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoundsExcavationSource verifies a proposed excavation target against the
// live map: per-cell rock/eligibility, site-level roof support after the
// counterfactual removal, and a miner who can reach the access cell. General
// colony facts (visible cells, roof defs, definitions) come from the shared
// RoundsBuildingSource read.
type RoundsExcavationSource interface {
	ReadExcavationSite(context.Context, *c.Identity, []domain.Cell, domain.Cell) (bridge.ExcavationSite, bridge.Result, error)
}

const (
	excavationStagePrefix = "excavation-stage-"
	excavationStageLimit  = 8
	excavationStageBound  = 64
	// excavationStallTicks bounds how long a stage action may stay held
	// (unsupported, changed geometry, no way in) before the planner cancels
	// it so the project can be reviewed against the geometry that changed
	// under it; an in-flight stage otherwise reads as open work forever.
	excavationStallTicks = domain.TicksPerHour
	excavationCandidates = 4
)

// excavationStageMethod names stage n of the project on target. The target
// key rides on the method id (#987), the goal_methods key, so an
// in-progress project is recovered from the journal alone; plan ids are
// bare UUIDs.
func excavationStageMethod(stage int, target policy.ExcavationTarget) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("%s%d@%s", excavationStagePrefix, stage, target.Key()))
}

// ExcavationMethod parses a tunnel stage method id: its stage and the
// target it carries. ok is false for any other method.
func ExcavationMethod(method domain.MethodID) (stage int, target policy.ExcavationTarget, ok bool) {
	head, key, found := strings.Cut(string(method), "@")
	if !found {
		return 0, policy.ExcavationTarget{}, false
	}
	n, cut := strings.CutPrefix(head, excavationStagePrefix)
	v, err := strconv.Atoi(n)
	if !cut || err != nil || v < 0 || strconv.Itoa(v) != n {
		return 0, policy.ExcavationTarget{}, false
	}
	target, err = policy.ParseExcavationKey(key)
	if err != nil {
		return 0, policy.ExcavationTarget{}, false
	}
	return v, target, true
}

// IsExcavationMethod reports whether method is a tunnel stage.
func IsExcavationMethod(method domain.MethodID) bool {
	_, _, ok := ExcavationMethod(method)
	return ok
}

// excavationProgress reads a Episode's tunnel stages: the target the
// latest (highest) stage carries and the next stage number.
func excavationProgress(methods []domain.Method) (latest *policy.ExcavationTarget, next int) {
	for _, m := range methods {
		if stage, target, ok := ExcavationMethod(m.Method); ok && stage >= next {
			next, latest = stage+1, &target
		}
	}
	return latest, next
}

// excavationProject reports the target of the goal's current tunnel: the
// one the most recently admitted stage under this Episode carries.
func (r *RoundsBuildingPlanner) excavationProject(call context.Context, goal store.WorkOwner) (*policy.ExcavationTarget, error) {
	methods, err := r.reviewer.player.journal.LoadOwnerMethods(call, goal)
	if err != nil {
		return nil, err
	}
	latest, _ := excavationProgress(methods)
	return latest, nil
}

// readExcavationSite reads the target under the current observation and
// insists the reply belongs to the same generation and tick as the colony
// facts the stage was planned from.
func (r *RoundsBuildingPlanner) readExcavationSite(call context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, purpose string, target policy.ExcavationTarget, cells []domain.Cell, access domain.Cell, check func() error) (bridge.ExcavationSite, error) {
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
	if err != nil || current.Native != snapshot.Native || domain.Tick(site.Context.GetTick()) < tick {
		return bridge.ExcavationSite{}, fmt.Errorf("%w: readExcavationSite: err != nil || current.Native != snapshot.Native || domain.Tick(site.Context.GetTick()) < tick", ErrControl)
	}
	snap.NoteSite(call, purpose, target, cells, site)
	return site, nil
}

// verifyExcavation reads a fresh tunnel candidate under the current
// observation: acceptable when every visible cell is eligible rock or
// already cleared, the counterfactual removal is not known to be
// unsupported, and a miner can reach the access cell now.
func (r *RoundsBuildingPlanner) verifyExcavation(call context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, target policy.ExcavationTarget, check func() error) (bool, error) {
	site, err := r.readExcavationSite(call, snapshot, tick, "verify", target, target.Cells(), target.Access, check)
	if err != nil {
		return false, err
	}
	return excavationVerified(target, site), nil
}

// excavationVerified is verifyExcavation's judgement of one site read.
func excavationVerified(target policy.ExcavationTarget, site bridge.ExcavationSite) bool {
	if site.Support == policy.ExcavationSupportUnsupported && !site.CollapsePending || !site.AccessReachable || !site.WorkerAvailable {
		clockSchedulerLog("excavation target %s rejected: support=%d (%s) worker=%v access=%v", target.Key(), site.Support, site.SupportBlocker, site.WorkerAvailable, site.AccessReachable)
		return false
	}
	review := policy.ReviewExcavation(target, excavationStates(site), excavationStageLimit)
	if len(review.Kept) > 0 || !review.Corridor {
		clockSchedulerLog("excavation target %s rejected: kept=%v corridor=%v", target.Key(), review.Kept, review.Corridor)
		return false
	}
	return true
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
	review store.Rounds
	goal   store.WorkOwner
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

// stepExcavation decides what the bound tunnel owes next from a fresh site
// read: the next frontier stage while diggable rock remains, nothing once
// every cell is cleared or kept. Three changes end the tunnel instead
// (an excavationBlocked verdict, after which the caller re-sites it): a roof no
// longer held once the remaining rock is gone, a
// way in that closed (the access cell walled off, a corridor cell that
// unfogged into something that cannot be mined), and a stage whose own
// removal native reports unsupported. A pending collapse and an unknown
// verdict hold the project for the next observation instead. Stage
// sequencing rides on GoalWorkOpen in step; this never runs while a stage
// plan is open.
func (r *RoundsBuildingPlanner) stepExcavation(call, epoch context.Context, s excavationStep) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	journal := p.journal
	methods, err := journal.LoadOwnerMethods(call, s.goal)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	_, stage := excavationProgress(methods)
	if stage > excavationStageBound {
		return RoundsBuildingResult{Verdict: refuse(RefusalRetriesSpent, "excavation_stage", "")}, nil
	}
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != s.state {
			return fmt.Errorf("%w: stepExcavation: p.session.State() != s.state", ErrControl)
		}
		return nil
	}
	snapshot := s.state.Snapshot
	snapshot.Revision = 1
	site, err := r.readExcavationSite(call, snapshot, s.facts.Identity.Tick, "stage", s.target, s.target.Cells(), s.target.Access, check)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	definitions := map[domain.Cell]string{}
	for _, cell := range site.Cells {
		definitions[cell.Cell] = cell.Definition
	}
	review, reason, door := excavationNext(s.target, site)
	clockSchedulerLog("excavation stage %d for %s: next=%v kept=%v remaining=%d unknown=%v complete=%v corridor=%v support=%d (%s) collapse=%v worker=%v access=%v", stage, s.target.Key(), review.Stage, review.Kept, review.Remaining, review.Unknown, review.Complete, review.Corridor, site.Support, site.SupportBlocker, site.CollapsePending, site.WorkerAvailable, site.AccessReachable)
	if door {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "excavation_door")}, nil
	}
	if !reason.IsZero() {
		return RoundsBuildingResult{Verdict: reason}, nil
	}
	next := review.Stage
	if site.Support != policy.ExcavationSupportSupported {
		// The whole remaining target may run past visible geometry; the
		// stage itself must be supported outright.
		stageSite, err := r.readExcavationSite(call, snapshot, s.facts.Identity.Tick, "stage-support", s.target, next, s.target.Access, check)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		clockSchedulerLog("excavation stage %d support=%d (%s)", stage, stageSite.Support, stageSite.SupportBlocker)
		switch stageSite.Support {
		case policy.ExcavationSupportSupported:
		case policy.ExcavationSupportUnsupported:
			if stageSite.CollapsePending {
				return RoundsBuildingResult{Verdict: collapsePending("excavation_site")}, nil
			}
			return RoundsBuildingResult{Verdict: excavationBlocked("roof_unsupported")}, nil
		default:
			return RoundsBuildingResult{Verdict: fieldUnavailable("excavation_support")}, nil
		}
	}
	if !site.WorkerAvailable {
		return RoundsBuildingResult{Verdict: noWorker("excavation")}, nil
	}
	snapshot.Plan = domain.MintPlanID()
	actions := make([]domain.Action, 0, len(next))
	for i, cell := range next {
		excavation, err := domain.NewExcavation(cell, definitions[cell])
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		action, err := domain.NewExcavationAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), excavation)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, actions)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	return r.admitExcavation(call, epoch, s, snapshot, excavationStageMethod(stage, s.target), plan, nil, policy.StockObservation{Snapshot: snapshot, Tick: s.facts.Identity.Tick}, check)
}

// excavationBlocked is the verdict of a tunnel that ends: the caller
// re-sites it (rounds_resource_tunnel.go).
func excavationBlocked(why string) Verdict { return siteBlocked("excavation_site", why) }

// excavationNext is stepExcavation's judgement of the project's site read:
// done once the tunnel is complete, a reason that holds or ends the
// project, or neither, when review.Stage is the next stage to designate
// (its own support still to be read unless the whole site is supported).
func excavationNext(target policy.ExcavationTarget, site bridge.ExcavationSite) (policy.ExcavationReview, Verdict, bool) {
	review := policy.ReviewExcavation(target, excavationStates(site), excavationStageLimit)
	switch {
	case site.CollapsePending:
		return review, collapsePending("excavation_site"), false
	case !review.Corridor || !site.AccessReachable:
		return review, excavationBlocked("way_in_closed"), false
	case review.Complete:
		return review, Verdict{}, true
	case site.Support == policy.ExcavationSupportUnsupported:
		return review, excavationBlocked("roof_unsupported"), false
	case len(review.Stage) == 0 && review.Unknown:
		return review, fieldUnavailable("excavation_cells"), false
	case len(review.Stage) == 0:
		return review, noSpace("excavation_next_stage"), false
	}
	return review, Verdict{}, false
}

// cancelStalledExcavation cancels the stage actions of the goal's open
// excavation plans that have sat held for excavationStallTicks or longer
// (a native refusal at the cell, lost support, no way to the access cell),
// so the stage closes and the next review can re-site or abandon the
// project instead of re-inspecting the same refusal forever. Cancelling
// the plan's pending work is the only durable change; cells the pawns
// already opened stay cleared and are never designated again.
func cancelStalledExcavation(ctx context.Context, journal *store.Store, goal store.WorkOwner, now domain.Tick) error {
	for _, method := range goal.OwnerMethods() {
		if !IsExcavationMethod(method.Method) {
			continue
		}
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		if !store.PlanOpen(plan) {
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

// admitExcavation repeats step's boundary, staleness and review checks before
// handing the bundle to the shared admission transaction.
func (r *RoundsBuildingPlanner) admitExcavation(call, epoch context.Context, s excavationStep, snapshot domain.GenerationSnapshot, method domain.MethodID, plan domain.PlanSpec, previews []policy.Preview, stock policy.StockObservation, check func() error) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	// The reviewer's identity: a MaintainResource tunnel (#1074) runs this
	// without a building source of its own.
	last, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	actual, err := observation.DecodeIdentity(last)
	if err != nil || !roundsBuildingBoundary(actual, s.state.Snapshot, s.facts.Identity.Tick) {
		return RoundsBuildingResult{}, fmt.Errorf("%w: admitExcavation: err != nil || !roundsBuildingBoundary(actual, s.state.Snapshot, s.facts.Identity.Tick)", ErrControl)
	}
	now := r.reviewer.clock.Now()
	if now.Before(s.read.StartedAt) || now.Sub(s.read.StartedAt) > r.reviewer.maxAge {
		return RoundsBuildingResult{}, observation.ErrStale
	}
	if err = check(); err != nil {
		return RoundsBuildingResult{}, err
	}
	latest, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if latest.Revision != s.review.Revision || !latest.Enabled {
		return RoundsBuildingResult{}, fmt.Errorf("%w: admitExcavation: latest.Revision != s.review.Revision || !latest.Enabled", ErrControl)
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Owner: s.goal, Method: method, Plan: plan, Current: snapshot, Tick: s.facts.Identity.Tick, Bounds: domain.Known(s.facts.Bounds), Stock: stock, Previews: previews, Purpose: policy.Rounds})
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	reason := admissionRefused(decision)
	if decision.Admitted {
		reason = BuildingReasonAdmitted
	}
	return RoundsBuildingResult{Verdict: reason, Decision: decision}, nil
}
