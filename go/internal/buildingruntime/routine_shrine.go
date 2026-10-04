package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoutineShrineSource is the shrine census plus the readiness reads (#457)
// the breach goal judges from, and the claim target read (#459) that
// gives each empty casket its CAS token.
type RoutineShrineSource interface {
	observation.ColonySource
	observation.ShrineSource
	shrineReadinessNative
	ReadClaimBuildingTarget(context.Context, *c.Identity, string) (bridge.ClaimBuildingTarget, bridge.Result, error)
}

// RoutineShrinePlanner composes the ClearAncientShrine goal's methods:
// the breach (#458), when readiness reads Ready for a sealed shrine
// touching Home it drafts the squad to standing cells behind the trap
// line and designates the chosen wall for an in-place deconstruction (the
// wall falling is the method's end: the plan has no open work, the worker
// releases the owned drafts and ActiveCombat answers the guards); and the
// claim (#459), once the shrine is open and guard-free every empty casket
// the player does not own is claimed in one method; and the opening
// (#460), once the review's opening gate holds (#875), the melee lock: one
// violence-capable melee colonist drafted at each filled casket and one
// OpenCasket order, held lock_understaffed while the squad cannot cover
// every casket. While the gate holds back, filled caskets stay sealed.
type RoutineShrinePlanner struct {
	reviewer *Rounder
	native   RoutineShrineSource
}
type RoutineShrineResult struct {
	Verdict
	Plan domain.PlanID
	// Hold is the readiness reason the planner held on (BuildingReasonHeld)
	// and Shrine the shrine it judged.
	Hold, Shrine string
	// Skipped is every candidate the step judged and passed over, with its
	// own reason, beside the one Shrine names (#680).
	Skipped         []policy.ShrineHold
	NativeWorkTicks uint32
}

func NewRoutineShrinePlanner(reviewer *Rounder, native RoutineShrineSource) (*RoutineShrinePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineShrinePlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineShrinePlanner{reviewer, native}, nil
}
func (r *RoutineShrinePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (result RoutineShrineResult, err error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineShrineResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineShrineResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineShrineResult{Verdict: BuildingReasonNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.ClearAncientShrine, state.Snapshot, review.Tick)
	defer recorded()
	// The step's own answer goes on the review it planned under (#680), so
	// the journal names the shrine it held on rather than leaving the
	// advisory ShrineHolds, in identity order, to read as the cause. A
	// review filed since is the newer answer; the record yields to it.
	// held collects the candidates the step passed over; a step that went
	// on to act on a later shrine names every one of them skipped.
	var held RoutineShrineResult
	defer func() {
		if err != nil || result.Verdict.IsZero() {
			return
		}
		if result.Shrine != held.Shrine || result.Hold != held.Hold {
			result.Skipped = append(slices.Clone(held.Skipped), result.Skipped...)
			if held.Hold != "" && held.Shrine != result.Shrine {
				result.Skipped = append(result.Skipped, policy.ShrineHold{Shrine: held.Shrine, Reason: held.Hold})
			}
		}
		step := store.RoutineShrineStep{Tick: review.Tick, Reason: result.Verdict.String(), Shrine: result.Shrine, Hold: result.Hold, Plan: result.Plan, Skipped: result.Skipped}
		if _, recordErr := p.journal.RecordShrineStep(call, review.Revision, step); recordErr != nil && !errors.Is(recordErr, store.ErrConflict) {
			err = recordErr
		}
	}()
	// A pause or authority change between the draft and the move releases
	// the draft; the moves riding on it and the open waiting on them can
	// then never dispatch, and while they stay open the goal never re-plans
	// and the clock holds on work that cannot run (#707). Settle every
	// shrine plan so orphaned, including those of a goal a reload
	// invalidated, before the active goal is read.
	if err = r.settleOrphanedPlans(call); err != nil {
		return RoutineShrineResult{}, err
	}
	goal, workable, err := p.journal.WorkableProject(call, review, policy.ClearAncientShrine)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if !workable {
		return RoutineShrineResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineShrineResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.ClearAncientShrine && (row.Selected || row.Committed)
	}
	if !selected {
		return RoutineShrineResult{Verdict: awaitingSlot(string(policy.ClearAncientShrine))}, nil
	}
	started := r.reviewer.clock.Now()
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineShrineResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	colony, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	read, err := observation.ObserveShrines(call, r.native, expected)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	shrines, known := read.Value()
	if !known {
		return RoutineShrineResult{Verdict: fieldUnavailable("shrines")}, nil
	}
	// The opening gate (#875) was judged at the review; a casket it decided
	// open there is owed an opening now. The lock is re-staffed below.
	opening := shrineOpeningFromHolds(review.ShrineHolds)
	targets := map[string]bool{}
	for _, id := range policy.ShrineClearanceTargets(shrines, opening) {
		targets[id] = true
	}
	var candidates []policy.AncientShrine
	for _, shrine := range shrines {
		if targets[shrine.ID] {
			candidates = append(candidates, shrine)
		}
	}
	if len(candidates) == 0 {
		return RoutineShrineResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	sort.Slice(candidates, func(i, j int) bool { return candidates[i].ID < candidates[j].ID })
	claims := policy.ShrineClaimTargets(candidates)
	for _, shrine := range candidates {
		if caskets := claims[shrine.ID]; len(caskets) > 0 {
			return r.claim(call, epoch, state, goal, shrine, caskets, started)
		}
	}
	held = RoutineShrineResult{Verdict: BuildingReasonHeld}
	opens := policy.ShrineOpenTargets(candidates, opening)
	if len(opens) > 0 {
		needed, err := plannedDrafts(call, p.journal)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		squad, err := shrineSquad(call, r.native, boundary.Identity(state.Snapshot), nil, needed)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		snap.NoteShrineSquad(call, squad)
		for _, shrine := range candidates {
			caskets := opens[shrine.ID]
			if len(caskets) == 0 {
				continue
			}
			lock := policy.ShrineMeleeLock(caskets, squad)
			if lock.Reason != "" {
				held = held.pass(shrine.ID, lock.Reason)
				continue
			}
			return r.open(call, epoch, state, goal, shrine, caskets, lock, started, arbiter)
		}
	}
	reports, err := shrineReadiness(call, r.native, boundary.Identity(state.Snapshot), candidates, nil, colony.Projection.Threat.RaidPoints, colony.Projection.Center, colony.Projection.Bounds)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	for i, report := range reports {
		shrine := candidates[i]
		reason := policy.ShrineHoldReason(shrine, report.Readiness)
		if reason != policy.ShrineReady {
			held = held.pass(shrine.ID, reason)
			continue
		}
		return r.breach(call, epoch, state, goal, shrine, report, colony.Projection, started, arbiter)
	}
	return held, nil
}

// pass records one candidate the step judged and moved past: the first
// becomes the step's hold, every later one a skipped candidate.
func (r RoutineShrineResult) pass(shrine, reason string) RoutineShrineResult {
	if r.Hold == "" {
		r.Hold, r.Shrine = reason, shrine
	} else {
		r.Skipped = append(r.Skipped, policy.ShrineHold{Shrine: shrine, Reason: reason})
	}
	return r
}

// claim commits one open, guard-free shrine's casket method: a
// ClaimBuilding for every empty casket the player does not own, each under
// the CAS token a fresh target read gives it. A casket the read already
// shows as the player's (a census a tick behind) is skipped; a claim
// refused natively surfaces as the action's own unsuccessful outcome.
func (r *RoutineShrinePlanner) claim(call, epoch context.Context, state ControlState, goal store.ProjectState, shrine policy.AncientShrine, caskets []policy.ShrineCasket, started time.Time) (RoutineShrineResult, error) {
	p := r.reviewer.player
	prefix := fmt.Sprintf("claim-%s-", shrine.ID)
	attempt := projectAttemptCount(goal, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineShrineResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", ""), Shrine: shrine.ID}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	identity := boundary.Identity(state.Snapshot)
	var actions []domain.Action
	for _, casket := range caskets {
		target, _, err := r.native.ReadClaimBuildingTarget(call, identity, casket.EntityID)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		if target.PlayerOwned {
			continue
		}
		value, err := domain.NewClaimBuilding(casket.EntityID)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		action, err := domain.NewClaimBuildingAction(domain.ActionID(fmt.Sprintf("%s-claim-%s", id, casket.EntityID)), value)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		actions = append(actions, action)
	}
	if len(actions) == 0 {
		return RoutineShrineResult{Verdict: BuildingReasonNoDeficit, Shrine: shrine.ID}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineShrineResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineShrineResult{}, fmt.Errorf("%w: claim: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitProjectMethod(call, goal.Project.ID, goal.Revision, method, "", plan); err != nil {
		return RoutineShrineResult{}, err
	}
	return RoutineShrineResult{Verdict: BuildingReasonAdmitted, Plan: id, Shrine: shrine.ID}, nil
}

// breach commits one sealed shrine's method: an owned draft and a move to a
// standing cell behind the trap line for each drafted defender, and the
// breach deconstruction of the chosen wall. One colonist is always left
// undrafted for the deconstruct job. The method is retried at most
// maxMedicalAttemptsPerPatient times per wall and Episode.
func (r *RoutineShrinePlanner) breach(call, epoch context.Context, state ControlState, goal store.ProjectState, shrine policy.AncientShrine, report ShrineReadinessReport, projection observation.ColonyProjection, started time.Time, arbiter *stepArbiter) (RoutineShrineResult, error) {
	p := r.reviewer.player
	wall := report.Readiness.Wall
	colonists, known := projection.Facts.Colonists.Value()
	if !known {
		return RoutineShrineResult{Verdict: fieldUnavailable("colonists")}, nil
	}
	drafted := policy.ShrineBreachDrafts(report.Readiness.Squad, int(colonists))
	if !arbiter.tryClaim(drafted) {
		return RoutineShrineResult{Verdict: waitFor(WaitMethodUsed, "breach_defenders")}, nil
	}
	positions := policy.ShrineBreachPositions(wall, drafted, report.Standing, report.Traps)
	prefix := fmt.Sprintf("breach-%s-%s-", shrine.ID, wall.EntityID)
	attempt := projectAttemptCount(goal, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineShrineResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", ""), Shrine: shrine.ID}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	var actions []domain.Action
	for _, defender := range drafted {
		draftID := domain.ActionID(fmt.Sprintf("%s-draft-%s", id, defender))
		draft, err := domain.NewOwnedDraft(defender)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		draftAction, err := domain.NewOwnedDraftAction(draftID, draft)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		actions = append(actions, draftAction)
		cell, ok := positions[defender]
		if !ok {
			continue
		}
		movement, err := domain.NewMovement(defender, cell, draftID)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		moveAction, err := domain.NewMovementAction(domain.ActionID(fmt.Sprintf("%s-move-%s", id, defender)), movement)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		actions = append(actions, moveAction)
	}
	definition := wall.DefName
	if definition == "" {
		definition = "Wall"
	}
	value, err := domain.NewDeconstruction(wall.EntityID, definition, wall.Cell)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	breachID := domain.ActionID(fmt.Sprintf("%s-breach", id))
	breachAction, err := domain.NewDeconstructionAction(breachID, value)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	actions = append(actions, breachAction)
	// The wall goes only once every drafted defender stands: the breach
	// waits on each draft (and each move when a cell was found).
	var dependencies []domain.ActionDependency
	for _, action := range actions[:len(actions)-1] {
		dependencies = append(dependencies, domain.ActionDependency{Action: breachID, Requires: action.ID()})
	}
	plan, err := domain.NewPlan(id, 1, actions, dependencies...)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineShrineResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineShrineResult{}, fmt.Errorf("%w: breach: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitProjectMethod(call, goal.Project.ID, goal.Revision, method, "", plan); err != nil {
		return RoutineShrineResult{}, err
	}
	return RoutineShrineResult{Verdict: BuildingReasonAdmitted, Plan: id, Shrine: shrine.ID}, nil
}

// open commits one open, guard-free shrine's melee lock (#460): an owned
// draft and a move to the casket's interaction cell for each locker, then
// one OpenCasket by the opener on the lowest casket, which ejects every
// casket of the group. The lockers stand where the ancients drop and their
// drafted auto-attack answers a waking hostile; the plan has no further
// work, so the worker keeps the drafts until the opening resolves and
// ActiveCombat and the custody planner take the occupants from there.
func (r *RoutineShrinePlanner) open(call, epoch context.Context, state ControlState, goal store.ProjectState, shrine policy.AncientShrine, caskets []policy.ShrineCasket, lock policy.ShrineLock, started time.Time, arbiter *stepArbiter) (RoutineShrineResult, error) {
	p := r.reviewer.player
	lockers := make([]domain.PawnID, 0, len(lock.Lockers))
	for _, casket := range caskets {
		lockers = append(lockers, lock.Lockers[casket.EntityID])
	}
	if !arbiter.tryClaim(lockers) {
		return RoutineShrineResult{Verdict: waitFor(WaitMethodUsed, "casket_lockers")}, nil
	}
	prefix := fmt.Sprintf("open-%s-", shrine.ID)
	attempt := projectAttemptCount(goal, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineShrineResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", ""), Shrine: shrine.ID}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	var actions []domain.Action
	var target policy.ShrineCasket
	for _, casket := range caskets {
		if casket.EntityID == lock.Casket {
			target = casket
		}
		locker := lock.Lockers[casket.EntityID]
		draftID := domain.ActionID(fmt.Sprintf("%s-draft-%s", id, locker))
		draft, err := domain.NewOwnedDraft(locker)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		draftAction, err := domain.NewOwnedDraftAction(draftID, draft)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		movement, err := domain.NewMovement(locker, casket.InteractionCell, draftID)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		moveAction, err := domain.NewMovementAction(domain.ActionID(fmt.Sprintf("%s-move-%s", id, locker)), movement)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		actions = append(actions, draftAction, moveAction)
	}
	if target.EntityID == "" {
		return RoutineShrineResult{}, fmt.Errorf("%w: open: target.EntityID == \"\"", ErrControl)
	}
	value, err := domain.NewOpenCasket(lock.Opener, target.EntityID, target.Cell)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	openID := domain.ActionID(fmt.Sprintf("%s-open", id))
	openAction, err := domain.NewOpenCasketAction(openID, value)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	actions = append(actions, openAction)
	// The casket opens only once every locker stands at a casket.
	var dependencies []domain.ActionDependency
	for _, action := range actions[:len(actions)-1] {
		dependencies = append(dependencies, domain.ActionDependency{Action: openID, Requires: action.ID()})
	}
	plan, err := domain.NewPlan(id, 1, actions, dependencies...)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineShrineResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineShrineResult{}, fmt.Errorf("%w: open: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitProjectMethod(call, goal.Project.ID, goal.Revision, method, "", plan); err != nil {
		return RoutineShrineResult{}, err
	}
	return RoutineShrineResult{Verdict: BuildingReasonAdmitted, Plan: id, Shrine: shrine.ID}, nil
}

// shrineMethod reports a method the shrine planner admits (claim, breach or
// open), read from the plan's stored method (#987).
func shrineMethod(method domain.MethodID) bool {
	for _, prefix := range []string{"claim-", "breach-", "open-"} {
		if strings.HasPrefix(string(method), prefix) {
			return true
		}
	}
	return false
}

// settleOrphanedPlans cancels the unissued work of every shrine plan whose
// draft was lost (orphanedDraftDependents): the open and retreat wait on
// the move, so the whole plan settles, not only the orders on the draft.
func (r *RoutineShrinePlanner) settleOrphanedPlans(call context.Context) error {
	p := r.reviewer.player
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return err
	}
	for _, plan := range plans {
		if !shrineMethod(plan.Method) || len(orphanedDraftDependents(plan.Spec, plan.Progress)) == 0 {
			continue
		}
		for _, progress := range plan.Progress {
			if v := progress.View(); v.Stage == domain.Pending || v.Stage == domain.Prepared {
				if _, err = p.journal.Cancel(call, plan.Spec.ID(), v.Action); err != nil {
					return err
				}
			}
		}
	}
	return nil
}
