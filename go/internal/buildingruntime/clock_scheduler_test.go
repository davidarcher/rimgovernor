package buildingruntime

import (
	"context"
	"reflect"
	"slices"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type schedulerNative struct {
	*clockCoreFake
	emergency policy.EmergencyFacts
}

// ReadStep answers what the fake's Tick, ReadClockStatus and
// ReadEmergency answer, from one call (issue #127).
func (f *schedulerNative) ReadStep(ctx context.Context, request bridge.StepRequest) (*o.BundleSnapshot, bridge.Result, error) {
	return composeStep(ctx, request, bundleParts{tick: f.Tick, status: f.ReadClockStatus, emergency: f.ReadEmergency})
}

func (f *schedulerNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Context: proto.Clone(f.status.Context).(*c.ObservationContext), Facts: f.emergency}, bridge.Result{}, ctx.Err()
}
func schedulerFixture(t *testing.T) (*ClockScheduler, *schedulerNative) {
	t.Helper()
	return schedulerFixturePlan(t, nil)
}

// schedulerFixturePlan is schedulerFixture over a plan with extra actions
// and dependencies after the fixture placement.
func schedulerFixturePlan(t *testing.T, extra []domain.Action, dependencies ...domain.ActionDependency) (*ClockScheduler, *schedulerNative) {
	t.Helper()
	db, err := store.Open(context.Background(), storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	s, native := schedulerFixtureJournal(t, db, extra, dependencies...)
	return s, native
}

// schedulerFixtureJournal is schedulerFixturePlan over an opened journal,
// for tests that seed it directly.
func schedulerFixtureJournal(t *testing.T, db *store.Store, extra []domain.Action, dependencies ...domain.ActionDependency) (*ClockScheduler, *schedulerNative) {
	t.Helper()
	_, _, f, intent := clockCoreFixtureOver(t, db)
	s, _, profile := newClockSessionTestPlan(t, db, f, extra, dependencies...)
	p, err := NewPlayer(context.Background(), PlayerConfig{CallTimeout: 10 * time.Second, JournalTimeout: 10 * time.Second}, db, s, playerWorldFunc(func(context.Context) (store.World, error) { return playerWorld(intent.Snapshot), nil }))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = p.Close(context.Background()) })
	if _, err = s.Acquire(context.Background(), intent.Snapshot); err != nil {
		t.Fatal(err)
	}
	native := &schedulerNative{clockCoreFake: f, emergency: policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}}
	scheduler, err := NewClockScheduler(p, s, native, ClockSchedulerConfig{Profile: profile, Start: *intent.Command.Start, MaxAge: time.Second}, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	return scheduler, native
}
func TestClockSchedulerStartsOnceAndLeavesRunningEpoch(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	got, err := s.Step(context.Background())
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || f.writes != 1 {
		t.Fatal(got, err, f.writes)
	}
	second, err := s.Step(context.Background())
	if err != nil || !second.Running || f.writes != 1 || f.pauses != 0 {
		t.Fatal(second, err)
	}
	if err = s.session.Disable(); err != nil {
		t.Fatal(err)
	}
	cleanup, err := s.Step(context.Background())
	if err != nil || !cleanup.Cleaned || f.pauses != 1 || f.writes != 1 {
		t.Fatal(cleanup, err)
	}
}

func TestClockSchedulerUnknownRecoversExactRequest(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	f.lost = true
	first, err := s.Step(context.Background())
	if err == nil || first.Attempt == nil || first.Attempt.Phase != store.ClockUncertain {
		t.Fatal(first, err)
	}
	second, err := s.Step(context.Background())
	if err != nil || !second.Reconciled || second.Attempt == nil || second.Attempt.Intent.RequestID != first.Attempt.Intent.RequestID || f.writes != 1 {
		t.Fatal(second, err)
	}
}
func TestClockSchedulerUnsafeAndUnreviewedNeverStart(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"unsafe", "unreviewed", "unknown", "disabled", "cancelled"} {
		t.Run(kind, func(t *testing.T) {
			s, f := schedulerFixture(t)
			ctx := context.Background()
			switch kind {
			case "unsafe":
				f.emergency.Threats = []policy.EmergencyThreat{{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}
			case "unreviewed":
				f.status.NewestCursor = proto.Int64(1)
			case "unknown":
				f.emergency.ColonistsComplete = domain.Unknown[bool]()
			case "disabled":
				_ = s.session.Disable()
			case "cancelled":
				var cancel context.CancelFunc
				ctx, cancel = context.WithCancel(ctx)
				cancel()
			}
			if _, err := s.Step(ctx); err == nil {
				t.Fatal("admitted")
			}
			if f.writes != 0 {
				t.Fatal("native start")
			}
		})
	}
}
func TestClockSchedulerIdentityAndInertPrepared(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	// Prepare the exact prospective request without dispatch, then let native time
	// advance. Undispatched history must not permanently prevent a new window.
	state := s.session.State()
	plan, err := s.player.journal.LoadPlan(context.Background(), state.Snapshot.Plan)
	if err != nil {
		t.Fatal(err)
	}
	_, work, err := clockSchedulerWork(plan, state.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	admission := &store.ClockWindowAdmission{Profile: s.config.Profile, Snapshot: state.Snapshot, Tick: 11, MaxTicks: s.config.Start.MaxTicks}
	key, err := clockSchedulerKey(admission, work, s.config.Start)
	if err != nil {
		t.Fatal(err)
	}
	same, _ := clockSchedulerKey(admission, work, s.config.Start)
	if same != key {
		t.Fatal("unstable logical key")
	}
	accelerated := s.config.Start
	accelerated.Speed, accelerated.TestAcceleration = k.Speed_SPEED_ULTRAFAST, true
	if other, _ := clockSchedulerKey(admission, work, accelerated); other == key {
		t.Fatal("logical key ignores test acceleration")
	}
	start := s.config.Start
	id := clockTestNextID(t, s.player.journal)
	_, _, err = s.player.journal.PrepareClock(context.Background(), store.ClockIntent{RequestID: id, Key: key, Snapshot: state.Snapshot, Command: bridge.ClockCommand{Start: &start}, Window: admission})
	if err != nil {
		t.Fatal(err)
	}
	got, err := s.Step(context.Background())
	if err != nil || got.Attempt == nil || got.Attempt.Intent.RequestID == id || f.writes != 1 {
		t.Fatal(got, err)
	}
}
func TestClockSchedulerRejectsProfileAndSuppression(t *testing.T) {
	t.Parallel()
	s, _ := schedulerFixture(t)
	config := s.config
	config.Profile = t.TempDir()
	if _, err := NewClockScheduler(s.player, s.session, s.native, config, s.clock); err == nil {
		t.Fatal("foreign profile")
	}
	config = s.config
	config.Start.Policy = proto.Clone(config.Start.Policy).(*k.WatchPolicy)
	config.Start.Policy.AcknowledgedHostileIds = []string{"raider"}
	if _, err := NewClockScheduler(s.player, s.session, s.native, config, s.clock); err == nil {
		t.Fatal("suppression")
	}
}

func TestClockSchedulerCancelledWorkCannotAdvanceTime(t *testing.T) {
	t.Parallel()
	s, _ := schedulerFixture(t)
	state, err := s.player.journal.LoadPlan(context.Background(), s.session.State().Snapshot.Plan)
	if err != nil {
		t.Fatal(err)
	}
	p := state.Progress[0]
	p, err = p.Prepare(s.session.State().Snapshot, 10)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.MarkDispatched(s.session.State().Snapshot, 10)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.Cancel()
	if err != nil {
		t.Fatal(err)
	}
	state.Progress[0] = p
	work, _, err := clockSchedulerWork(state, s.session.State().Snapshot)
	if err != nil || work || !p.View().Unresolved {
		t.Fatal(work, err, p.View())
	}
}

func TestClockSchedulerUnknownStartWorldReplacement(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	f.lost = true
	first, err := s.Step(context.Background())
	if err == nil || first.Attempt == nil {
		t.Fatal(first, err)
	}
	f.status.Context.Identity.LoadToken = proto.String("replacement")
	got, err := s.Step(context.Background())
	if err != nil || !got.Cleaned || f.writes != 1 || f.pauses != 0 {
		t.Fatal(got, err)
	}
	old, err := s.player.journal.LookupClockAttempt(context.Background(), first.Attempt.Intent.RequestID)
	if err != nil || old.SupersededAt == nil || old.Phase != store.ClockUncertain {
		t.Fatal(old, err)
	}
}

// combatGoalPlan binds an ActiveCombat goal to the scheduler's current review
// and commits a squad plan (draft plus subdue) whose work stays open.
func combatGoalPlan(t *testing.T, s *ClockScheduler) domain.PlanID {
	t.Helper()
	ctx := context.Background()
	state := s.session.State()
	facts := policy.RoutineFacts{Workers: domain.Known(1), Wood: domain.Known(int64(100)), Hostiles: domain.Known(int64(1)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false)}
	review, err := s.player.journal.ReviewRoutine(ctx, store.RoundsRequest{Current: state.Snapshot, Tick: 12, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: facts})
	if err != nil {
		t.Fatal(err)
	}
	binding, ok := review.Review.Incident(policy.ActiveCombat)
	if !ok || binding.Situation != domain.SituationActive {
		t.Fatal(review.Review.Incidents)
	}
	draft, err := domain.NewOwnedDraft("pawn")
	if err != nil {
		t.Fatal(err)
	}
	draftAction, err := domain.NewOwnedDraftAction("combat-draft", draft)
	if err != nil {
		t.Fatal(err)
	}
	attack, err := domain.NewSubdue("pawn", "raider", "combat-draft")
	if err != nil {
		t.Fatal(err)
	}
	attackAction, err := domain.NewSubdueAction("combat-attack", attack)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("routine-defense-test", 1, []domain.Action{draftAction, attackAction})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.player.journal.CommitIncidentMethod(ctx, binding.Incident, "squad-test", "", plan); err != nil {
		t.Fatal(err)
	}
	return plan.ID()
}

func TestClockSchedulerCombatPlanAdmitsBoundedCombatWindow(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	ctx := context.Background()
	config := s.config
	config.CombatMaxTicks = 30
	var err error
	if s, err = NewClockScheduler(s.player, s.session, s.native, config, s.clock); err != nil {
		t.Fatal(err)
	}
	f.emergency.Threats = []policy.EmergencyThreat{
		{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)},
		{ID: "archer", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)},
		{ID: "fallen", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(true)},
	}
	// Live hostiles without an admitted combat plan still refuse the window.
	if got, err := s.Step(ctx); err == nil || got.Combat || f.writes != 0 {
		t.Fatal(got, err)
	}
	combatGoalPlan(t, s)
	got, err := s.Step(ctx)
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || !got.Combat || got.Decision.Mode != policy.ClockWindowCombat || f.writes != 1 {
		t.Fatal(got, err, f.writes)
	}
	start := got.Attempt.Intent.Command.Start
	if start.MaxTicks != 30 || got.Attempt.Intent.Window.MaxTicks != 30 || start.Policy.GetMode() != k.WatchMode_WATCH_MODE_COMBAT || !reflect.DeepEqual(start.Policy.AcknowledgedHostileIds, []string{"archer", "raider"}) || len(start.Policy.AcknowledgedDownedColonistIds)+len(start.Policy.MedicalRestIds) != 0 {
		t.Fatal(start)
	}
	// No fight plan owns this combat (#852), so the window is disarmed:
	// its tick budget alone stops it.
	if len(start.Policy.CombatStopEvents) != 0 {
		t.Fatal("combat window armed without a fight", start.Policy.CombatStopEvents)
	}
	if s.config.Start.Policy.GetMode() != k.WatchMode_WATCH_MODE_COLONY || len(s.config.Start.Policy.AcknowledgedHostileIds)+len(s.config.Start.Policy.CombatStopEvents) != 0 {
		t.Fatal("combat window mutated the configured colony policy")
	}
	// The running combat epoch keeps the worker on its short poll.
	running, err := s.Step(ctx)
	if err != nil || !running.Running || !running.Combat || f.writes != 1 {
		t.Fatal(running, err)
	}
}

func TestClockSchedulerCombatEndsWithLastLiveHostile(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	ctx := context.Background()
	combatGoalPlan(t, s)
	f.emergency.Threats = []policy.EmergencyThreat{
		{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(true), Downed: domain.Known(false)},
		{ID: "archer", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(true)},
	}
	got, err := s.Step(ctx)
	if err != nil || got.Attempt == nil || got.Combat || got.Decision.Mode != policy.ClockWindowColony || f.writes != 1 {
		t.Fatal(got, err)
	}
	start := got.Attempt.Intent.Command.Start
	if start.Policy.GetMode() != k.WatchMode_WATCH_MODE_COLONY || len(start.Policy.AcknowledgedHostileIds) != 0 || start.MaxTicks != s.config.Start.MaxTicks {
		t.Fatal(start)
	}
}

func TestClockSchedulerRejectsCombatBudgetAboveColonyBudget(t *testing.T) {
	t.Parallel()
	s, _ := schedulerFixture(t)
	config := s.config
	config.CombatMaxTicks = config.Start.MaxTicks + 1
	if _, err := NewClockScheduler(s.player, s.session, s.native, config, s.clock); err == nil {
		t.Fatal("combat budget above colony budget")
	}
}

// An armed combat stop (#849) under an open fight plan (#852) is that
// plan's next decision: the scheduler resumes with another armed window.
// With the fight closed and hostiles still live, no window is admitted and
// the stop holds.
func TestClockSchedulerCombatStopResumesOnlyUnderAFight(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	ctx := context.Background()
	f.emergency.Threats = []policy.EmergencyThreat{{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}
	plan := combatGoalPlan(t, s)
	if err := s.player.journal.OpenCombatFight(ctx, plan, policy.CombatMemory{Tactic: policy.TacticSquad}, playerWorld(s.session.State().Snapshot), []domain.PawnID{"pawn"}); err != nil {
		t.Fatal(err)
	}
	got, err := s.Step(ctx)
	if err != nil || got.Attempt == nil || !got.Combat || f.writes != 1 {
		t.Fatal(got, err, f.writes)
	}
	if start := got.Attempt.Intent.Command.Start; !slices.Equal(start.Policy.CombatStopEvents, combatStopEvents) || len(combatStopEvents) != len(k.CombatEvent_name)-1 {
		t.Fatal("fight window not armed", start.Policy.CombatStopEvents)
	}
	stopCombatWindow := func() {
		t.Helper()
		epochs, err := s.player.journal.LoadClockEpochs(ctx, 4096)
		if err != nil || len(epochs) == 0 {
			t.Fatal(epochs, err)
		}
		epoch := proto.Clone(epochs[len(epochs)-1].Epoch).(*k.Epoch)
		f.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: k.StopReason_STOP_REASON_COMBAT_EVENT.Enum(), CombatEvent: k.CombatEvent_COMBAT_EVENT_ENTERED_RANGE.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(false), StoppedAtUnixMs: proto.Int64(1)}}
		f.status.ActualPaused, f.status.NativeTickBoundary, f.status.DurableEvents = proto.Bool(true), proto.Bool(true), proto.Bool(true)
		f.status.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum()
	}
	stopCombatWindow()
	// The fake's tick does not move, so the resumed window is the same
	// logical window: admitted again, not a new native write.
	got, err = s.Step(ctx)
	if err != nil || !got.Decision.Admitted || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || !got.Combat {
		t.Fatalf("an armed combat stop under a fight did not resume %+v %v", got.Decision, err)
	}
	stopCombatWindow()
	if err = s.player.journal.CloseCombatFight(ctx, plan); err != nil {
		t.Fatal(err)
	}
	// The fight retired but the plan's draft work is still open: the next
	// window is disarmed first, so no combat stop lands without an owner.
	got, err = s.Step(ctx)
	if err != nil || got.Attempt == nil || len(got.Attempt.Intent.Command.Start.Policy.CombatStopEvents) != 0 {
		t.Fatal("a window armed after its fight closed", got.Decision, err)
	}
	// With no plan at all, the live raider holds the clock.
	s2, f2 := schedulerFixture(t)
	f2.emergency.Threats = f.emergency.Threats
	if got, err = s2.Step(ctx); err == nil || got.Decision.Admitted || f2.writes != 0 {
		t.Fatal("a combat with no owning plan was admitted", got.Decision, err)
	}
}

// #886: once every plan action is settled, the open fight alone owns the
// combat; its next armed stop needs ticks, not a no_work park.
func TestClockSchedulerOpenFightIsWork(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	ctx := context.Background()
	f.emergency.Threats = []policy.EmergencyThreat{{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}
	plan := combatGoalPlan(t, s)
	if err := s.player.journal.OpenCombatFight(ctx, plan, policy.CombatMemory{Tactic: policy.TacticSquad}, playerWorld(s.session.State().Snapshot), []domain.PawnID{"pawn"}); err != nil {
		t.Fatal(err)
	}
	got, err := s.Step(ctx)
	if err != nil || got.Attempt == nil || !got.Combat || f.writes != 1 {
		t.Fatal(got, err, f.writes)
	}
	if start := got.Attempt.Intent.Command.Start; !slices.Equal(start.Policy.CombatStopEvents, combatStopEvents) || len(combatStopEvents) != len(k.CombatEvent_name)-1 {
		t.Fatal("fight window not armed", start.Policy.CombatStopEvents)
	}
	stopCombatWindow := func() {
		t.Helper()
		epochs, err := s.player.journal.LoadClockEpochs(ctx, 4096)
		if err != nil || len(epochs) == 0 {
			t.Fatal(epochs, err)
		}
		epoch := proto.Clone(epochs[len(epochs)-1].Epoch).(*k.Epoch)
		f.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: k.StopReason_STOP_REASON_COMBAT_EVENT.Enum(), CombatEvent: k.CombatEvent_COMBAT_EVENT_ENTERED_RANGE.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(false), StoppedAtUnixMs: proto.Int64(1)}}
		f.status.ActualPaused, f.status.NativeTickBoundary, f.status.DurableEvents = proto.Bool(true), proto.Bool(true), proto.Bool(true)
		f.status.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum()
	}
	// #886: once the plan's actions are settled the open fight alone owns
	// the combat; it still needs ticks, not a no_work park.
	for _, id := range []domain.PlanID{plan, s.session.State().Snapshot.Plan} {
		state, err := s.player.journal.LoadPlan(ctx, id)
		if err != nil {
			t.Fatal(err)
		}
		for _, action := range state.Spec.Actions() {
			if _, err = s.player.journal.Cancel(ctx, id, action.ID()); err != nil {
				t.Fatal(err)
			}
		}
	}
	stopCombatWindow()
	got, err = s.Step(ctx)
	if err != nil || !got.Decision.Admitted || !got.Combat {
		t.Fatalf("an open fight with no plan work parked the clock %+v %v", got.Decision, err)
	}
}

// #890: the defense planner waits on its own fight's drafts, which stay
// open for the whole fight; a stopped combat window is that fight's next
// decision, so the step releases the wait and runs the planner instead of
// parking it until the fight is over.
func TestClockSchedulerCombatStopReleasesDefenseWait(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	ctx := context.Background()
	f.emergency.Threats = []policy.EmergencyThreat{{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}
	plan := combatGoalPlan(t, s)
	if err := s.player.journal.OpenCombatFight(ctx, plan, policy.CombatMemory{Tactic: policy.TacticSquad}, playerWorld(s.session.State().Snapshot), []domain.PawnID{"pawn"}); err != nil {
		t.Fatal(err)
	}
	if got, err := s.Step(ctx); err != nil || !got.Combat {
		t.Fatal(got, err)
	}
	state, err := s.player.journal.LoadPlan(ctx, plan)
	if err != nil || len(state.Spec.Actions()) == 0 {
		t.Fatal(state, err)
	}
	s.queue.waits[defensePlanner] = plannerWait{On: []domain.ActionID{state.Spec.Actions()[0].ID()}, Deadline: 1 << 40}
	epochs, err := s.player.journal.LoadClockEpochs(ctx, 4096)
	if err != nil || len(epochs) == 0 {
		t.Fatal(epochs, err)
	}
	epoch := proto.Clone(epochs[len(epochs)-1].Epoch).(*k.Epoch)
	f.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: k.StopReason_STOP_REASON_COMBAT_EVENT.Enum(), CombatEvent: k.CombatEvent_COMBAT_EVENT_MELEE_CONTACT.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(false), StoppedAtUnixMs: proto.Int64(1)}}
	f.status.ActualPaused, f.status.NativeTickBoundary, f.status.DurableEvents = proto.Bool(true), proto.Bool(true), proto.Bool(true)
	f.status.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum()
	got, err := s.StepWithReason(ctx, StepReason{Cause: StepWake, TickAdvanced: true})
	if err != nil || !got.Combat || slices.Contains(got.Waiting, defensePlanner) {
		t.Fatal(got.Waiting, got.Decision, err)
	}
	if _, waits := s.queue.waitingOn(defensePlanner); waits {
		t.Fatal("defense still waits on its own fight's drafts at a combat stop")
	}
}
