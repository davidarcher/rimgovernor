package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

type WorkerConfig struct {
	BreakSource BreakResponseSource
	// Pawns and Moves drive the arrival hold (arrivalHolds): an action
	// that depends on a completed move waits until its pawn stands on the
	// destination, and an idle drafted mover's order is sent again. Nil
	// Pawns disables the hold; nil Moves never resends.
	Pawns                                 ArrivalPawns
	Moves                                 boundary.ActionsWriter
	RoutineMethods                        bool
	StepInterval, MaxBackoff, StepTimeout time.Duration
	RenewInterval, RenewTimeout           time.Duration
	// MaxDispatches bounds the actions one step runs before it yields the
	// player gate; zero means workerDefaultDispatches. Every eligible
	// independent action is dispatched in the step that finds it (#593):
	// one per step cost a scheduler-step round trip per wall segment. A
	// dependent action still waits for its prerequisite's outcome, which
	// Hands enforces.
	MaxDispatches int
	// Wake names attempts whose outcome the native clock latched; the next
	// step reconciles them first and ignores their backoff. Nil keeps the
	// ticker cadence.
	Wake *WakeSignal
	// Advanced, when set, is called after a step that moved an action to a
	// new stage, so the clock step loop reviews at once (issue #162).
	Advanced func()
	// Store is the scheduler's decoded state store. A dispatch native
	// refuses for a map-consuming kind (a placement or zone whose CAS token
	// no longer matches) asks it to resync the planning window in full on
	// its next refresh instead of trusting a delta (#357); nil asks nothing.
	Store *facts.Store
	// WindowRunning, when set, reports the scheduler's hint that its window
	// is running; each native call the worker issues is recorded with it
	// as a "worker_dispatch" flight row (#243), so a run can count the
	// dispatches made live and the fraction native refused.
	WindowRunning func() bool
	// Trace, when set, is the trace of the scheduler's latest step
	// (ClockWorker.Trace). Each worker step is a span under it and each
	// dispatch a span under the step, so every row a dispatch leaves joins
	// the trace of the step that admitted the window it ran in (#298).
	// Nil starts a trace per worker step.
	Trace func() telemetry.Trace
	// Validity, when set, is the read validity of the scheduler's latest
	// step (ClockScheduler.Validity, #624): each dispatch runs under it, so
	// its boundaries judge their reads by age class (a dispatch
	// precondition within one dispatch's reads at the window's pace, the
	// cached emergency census within the step's) instead of the global
	// drift. Nil leaves the dispatches on the compatibility shim.
	Validity func() (domain.ReadValidity, bool)
	// Flush, when set, asks native to capture the snapshot frame the
	// step's writes deferred (bridge.FlushSnapshot, #1274): a step's
	// dispatches send defer_snapshot and the step flushes once at its end,
	// so a dispatch wave pays one frame capture instead of one per apply.
	// Nil leaves every dispatch capturing its own frame.
	Flush func(context.Context) error
}

// routineExecutableKind lists every action kind the worker (and, for a
// non-current plan, AuthorizeRoutinePlan) is allowed to dispatch or observe.
// Growing this list is how a new action family joins live automatic
// execution; each kind here already carries its own CAS-admission-guarded
// executor/boundary pair, so this is an allowlist of what has that
// machinery, not a bypass of it.
func routineExecutableKind(kind domain.ActionKind) bool {
	switch kind {
	case domain.BuildingAction, domain.OwnedDraftAction, domain.SubdueAction,
		domain.SupplyAllowAction, domain.SupplyForbidAction, domain.WorkAssignmentAction, domain.AcquisitionAction, domain.AcquisitionWithdrawAction, domain.ZoneCreateAction,
		domain.ProductionBillAction, domain.TendAction, domain.RescueAction, domain.CaptureAction, domain.UseItemAction, domain.HaulAction, domain.EquipAction,
		domain.GearReplaceAction, domain.ApparelPolicyAction, domain.RecoveryServiceAction, domain.MovementAction, domain.HusbandryAction,
		domain.PrisonerInteractionAction, domain.RepairAction, domain.CleanAction, domain.MineAcquisitionAction, domain.OpenCasketAction,
		domain.BuildingTemperatureAction, domain.BedUseAction, domain.GrowerCropAction, domain.ClaimBuildingAction, domain.AutoRefuelAction, domain.SurgeryAction, domain.AutoHomeAreaAction, domain.AreaAction, domain.ZoneDeleteAction, domain.ZoneCellEditAction, domain.StockpilePatchAction, domain.BedAssignAction, domain.ExcavationAction, domain.DialogAnswerAction, domain.NamingConfirmationAction, domain.ResearchSelectAction, domain.TradeAction, domain.DeconstructionAction, domain.CutPlantAction, domain.StripAction, domain.MoveBuildingAction, domain.UninstallBuildingAction, domain.CoverClearanceAction, domain.FoundationRemovalAction, domain.HomeCoverageAction, domain.QuestAcceptAction, domain.WallRemovalAction, domain.CaravanDepartureAction:
		return true
	default:
		return false
	}
}

type workerSession interface {
	playerSession
	RunBatch(context.Context, domain.PlanID, []domain.ActionID) ([]executor.BatchItem, error)
	ObserveTarget(context.Context, domain.GenerationSnapshot) error
}

// Worker owns Player's lifecycle, but its caller retains bridge and store handles
// until Close succeeds. Background work cannot acquire player authority.
type Worker struct {
	player      *Player
	session     workerSession
	config      WorkerConfig
	ctx         context.Context
	cancel      context.CancelFunc
	stopPlayer  func() bool
	done        chan struct{}
	stopContext func() bool
	// Only the step loop accesses scheduling state. The catalog bounds this map.
	waits map[domain.ActionID]workerWait
	// cursor is the plan of the last rotation-selected action: the next
	// step starts at the first candidate of the plan after it in the
	// catalog, even once that plan has left the catalog (retired), so
	// neither the catalog's head nor one long plan can monopolise steps.
	cursor      domain.PlanID
	cursorKnown bool
	// focus holds woken actions not yet reconciled since the wake.
	focus map[domain.ActionID]WakeOutcome
	// advanced reports that the last step moved an action to another
	// stage, so a successor may have become dispatchable.
	advanced bool
}

// workerBurstMax bounds the steps one wake or advance runs back to back.
const workerBurstMax = 16

// workerDefaultDispatches is MaxDispatches when the config leaves it zero:
// the whole of an ordinary plan (a wall run, a room's furniture) in one
// step, bounded so a long catalog still yields the gate within the step
// budget.
const workerDefaultDispatches = 16

func (w *Worker) dispatchBudget() int {
	if w.config.MaxDispatches > 0 {
		return w.config.MaxDispatches
	}
	return workerDefaultDispatches
}

type workerCandidate struct {
	view domain.ProgressView
	plan domain.PlanID
	kind domain.ActionKind
	// action and snapshot are what a dispatch inspects: the action's spec
	// and the authority snapshot the plan dispatches under.
	action   domain.Action
	snapshot domain.GenerationSnapshot
}

type workerWait struct {
	view  domain.ProgressView
	scope ControlState
	delay time.Duration
	until time.Time
	// outcome is the last surfaced "stage/refusals/error" of this action, so
	// a sustained hold logs once instead of every step (see issue #70);
	// repeats counts the unlogged runs that restated it, reported when the
	// outcome next changes so a reader can see how long the hold lasted.
	outcome string
	repeats int
	// stale records that the run was held on stale facts (workerHeldStale):
	// the next stop is a new observation, so it is retried there at once.
	stale bool
	// cancelled counts consecutive steps whose dispatch of this undispatched
	// action lost its own call context while the step's was live (#671).
	cancelled int
}

// workerOutcome keys one action run by what a reader of the log needs to
// diagnose a stuck action: its stage, the executor's refusal reasons and the
// error. Refusal reasons matter most -- an owned draft held before prepare
// otherwise leaves no trace but a bare "building execution held".
func workerOutcome(after domain.ProgressView, result executor.Result, err error) string {
	reasons := make([]string, 0, len(result.Refused))
	for _, r := range result.Refused {
		reasons = append(reasons, string(r.Reason))
	}
	// The receipt and effect name why an attempt is unresolved: an unknown
	// receipt is a native call that timed out on the controller side (#71),
	// and with the attempt number it identifies the native ledger entry.
	receipt, effect := "-", "-"
	if v, known := after.Receipt.Value(); known {
		receipt = string(v)
	}
	if v, known := after.Effect.Value(); known {
		effect = string(v)
	}
	return fmt.Sprintf("stage=%s attempt=%d receipt=%s effect=%s refused=[%s] err=%v", after.Stage, after.Attempt, receipt, effect, strings.Join(reasons, ","), err)
}

func NewWorker(ctx context.Context, config WorkerConfig, player *Player, session *Session) (*Worker, error) {
	if session == nil || player == nil || player.session != session || player.journal != session.journal {
		return nil, fmt.Errorf("%w: NewWorker: session == nil || player == nil || player.session != session || player.journal != session.journal", ErrControl)
	}
	if config.RoutineMethods && !session.routineMethods {
		return nil, fmt.Errorf("%w: NewWorker: config.RoutineMethods && !session.routineMethods", ErrControl)
	}
	return newWorker(ctx, config, player, session)
}
func newWorker(ctx context.Context, config WorkerConfig, player *Player, session workerSession) (*Worker, error) {
	if player == nil || session == nil || player.session != session || config.StepInterval <= 0 || config.MaxBackoff < config.StepInterval || config.MaxBackoff > time.Minute || config.StepTimeout <= 0 || config.StepTimeout > player.config.CallTimeout {
		return nil, fmt.Errorf("%w: newWorker: player == nil || session == nil || player.session != session || config.StepInterval <= 0 || config.MaxBacko", ErrControl)
	}
	player.mu.Lock()
	defer player.mu.Unlock()
	if player.workerAttached || player.closing || player.closed || player.lifetime.Err() != nil {
		return nil, fmt.Errorf("%w: newWorker: player.workerAttached || player.closing || player.closed || player.lifetime.Err() != nil", ErrControl)
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	lifetime, cancel := context.WithCancel(ctx)
	w := &Worker{player: player, session: session, config: config, ctx: lifetime, cancel: cancel, done: make(chan struct{}), waits: make(map[domain.ActionID]workerWait)}
	player.workerAttached = true
	w.stopPlayer = context.AfterFunc(player.lifetime, cancel)
	w.stopContext = context.AfterFunc(lifetime, func() { _ = w.stop() })
	var loops sync.WaitGroup
	loops.Add(1)
	go func() { defer loops.Done(); w.steps() }()
	go func() { loops.Wait(); w.stop(); close(w.done) }()
	return w, nil
}

// stop rejects new player requests before waiting for a possibly slow native call.
func (w *Worker) stop() error {
	p := w.player
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.closed || p.closing {
		return nil
	}
	p.closing = true
	p.cancelEpoch()
	return p.session.Disable()
}
func (w *Worker) Close(ctx context.Context) error {
	w.cancel()
	disableErr := w.stop()
	select {
	case <-w.done:
	case <-ctx.Done():
		return errors.Join(disableErr, ctx.Err())
	}
	// Player.Close already serializes concurrent closers through its gate.
	w.stopPlayer()
	w.stopContext()
	return errors.Join(disableErr, w.player.Close(ctx))
}
func (w *Worker) steps() {
	ticker := time.NewTicker(w.config.StepInterval)
	defer ticker.Stop()
	// The per-action outcome line already names the failing action; the
	// step-level error only adds information when it changes.
	previous, repeats := "", 0
	burst := 0
	for {
		if w.ctx.Err() != nil {
			return
		}
		err := w.step(w.ctx, time.Now())
		burst++
		message := ""
		if err != nil {
			message = err.Error()
		}
		if message != previous {
			clockSchedulerLog("worker step: err=%v%s", err, workerRepeats(repeats))
			previous, repeats = message, 0
		} else {
			repeats++
		}
		// A wake steps at once; while focused actions remain, keep stepping
		// so each of them is reconciled without waiting a StepInterval, and
		// a step that advanced an action steps again so the successor it
		// unblocked dispatches before the clock readmits a window rather
		// than a StepInterval or its own backoff later (issue #162).
		if (len(w.focus) > 0 || w.advanced) && burst < workerBurstMax {
			continue
		}
		burst = 0
		select {
		case <-w.ctx.Done():
			return
		case <-ticker.C:
		case <-w.config.Wake.C():
		}
	}
}

// workerFocusMax bounds the woken set a single step carries forward.
const workerFocusMax = 64

func (w *Worker) takeWake() {
	woken, _ := w.config.Wake.Take()
	if len(woken) == 0 {
		return
	}
	// Latched outcomes change what every other action in their plans is
	// waiting on: drop the backoffs so successors are reconsidered at once.
	w.waits = map[domain.ActionID]workerWait{}
	if w.focus == nil {
		w.focus = map[domain.ActionID]WakeOutcome{}
	}
	for id, outcome := range woken {
		if len(w.focus) >= workerFocusMax {
			break
		}
		w.focus[id] = outcome
		delete(w.waits, id)
	}
}

func (w *Worker) focusNamed(id domain.ActionID) bool { _, ok := w.focus[id]; return ok }
func (w *Worker) step(ctx context.Context, now time.Time) error {
	w.advanced = false
	w.takeWake()
	var parent telemetry.Trace
	if w.config.Trace != nil {
		parent = w.config.Trace()
	}
	stepTrace := parent.Child()
	call := telemetry.WithTrace(ctx, stepTrace)
	// StepTimeout budgets the dispatches, not the wait for the player
	// gate: a scheduler step holds the gate for its whole planner wave, and
	// a budget that started before the wait left the step 0-3 s for its
	// actions and killed the last one at the deadline (#410).
	call, epoch, done, err := w.player.enter(call, "worker", false)
	if err != nil {
		return err
	}
	defer done()
	call, cancel := context.WithTimeout(call, w.config.StepTimeout)
	defer cancel()
	if w.config.Validity != nil {
		if v, ok := w.config.Validity(); ok {
			call = domain.WithReadValidity(call, v)
		}
	}
	world, worldErr := w.player.worlds.ReadWorld(call)
	if worldErr == nil {
		worldErr = world.Validate()
	}
	if worldErr != nil {
		// A read that ran out of the step's budget (a scheduler step held
		// the gate for most of it) or failed in transport says nothing
		// about the world: the step fails and the next one reads again
		// (#342). Only a world that reads back unavailable or different
		// is evidence against the authority.
		if call.Err() != nil || errors.Is(worldErr, context.DeadlineExceeded) || errors.Is(worldErr, context.Canceled) || errors.Is(worldErr, bridge.ErrTransport) {
			return worldErr
		}
		if err = w.session.Disable(); err != nil {
			return errors.Join(worldErr, err)
		}
	}
	if err = w.player.current(call, epoch); err != nil {
		return err
	}
	scope := w.session.State()
	// Identity is a prerequisite for retaining permission, not just dispatch.
	// Keep the private cleanup scope, but stop renewal of an unavailable or
	// different world before considering any read-only reconciliation target.
	if scope.ObservationKnown && playerWorld(scope.Snapshot) != world || scope.Enabled && !scope.ObservationKnown {
		if err = w.session.Disable(); err != nil {
			return err
		}
		scope = w.session.State()
	}
	plans, err := w.player.journal.LoadPlans(call, 256)
	if err != nil {
		return err
	}
	breakHeld := map[domain.ActionID]bool{}
	if scope.Enabled && scope.ObservationKnown {
		breakHeld, err = w.breakDispatchHolds(call, scope.Snapshot, plans)
		if err != nil {
			return err
		}
		if w.config.Pawns != nil {
			for id := range w.arrivalHolds(call, scope.Snapshot, plans) {
				breakHeld[id] = true
			}
		}
	}
	live := make(map[domain.ActionID]bool)
	var candidates []workerCandidate
	// One author: the root plan carries authority, and every other plan --
	// routine method or player submission -- is dispatched under it once
	// authorized. There is no priority arbitration between them.
	for _, plan := range plans {
		planScope := scope
		if scope.Enabled && scope.ObservationKnown && plan.Spec.ID() != scope.Snapshot.Plan {
			target := scope.Snapshot
			target.Plan, target.Revision = plan.Spec.ID(), plan.Spec.Revision()
			if authErr := (planAuthorizer{w.player.journal, w.config.RoutineMethods}).AuthorizeRoutinePlan(call, scope.Snapshot, target); authErr == nil {
				planScope.Snapshot = target
			} else if clockDebug() {
				clockSchedulerLog("worker: authorize plan=%s root=%+v err=%v", plan.Spec.ID(), scope.Snapshot, authErr)
			}
		}
		for _, progress := range plan.Progress {
			v := progress.View()
			if !v.Unresolved && breakHeld[v.Action] {
				continue
			}
			routineObservation := w.config.RoutineMethods && v.Unresolved && routineExecutableKind(progress.Action().Kind()) && playerWorld(v.Snapshot) == world
			if clockDebug() && progress.Action().Kind() == domain.BuildingTemperatureAction {
				clockSchedulerLog("worker: temperature candidate action=%s stage=%v authorized=%v eligible=%v worldErr=%v", v.Action, v.Stage, planScope.Snapshot != scope.Snapshot, workerEligible(plan, v, planScope, world), worldErr)
			}
			if worldErr == nil && (routineObservation || workerEligible(plan, v, planScope, world)) {
				live[v.Action] = true
				candidates = append(candidates, workerCandidate{view: v, plan: plan.Spec.ID(), kind: progress.Action().Kind(), action: progress.Action(), snapshot: planScope.Snapshot})
			}
		}
	}
	for id := range w.waits {
		if !live[id] {
			delete(w.waits, id)
		}
	}
	for id := range w.focus {
		if !live[id] {
			delete(w.focus, id)
		}
	}
	// Catalog order is stable; rotate by plan, including on failed reads,
	// so an unavailable attempt cannot monopolize reconciliation. The
	// rotation resumes at the first candidate of the plan after the last
	// selection's, whether or not that plan still lists one: rotating by
	// action gave a forty-action shell plan every step until its last
	// action and restarted at the catalog's head whenever the selected
	// action completed, so the work-assignment plan sorted after the shell
	// never had a turn (#322). Within a plan the first candidate is its
	// earliest action, so a sequential plan still progresses in order.
	// Woken actions come first: the native clock latched their outcome, so
	// reconciling them is the reason this step runs; they leave the
	// rotation where it was.
	start := 0
	if w.cursorKnown {
		start = len(candidates)
		for i, v := range candidates {
			if v.plan > w.cursor {
				start = i
				break
			}
		}
		if len(candidates) > 0 {
			start %= len(candidates)
		}
	}
	ordered := make([]workerCandidate, 0, len(candidates))
	for _, focused := range []bool{true, false} {
		for n := 0; n < len(candidates); n++ {
			if candidate := candidates[(start+n)%len(candidates)]; w.focusNamed(candidate.view.Action) == focused {
				ordered = append(ordered, candidate)
			}
		}
	}
	// Every candidate off its backoff dispatches in this step, up to the
	// dispatch budget, so a plan's independent actions reach native in one
	// gate hold rather than one per scheduler round (#593). A dependent
	// action Hands holds until its prerequisite's outcome stays a held
	// result here and backs off like any other.
	var dispatched int
	var errs []error
	// One flush per step that sent a deferred write, on every exit: a
	// yield, an error, or the end of the candidates.
	sent := false
	defer func() {
		if sent {
			w.flush(call)
		}
	}()
	// A dispatch that starts with less of the step's budget left than the
	// longest dispatch so far took would lose its native call to the
	// deadline with the receipt unknown (a reconcile round trip later);
	// the step yields instead and the next one starts it whole (#410).
	var longest time.Duration
	deadline, bounded := call.Deadline()
	// A plan's building candidates go out together as one batched Apply
	// (#1042): the batch is one dispatch against the budget and the longest
	// estimate, and takes every eligible building candidate of its plan.
	taken := make(map[domain.ActionID]bool)
	for at, lead := range ordered {
		if dispatched >= w.dispatchBudget() {
			break
		}
		if bounded && dispatched > 0 && time.Until(deadline) < longest {
			clockSchedulerLog("worker: step budget short of a dispatch (%s left, longest %s): yielding after %d", time.Until(deadline).Round(time.Millisecond), longest.Round(time.Millisecond), dispatched)
			break
		}
		if taken[lead.view.Action] || w.backedOff(lead, scope, now) {
			continue
		}
		group := []workerCandidate{lead}
		if workerBatched(lead) {
			for _, next := range ordered[at+1:] {
				if next.plan == lead.plan && workerBatched(next) && !taken[next.view.Action] && !w.backedOff(next, scope, now) {
					group = append(group, next)
				}
			}
		}
		dispatched++
		focusedAt := make([]bool, len(group))
		for i, member := range group {
			taken[member.view.Action] = true
			focusedAt[i] = w.focusNamed(member.view.Action)
			delete(w.focus, member.view.Action)
			if !focusedAt[i] {
				w.cursor, w.cursorKnown = lead.plan, true
			}
		}
		if err = w.player.current(call, epoch); err != nil {
			return err
		}
		if !scope.Enabled {
			err = w.session.ObserveTarget(call, lead.view.Snapshot)
		}
		if err == nil {
			err = w.player.current(call, epoch)
		}
		results := make([]executor.Result, len(group))
		resultErrs := make([]error, len(group))
		dispatchStarted := time.Now()
		running := w.config.WindowRunning != nil && w.config.WindowRunning()
		run, tally := bridge.WithReadTally(telemetry.WithTrace(call, stepTrace.Child()))
		if err != nil {
			for i := range group {
				resultErrs[i] = err
			}
		} else {
			if w.config.Flush != nil {
				run, sent = bridge.WithDeferredSnapshot(run), true
			}
			results, resultErrs = w.runBatch(call, run, lead.view.Plan, group)
		}
		longest = max(longest, time.Since(dispatchStarted))
		for _, result := range results {
			if result.NativeCalled {
				// The decoded store drops what the dispatch changed:
				// only a clock events page would drop it otherwise, and a
				// next decision replans from the rows before this write.
				if everything, families := operationFamilies(lead.kind, true); everything {
					w.config.Store.InvalidateAll()
				} else {
					w.config.Store.InvalidateFamily(families...)
				}
				break
			}
		}
		stop := false
		for i, candidate := range group {
			v := candidate.view
			wait := w.waits[v.Action]
			result, err := results[i], resultErrs[i]
			after := v
			if result.Progress.View().Action == v.Action {
				after = result.Progress.View()
			}
			stale := workerHeldStale(after, result, err)
			if result.NativeCalled {
				workerDispatchRow(run, tally, candidate.view, after, running, stale, err)
			}
			delay := w.config.StepInterval
			if workerSameView(after, v) && workerSameView(wait.view, v) && wait.scope == workerScope(scope) {
				delay = min(wait.delay*2, workerBackoffCap(w.config, after))
			}
			if err == nil && after.Stage != v.Stage {
				w.advanced = true
			}
			// A dispatch held on stale facts (the authority or tick moved under
			// its inspection) clears on the next observation, so it is retried
			// at once, off its backoff, before the game's own work scanner takes
			// the order's target (#288); the clock is not held for it, since
			// every routine kind dispatches under the running window (#244). One
			// retry per hold: a hold that survives it backs off as usual.
			if stale && !wait.stale {
				if w.focus == nil {
					w.focus = map[domain.ActionID]WakeOutcome{}
				}
				w.focus[v.Action] = WakeOutcome{Action: v.Action, Attempt: after.Attempt}
			}
			// One line per change of outcome, in either log: a refusal that
			// repeats verbatim on every retry (a CAS token that never matches, a
			// native read refused for the same reason) would otherwise dominate
			// the run's log without adding anything a reader can act on (#100).
			outcome := workerOutcome(after, result, err)
			repeats := wait.repeats
			if outcome != wait.outcome {
				workerOutcomeEvent(run, v, after, outcome, err, repeats)
				repeats = 0
			} else {
				repeats++
			}
			cancelled := 0
			own := workerOwnCancel(call, err) && after.Attempt == v.Attempt && (after.Stage == domain.Pending || after.Stage == domain.Prepared)
			if own {
				cancelled = 1
				if workerSameView(wait.view, v) {
					cancelled = wait.cancelled + 1
				}
			}
			// An undispatched action whose dispatch is cancelled step after step
			// never reaches native; parked at attempt 0 it would hold its goal
			// for ever (#671). It settles cancelled, so the goal re-plans.
			if cancelled >= workerCancelledSettle {
				if _, cancelErr := w.player.journal.Cancel(call, v.Plan, v.Action); cancelErr != nil {
					errs = append(errs, cancelErr)
				} else {
					slog.Default().Warn("worker settled a dispatch cancelled on every attempt", telemetry.ComponentKey, "worker", "action", string(v.Action), "stage", string(v.Stage), "attempts", cancelled, "err", err)
					delete(w.waits, v.Action)
					w.advanced = true
					continue
				}
			}
			w.waits[v.Action] = workerWait{view: after, scope: workerScope(w.session.State()), delay: delay, until: now.Add(delay), outcome: outcome, repeats: repeats, stale: stale, cancelled: cancelled}
			if err != nil {
				errs = append(errs, err)
				// A step that ran out of budget or lost its transport says
				// nothing about the next candidate either (#342); one dispatch
				// cancelled by its own context says nothing about its siblings.
				if !own && (call.Err() != nil || errors.Is(err, context.DeadlineExceeded) || errors.Is(err, context.Canceled) || errors.Is(err, bridge.ErrTransport)) {
					stop = true
				}
			}
		}
		if stop {
			break
		}
	}
	if w.advanced && w.config.Advanced != nil {
		w.config.Advanced()
	}
	return errors.Join(append([]error{worldErr}, errs...)...)
}

// workerFlushTimeout bounds the end-of-step flush, which runs even when the
// step's own budget is spent.
const workerFlushTimeout = 5 * time.Second

// flush asks native to capture the step's deferred writes. A failure is
// logged only: native captures them anyway within its 1 s safety net.
func (w *Worker) flush(call context.Context) {
	ctx, cancel := context.WithTimeout(context.WithoutCancel(call), workerFlushTimeout)
	defer cancel()
	if err := w.config.Flush(ctx); err != nil {
		slog.Default().Warn("worker snapshot flush failed", telemetry.ComponentKey, "worker", "err", err)
	}
}

// workerBatched reports whether candidate joins its plan's other plain
// intents in one batched Apply (#1042); other kinds
// each dispatch alone.
func workerBatched(candidate workerCandidate) bool {
	return executor.PlainIntent(candidate.kind)
}

// runBatch sends group, one plan's candidates, as one RunBatch and
// returns each member's result and error in group order. A batch cancelled
// by its own context while the step's is live retries once, whole (#671).
func (w *Worker) runBatch(call, run context.Context, plan domain.PlanID, group []workerCandidate) ([]executor.Result, []error) {
	ids := make([]domain.ActionID, len(group))
	for i, member := range group {
		ids[i] = member.view.Action
	}
	results, errs := make([]executor.Result, len(group)), make([]error, len(group))
	for try := 0; try < 2; try++ {
		items, err := w.session.RunBatch(run, plan, ids)
		ownCancel := workerOwnCancel(call, err)
		for i := range group {
			results[i], errs[i] = executor.Result{}, err
			if i < len(items) {
				results[i] = items[i].Result
				if items[i].Err != nil {
					errs[i] = items[i].Err
				}
			}
			ownCancel = ownCancel || workerOwnCancel(call, errs[i])
		}
		if !ownCancel {
			break
		}
	}
	return results, errs
}

// workerCancelledSettle is how many consecutive steps an undispatched
// action's dispatch may be cancelled by its own context before the worker
// settles it cancelled (#671).
const workerCancelledSettle = 3

// workerOwnCancel reports whether err is the dispatch's own context
// cancellation while the step's context is still live.
func workerOwnCancel(call context.Context, err error) bool {
	return err != nil && call.Err() == nil && errors.Is(err, context.Canceled)
}

// backedOff reports whether candidate waits out a backoff from an earlier
// step's identical outcome; a woken action never does.
func (w *Worker) backedOff(candidate workerCandidate, scope ControlState, now time.Time) bool {
	v := candidate.view
	wait := w.waits[v.Action]
	return !w.focusNamed(v.Action) && workerSameView(wait.view, v) && wait.scope == workerScope(scope) && now.Before(wait.until)
}

// workerDispatchRow publishes one "worker_dispatch" flight row for a run
// that reached native: the action's kind and attempt, the receipt the run
// left (accepted, refused, unknown; "-" when the write was not a dispatch),
// whether the scheduler's window was running when the run began (#243),
// and the error. `rimgovernor phases` sums them (bridge.DispatchSample).
func workerDispatchRow(ctx context.Context, tally *bridge.ReadTally, before, after domain.ProgressView, running, stale bool, err error) {
	receipt := "-"
	if v, known := after.Receipt.Value(); known && (after.Attempt != before.Attempt || !workerSameReceipt(before, after)) {
		receipt = string(v)
	}
	// stale marks a run held on stale facts (workerHeldStale) so a run can
	// count the holds the read bounds refused (#624).
	extra := map[string]any{"action": string(after.Action), "attempt": after.Attempt, "stage": string(after.Stage), "receipt": receipt, "running": running, "stale": stale}
	if err != nil {
		extra["error"] = err.Error()
	}
	tally.PublishAs(ctx, "worker_dispatch", extra)
}

func workerSameReceipt(a, b domain.ProgressView) bool {
	x, xk := a.Receipt.Value()
	y, yk := b.Receipt.Value()
	return xk == yk && x == y
}

// workerOutcomeEvent publishes one "worker_outcome" event when an action's
// reconciliation outcome changes: the action, its stage before and after
// the run, the outcome text, and the error, at Warn when the run failed.
func workerOutcomeEvent(ctx context.Context, before, after domain.ProgressView, outcome string, err error, repeats int) {
	level := slog.LevelInfo
	if err != nil {
		level = slog.LevelWarn
	}
	slog.Default().Log(ctx, level, "worker outcome", telemetry.ComponentKey, "worker", telemetry.KindKey, "worker_outcome", "action", string(before.Action), "attempt", int64(after.Attempt), "stage", string(before.Stage), "stage_after", string(after.Stage), "outcome", outcome, "err", err, "repeated", repeats)
}

// workerRepeats renders how many unlogged runs restated the previous outcome.
func workerRepeats(n int) string {
	if n == 0 {
		return ""
	}
	return fmt.Sprintf(" (previous outcome repeated %d more times)", n)
}

// workerSameView reports whether two views of one action describe the same
// reconciliation state. The observed tick advances with every read of a
// running game, so it is not a change; a read that only restated an
// unresolved effect must back off like any other unchanged attempt, or a
// handful of unresolvable observations monopolise the one-action-per-step
// rotation and starve the plans that could progress.
func workerSameView(a, b domain.ProgressView) bool {
	a.Tick, b.Tick = 0, 0
	return a == b
}

// workerBackoffCap bounds the retry delay of an unchanged attempt. An
// observation the native side could not classify (EffectUnknown: the source
// or output is gone, the pawn is unobservable) rarely resolves within the
// ordinary cap and is rechecked at most once a minute; everything else keeps
// the configured cap so pending work is noticed promptly.
func workerBackoffCap(config WorkerConfig, v domain.ProgressView) time.Duration {
	if effect, known := v.Effect.Value(); known && effect == domain.EffectUnknown && v.Stage == domain.AwaitingObservation {
		return max(config.MaxBackoff, min(6*config.MaxBackoff, time.Minute))
	}
	return config.MaxBackoff
}

// workerHeldStale reports a run held before dispatch on facts from another
// world or generation, or a tick behind what was admitted: the executor's stale_facts
// refusal, or an emergency hold recorded for the same reason. Such a hold is
// expected to clear on the next observation, unlike a refusal that names a
// world condition (stock, geometry, an unavailable pawn).
func workerHeldStale(after domain.ProgressView, result executor.Result, err error) bool {
	if !errors.Is(err, executor.ErrHeld) || after.Unresolved || after.Stage != domain.Pending && after.Stage != domain.Prepared {
		return false
	}
	for _, refusal := range result.Refused {
		if refusal.Reason == policy.StaleFacts {
			return true
		}
	}
	if len(result.Refused) > 0 {
		return false
	}
	held, known := after.HeldReason.Value()
	if !known {
		return false
	}
	for _, reason := range held.Reasons() {
		if reason == domain.HeldStaleFacts {
			return true
		}
	}
	return false
}

func workerEligible(plan store.PlanState, v domain.ProgressView, scope ControlState, world store.World) bool {
	supported := false
	for _, action := range plan.Spec.Actions() {
		if action.ID() == v.Action && routineExecutableKind(action.Kind()) {
			supported = true
			break
		}
	}
	if !supported {
		return false
	}
	if scope.Enabled {
		if !scope.ObservationKnown || playerWorld(scope.Snapshot) != world || scope.Snapshot.Plan != v.Plan || scope.Snapshot.Revision != v.Revision {
			return false
		}
	} else {
		return v.Unresolved && playerWorld(v.Snapshot) == world
	}
	if v.Unresolved {
		return playerWorld(v.Snapshot) == world
	}
	if v.Stage != domain.Pending && v.Stage != domain.Prepared {
		return false
	}
	// A Prepared action has no write outstanding (dispatch is journaled before
	// the native call), so one whose snapshot went stale -- the step budget
	// cancelled its dispatch and the native generation then moved -- is
	// re-prepared under the current scope by the next run rather than
	// stranded behind an authority it can never dispatch against (#101).
	if v.Attempt == 0 {
		return true
	}
	// A trusted refusal is a no-effect proof. Only a later resume (a newer
	// native generation) authorizes another attempt; an unknown receipt always
	// stays reconciliation. A write the transport never issued refused
	// nothing, so the same generation may retry it.
	receipt, known := v.Receipt.Value()
	// An intent whose Apply receipt came back unknown is back at Pending
	// for a resend (#856). Like a refusal, only a later resume (a newer
	// native generation) resends it; native answers applied if the first
	// write landed, so the resend cannot duplicate the building.
	if known && receipt == domain.ReceiptUnknown && v.Stage == domain.Pending {
		return playerWorld(v.Snapshot) == world && scope.Snapshot.Native > v.Snapshot.Native
	}
	effect, effectKnown := v.Effect.Value()
	if !known || !effectKnown || effect != domain.EffectAbsent || playerWorld(v.Snapshot) != world {
		return false
	}
	// A refused attempt already re-prepared carries the newer generation that
	// authorized it, so it stays eligible until it dispatches.
	return receipt == domain.ReceiptUnsent || receipt == domain.ReceiptRefused && (v.Stage == domain.Prepared || scope.Snapshot.Native > v.Snapshot.Native)
}

// Read reconciliation may rotate plan targets without changing world authority.
// That rotation must not reset a paused attempt's cooldown.
func workerScope(scope ControlState) ControlState {
	if !scope.Enabled {
		scope.Snapshot.Plan = ""
		scope.Snapshot.Revision = 0
	}
	return scope
}

// workerPlanHoldsDraft reports whether plan still holds the draft v, either
// completed or still in flight (dispatched, receipt accepted, awaiting the
// observation a hold interrupted): no other action of the plan has failed
// or been cancelled, and at least one is still unfinished. An order riding
// on another draft (a move or a subdue naming a different
// draft action) says nothing about this one: a timed-out move for one
// defender must not undraft the others mid-order. An order riding on this
// draft that native is still executing holds it outright: a recovered goal
// cancels the plan's unissued orders (settleUnissuedWork) and leaves the
// dispatched ones to close on their own, which they cannot once the pawn
// is undrafted under them. A completed subdue order (#856) stands
// on its draft the same way: its intent receipt is terminal while native
// keeps fighting, so the draft is held until the planner cancels the order
// (releaseBreakWork, once the victim is downed or out of the break).
func workerPlanHoldsDraft(plan store.PlanState, v domain.ProgressView) bool {
	switch v.Stage {
	case domain.Completed, domain.Dispatched, domain.AwaitingObservation:
	default:
		return false
	}
	unfinished, settled := false, false
	for _, p := range plan.Progress {
		other := p.View()
		if other.Action == v.Action {
			continue
		}
		rides := workerOrderDraft(p.Action())
		if rides != "" && rides != v.Action {
			continue
		}
		switch other.Stage {
		case domain.Cancelled, domain.Unsuccessful:
			settled = true
		case domain.Dispatched, domain.AwaitingObservation:
			if rides == v.Action {
				return true
			}
			unfinished = true
		case domain.Pending, domain.Prepared:
			unfinished = true
		case domain.Completed:
			if rides == v.Action && p.Action().Kind() == domain.SubdueAction {
				return true
			}
		}
	}
	return unfinished && !settled
}

// workerOrderDraft names the owned draft an order rides on, or "" for an
// action that is not a drafted order.
func workerOrderDraft(action domain.Action) domain.ActionID {
	if m, ok := action.Movement(); ok {
		return m.DraftAction()
	}
	if m, ok := action.Subdue(); ok {
		return m.DraftAction()
	}
	return ""
}
