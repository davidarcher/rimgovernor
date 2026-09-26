package buildingruntime

import (
	"context"
	"crypto/sha256"
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
// (#460), under a policy that opens caskets, the melee lock: one
// violence-capable melee colonist drafted at each filled casket and one
// OpenCasket order, held lock_understaffed while the squad cannot cover
// every casket. The optional heat fallback builds heaters and opens by a
// doorway shot when the melee lock is understaffed. Off policy, filled
// caskets stay sealed.
type RoutineShrinePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineShrineSource
}
type RoutineShrineResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	// Hold is the readiness reason the planner held on (BuildingMethodHeld)
	// and Shrine the shrine it judged.
	Hold, Shrine string
	// Skipped is every candidate the step judged and passed over, with its
	// own reason, beside the one Shrine names (#680).
	Skipped         []policy.ShrineHold
	NativeWorkTicks uint32
}

// BuildingMethodHeld is the shrine planner's answer while every target
// shrine holds; RoutineShrineResult.Hold carries the reason.
const BuildingMethodHeld RoutineBuildingReason = "breach_held"

func NewRoutineShrinePlanner(reviewer *RoutineReviewer, native RoutineShrineSource) (*RoutineShrinePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	if reviewer.policy.Shrine.HeatFallback {
		if _, ok := native.(shrineHeatSource); !ok {
			return nil, ErrControl
		}
	}
	return &RoutineShrinePlanner{reviewer, native}, nil
}
func (r *RoutineShrinePlanner) Step(ctx context.Context) (RoutineShrineResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineShrinePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (result RoutineShrineResult, err error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineShrineResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineShrineResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineShrineResult{Reason: BuildingMethodNoReview}, nil
	}
	// The step's own answer goes on the review it planned under (#680), so
	// the journal names the shrine it held on rather than leaving the
	// advisory ShrineHolds, in identity order, to read as the cause. A
	// review filed since is the newer answer; the record yields to it.
	// held collects the candidates the step passed over; a step that went
	// on to act on a later shrine names every one of them skipped.
	var held RoutineShrineResult
	defer func() {
		if err != nil || result.Reason == "" {
			return
		}
		if result.Shrine != held.Shrine || result.Hold != held.Hold {
			result.Skipped = append(slices.Clone(held.Skipped), result.Skipped...)
			if held.Hold != "" && held.Shrine != result.Shrine {
				result.Skipped = append(result.Skipped, policy.ShrineHold{Shrine: held.Shrine, Reason: held.Hold})
			}
		}
		step := store.RoutineShrineStep{Tick: review.Tick, Reason: string(result.Reason), Shrine: result.Shrine, Hold: result.Hold, Plan: result.Plan, Skipped: result.Skipped}
		if _, recordErr := p.journal.RecordShrineStep(call, review.Revision, step); recordErr != nil && !errors.Is(recordErr, store.ErrConflict) {
			err = recordErr
		}
	}()
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.ClearAncientShrine {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineShrineResult{Reason: BuildingMethodNoDeficit}, nil
	}
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.ClearAncientShrine && row.Selected
	}
	if !selected {
		return RoutineShrineResult{Reason: BuildingMethodRefused}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		// A pause or authority change between the draft and the move
		// releases the draft; the moves riding on it and the open waiting on
		// them can then never dispatch, and while they stay open the goal
		// never re-plans (#707). Settle the plan's unissued work so a fresh
		// method drafts again.
		if len(orphanedDraftDependents(plan.Spec, plan.Progress)) > 0 {
			for _, progress := range plan.Progress {
				if v := progress.View(); v.Stage == domain.Pending || v.Stage == domain.Prepared {
					if _, err = p.journal.Cancel(call, method.Plan, v.Action); err != nil {
						return RoutineShrineResult{}, err
					}
				}
			}
			if plan, err = p.journal.LoadPlan(call, method.Plan); err != nil {
				return RoutineShrineResult{}, err
			}
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineShrineResult{Reason: BuildingMethodExistingWork}, nil
		}
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
		return RoutineShrineResult{}, ErrControl
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
		return RoutineShrineResult{Reason: BuildingMethodUnknown}, nil
	}
	targets := map[string]bool{}
	for _, id := range policy.ShrineClearanceTargets(shrines, r.reviewer.policy.Shrine) {
		targets[id] = true
	}
	var candidates []policy.AncientShrine
	for _, shrine := range shrines {
		if targets[shrine.ID] {
			candidates = append(candidates, shrine)
		}
	}
	if len(candidates) == 0 {
		return RoutineShrineResult{Reason: BuildingMethodNoDeficit}, nil
	}
	sort.Slice(candidates, func(i, j int) bool { return candidates[i].ID < candidates[j].ID })
	claims := policy.ShrineClaimTargets(candidates)
	for _, shrine := range candidates {
		if caskets := claims[shrine.ID]; len(caskets) > 0 {
			return r.claim(call, epoch, state, goal, shrine, caskets, started)
		}
	}
	held = RoutineShrineResult{Reason: BuildingMethodHeld}
	opens := policy.ShrineOpenTargets(candidates, r.reviewer.policy.Shrine)
	if len(opens) > 0 {
		squad, err := shrineSquad(call, r.native, boundary.Identity(state.Snapshot), nil)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		// Completed heat plans are retired, so a started fallback shows in
		// the epoch's history rather than the active methods (#679).
		history, err := p.journal.LoadGoalMethods(call, goal.Goal.ID, goal.Goal.Epoch)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		for _, shrine := range candidates {
			caskets := opens[shrine.ID]
			if len(caskets) == 0 {
				continue
			}
			lock := policy.ShrineMeleeLock(caskets, squad)
			heatStarted := false
			for _, method := range history {
				heatStarted = heatStarted || method.Epoch == goal.Goal.Epoch && strings.HasPrefix(string(method.Method), "heat_") && strings.Contains(string(method.Method), "-"+shrine.ID+"-")
			}
			if heatStarted {
				if !r.reviewer.policy.Shrine.HeatFallback {
					return RoutineShrineResult{Reason: BuildingMethodHeld, Shrine: shrine.ID, Hold: "heat_policy_disabled"}, nil
				}
				return r.heat(call, epoch, state, goal, shrine, caskets, squad, colony.Projection, started, arbiter)
			}
			if lock.Reason != "" {
				if r.reviewer.policy.Shrine.HeatFallback {
					return r.heat(call, epoch, state, goal, shrine, caskets, squad, colony.Projection, started, arbiter)
				}
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
func (r *RoutineShrinePlanner) claim(call, epoch context.Context, state ControlState, goal store.GoalState, shrine policy.AncientShrine, caskets []policy.ShrineCasket, started time.Time) (RoutineShrineResult, error) {
	p := r.reviewer.player
	prefix := fmt.Sprintf("claim-%s-", shrine.ID)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineShrineResult{Reason: BuildingMethodExhausted, Shrine: shrine.ID}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-shrine-%x", digest[:16]))
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
		value, err := domain.NewClaimBuilding(casket.EntityID, target.Token)
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
		return RoutineShrineResult{Reason: BuildingMethodNoDeficit, Shrine: shrine.ID}, nil
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
		return RoutineShrineResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineShrineResult{}, err
	}
	return RoutineShrineResult{Reason: BuildingMethodAdmitted, Plan: id, Shrine: shrine.ID}, nil
}

// breach commits one sealed shrine's method: an owned draft and a move to a
// standing cell behind the trap line for each drafted defender, and the
// breach deconstruction of the chosen wall. One colonist is always left
// undrafted for the deconstruct job. The method is retried at most
// maxMedicalAttemptsPerPatient times per wall and goal epoch.
func (r *RoutineShrinePlanner) breach(call, epoch context.Context, state ControlState, goal store.GoalState, shrine policy.AncientShrine, report ShrineReadinessReport, projection observation.ColonyProjection, started time.Time, arbiter *stepArbiter) (RoutineShrineResult, error) {
	p := r.reviewer.player
	wall := report.Readiness.Wall
	colonists, known := projection.Facts.Colonists.Value()
	if !known {
		return RoutineShrineResult{Reason: BuildingMethodUnknown}, nil
	}
	drafted := policy.ShrineBreachDrafts(report.Readiness.Squad, int(colonists))
	if !arbiter.tryClaim(drafted) {
		return RoutineShrineResult{Reason: BuildingMethodUsed}, nil
	}
	positions := policy.ShrineBreachPositions(wall, drafted, report.Standing, report.Traps)
	prefix := fmt.Sprintf("breach-%s-%s-", shrine.ID, wall.EntityID)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineShrineResult{Reason: BuildingMethodExhausted, Shrine: shrine.ID}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-shrine-%x", digest[:16]))
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
	value, err := domain.NewBreachDeconstruction(wall.EntityID, definition, wall.Cell)
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
		return RoutineShrineResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineShrineResult{}, err
	}
	return RoutineShrineResult{Reason: BuildingMethodAdmitted, Plan: id, Shrine: shrine.ID}, nil
}

// open commits one open, guard-free shrine's melee lock (#460): an owned
// draft and a move to the casket's interaction cell for each locker, then
// one OpenCasket by the opener on the lowest casket, which ejects every
// casket of the group. The lockers stand where the ancients drop and their
// drafted auto-attack answers a waking hostile; the plan has no further
// work, so the worker keeps the drafts until the opening resolves and
// ActiveCombat and the custody planner take the occupants from there.
func (r *RoutineShrinePlanner) open(call, epoch context.Context, state ControlState, goal store.GoalState, shrine policy.AncientShrine, caskets []policy.ShrineCasket, lock policy.ShrineLock, started time.Time, arbiter *stepArbiter) (RoutineShrineResult, error) {
	p := r.reviewer.player
	lockers := make([]domain.PawnID, 0, len(lock.Lockers))
	for _, casket := range caskets {
		lockers = append(lockers, lock.Lockers[casket.EntityID])
	}
	if !arbiter.tryClaim(lockers) {
		return RoutineShrineResult{Reason: BuildingMethodUsed}, nil
	}
	prefix := fmt.Sprintf("open-%s-", shrine.ID)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineShrineResult{Reason: BuildingMethodExhausted, Shrine: shrine.ID}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-shrine-%x", digest[:16]))
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
		return RoutineShrineResult{}, ErrControl
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
		return RoutineShrineResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineShrineResult{}, err
	}
	return RoutineShrineResult{Reason: BuildingMethodAdmitted, Plan: id, Shrine: shrine.ID}, nil
}
