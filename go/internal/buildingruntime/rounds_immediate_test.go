package buildingruntime

import (
	"context"
	"errors"
	"slices"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestImmediateWaitInvalidatesOnlyForRelevantFacts(t *testing.T) {
	q := newPlannerQueue()
	q.configured = func(e plannerEntry) bool { return e.name == defensePlanner }
	q.due[defensePlanner] = 1000
	q.waits[defensePlanner] = plannerWait{On: []domain.ActionID{"issued-protection"}, Deadline: 1000}
	kindOf := func(domain.ActionID) (domain.ActionKind, bool) { return domain.MovementAction, true }
	q.wake(StepReason{Sections: []facts.Section{facts.Research}}, kindOf)
	if got := q.selection(100, false, false, func(domain.ActionID) bool { return true }); got.planners {
		t.Fatal("unrelated research invalidated issued protection")
	}
	q.wake(StepReason{Sections: []facts.Section{facts.Emergency}}, kindOf)
	got := q.selection(100, false, false, func(domain.ActionID) bool { return true })
	if !got.planners || !got.pick(plannerEntry{name: defensePlanner}) {
		t.Fatal("changed threat facts left protection waiting on old work")
	}
}

// The executor boundary records the dispatch while the real Worker owns the
// shared player gate. Native job completion is deliberately still unknown.
type immediateDispatchSession struct {
	*Session
	tended bool
}

func (s *immediateDispatchSession) RunBatch(ctx context.Context, id domain.PlanID, actions []domain.ActionID) ([]executor.BatchItem, error) {
	plan, err := s.journal.LoadPlan(ctx, id)
	if err != nil {
		return nil, err
	}
	current := s.State().Snapshot
	current.Plan, current.Revision = id, plan.Spec.Revision()
	var items []executor.BatchItem
	for _, action := range actions {
		for _, progress := range plan.Progress {
			if progress.View().Action != action || progress.Action().Kind() != domain.TendAction {
				continue
			}
			if _, err := s.journal.Prepare(ctx, id, action, current, 12); err != nil {
				return nil, err
			}
			dispatched, err := s.journal.Dispatch(ctx, id, action, current, 12)
			if err != nil {
				return nil, err
			}
			s.tended = true
			items = append(items, executor.BatchItem{Action: action, Result: executor.Result{Progress: dispatched, NativeCalled: true}})
		}
	}
	return items, nil
}

func TestImmediateTendYieldsToHandsBeforeDevelopment(t *testing.T) {
	s, f := schedulerFixture(t)
	n := schedulerRounds(t, s, f)
	v := n.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(2), proto.Uint32(2)
	rows := []*o.PawnState{tendGateRow("doctor", 0, false, []string{"patient"}), tendGateRow("patient", 0, true, []string{"doctor"})}
	n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: rows, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}
	source := &tendGateNative{&roundsTendNative{n}, n.pawnReply}
	n.pawnReply = proto.Clone(n.pawnReply).(*o.ListPawnsReply)
	for _, row := range n.pawnReply.GetObserved().Pawns {
		row.TendDoctor = nil
	}
	s.config.Rounds.native = source
	var err error
	s.config.Tend, err = NewRoundsTendPlanner(s.config.Rounds, source)
	if err != nil {
		t.Fatal(err)
	}
	s.config.Worker = true
	window := &heldDevelopmentWindow{windowedScheduler: windowedScheduler{f, n}, entered: make(chan struct{}, 1), release: make(chan struct{})}
	s.native = window
	var tend plannerEntry
	for _, entry := range plannerCatalog {
		if entry.name == "tend" {
			tend = entry
		}
	}
	s.catalog = []plannerEntry{tend, quickPlanner("ordinary", classOptional)}
	s.queue.catalog = func() []plannerEntry { return s.catalog }
	s.queue.configured = nil
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	type answer struct {
		result ClockSchedulerResult
		err    error
	}
	done := make(chan answer, 1)
	go func() { result, err := s.Step(ctx); done <- answer{result, err} }()
	select {
	case <-window.entered:
		close(window.release)
		<-done
		t.Fatal("development read preceded Hands")
	case first := <-done:
		if first.err != nil && !errors.Is(first.err, executor.ErrHeld) || !first.result.Immediate || first.result.Tend == nil || first.result.Tend.Plan == "" {
			t.Fatalf("tend not admitted before development: %+v, %v", first.result, first.err)
		}
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	if pending, _, err := s.protectionPending(ctx); err != nil || !pending {
		t.Fatalf("new protective method was not tracked: %v, %v", pending, err)
	}
	watched := slices.Clone(s.protection)
	plans, err := s.player.journal.LoadPlans(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			if slices.Contains(watched, progress.View().Action) {
				if _, err := s.player.journal.Hold(ctx, plan.Spec.ID(), progress.View().Action, []domain.HeldReason{domain.HeldUnknownFacts}, 12); err != nil {
					t.Fatal(err)
				}
			}
		}
	}
	if pending, advance, err := s.protectionPending(ctx); err != nil || pending || advance {
		t.Fatalf("an explicit hold gated ordinary review: pending=%v advance=%v err=%v", pending, advance, err)
	}
	s.protection = watched
	boundary := &immediateDispatchSession{Session: s.session}
	w := &Worker{player: s.player, session: boundary, config: WorkerConfig{StepInterval: time.Millisecond, MaxBackoff: time.Second, StepTimeout: 5 * time.Second, RoundsMethods: true}, waits: make(map[domain.ActionID]workerWait)}
	if err := w.step(ctx, time.Now()); err != nil {
		t.Fatal(err)
	}
	if !boundary.tended {
		t.Fatal("Hands did not dispatch the protective method")
	}
	if pending, advance, err := s.protectionPending(ctx); err != nil || pending || !advance {
		t.Fatalf("dispatch did not yield native time: pending=%v advance=%v err=%v", pending, advance, err)
	}
	if pending, advance, err := s.protectionPending(ctx); err != nil || pending || advance {
		t.Fatalf("dispatch permanently gated ordinary review: pending=%v advance=%v err=%v", pending, advance, err)
	}
	select {
	case <-window.entered:
		t.Fatal("development started before protective dispatch")
	default:
	}
}

func TestNativeStopInterruptsBlockedOrdinaryReview(t *testing.T) {
	s, f := schedulerFixture(t)
	n := schedulerRounds(t, s, f)
	window := &heldDevelopmentWindow{windowedScheduler: windowedScheduler{f, n}, entered: make(chan struct{}, 1), release: make(chan struct{})}
	s.native = window
	s.catalog = []plannerEntry{quickPlanner("ordinary", classOptional)}
	s.queue.catalog = func() []plannerEntry { return s.catalog }
	s.queue.configured = nil
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	finished := make(chan error, 1)
	go func() { _, err := s.Step(ctx); finished <- err }()
	select {
	case <-window.entered:
	case err := <-finished:
		t.Fatalf("review never reached development: %v", err)
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	page := clockPollPage(f, 0, "speed")
	page.Events[0].Event = &k.Event_Stopped{Stopped: &k.StopEvent{
		Reason:   k.StopReason_STOP_REASON_LETTER_PAUSE.Enum(),
		Evidence: &k.StopEvent_Pause{Pause: &k.PauseEvidence{ActualPaused: proto.Bool(true), PauseRequested: proto.Bool(false), PauseVerified: proto.Bool(true), Letter: &k.Letter{Id: proto.String("Letter_1"), Label: proto.String("positive"), DefName: proto.String("PositiveEvent")}}},
	}}
	poll, err := s.PollEvents(ctx, &clockPollNative{core: f.clockCoreFake, page: page}, 128)
	if err != nil || !poll.Captured || !poll.Stopped {
		t.Fatalf("stop not captured: %+v, %v", poll, err)
	}
	select {
	case err := <-finished:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("blocked ordinary review survived native stop: %v", err)
		}
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	if !s.session.State().Enabled {
		t.Fatal("ordinary interruption revoked valid authority")
	}
	// The read remains blocked; cancellation, not an artificial release, must
	// free the shared gate so the next immediate protection can use Hands.
	select {
	case s.player.gate <- struct{}{}:
		<-s.player.gate
	default:
		t.Fatal("ordinary review retained player gate")
	}
}

type heldDevelopmentWindow struct {
	windowedScheduler
	entered chan struct{}
	release chan struct{}
	block   string
}

func (w *heldDevelopmentWindow) hold(ctx context.Context) error {
	select {
	case w.entered <- struct{}{}:
	default:
	}
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-w.release:
		return nil
	}
}

func (w *heldDevelopmentWindow) ReadPlanningWindow(ctx context.Context, id *c.Identity, rect policy.Rectangle) (bridge.PlanningWindow, bridge.Result, error) {
	if w.block == "" || w.block == "planning" {
		if err := w.hold(ctx); err != nil {
			return bridge.PlanningWindow{}, bridge.Result{}, err
		}
	}
	return w.windowedScheduler.ReadPlanningWindow(ctx, id, rect)
}

func (w *heldDevelopmentWindow) ReadBillStacks(ctx context.Context, id *c.Identity) (bridge.EntityRows[*o.BillStack], bridge.Result, error) {
	if w.block == "bills" {
		if err := w.hold(ctx); err != nil {
			return bridge.EntityRows[*o.BillStack]{}, bridge.Result{}, err
		}
	}
	return w.windowedScheduler.ReadBillStacks(ctx, id)
}

func (w *heldDevelopmentWindow) ReadZoneSection(ctx context.Context, id *c.Identity) (bridge.ZonesRead, bridge.Result, error) {
	if w.block == "zones" {
		if err := w.hold(ctx); err != nil {
			return bridge.ZonesRead{}, bridge.Result{}, err
		}
	}
	return w.windowedScheduler.ReadZoneSection(ctx, id)
}

// Firefighting is RimWorld work: its protective execution is the admitted
// native clock window. A development read cannot prevent that dispatch, and
// a continuing fire does not prevent the following ordinary review.
func TestImmediateFireExecutesBeforeBlockedDevelopmentReview(t *testing.T) {
	for _, blocked := range []string{"planning", "bills", "zones"} {
		t.Run(blocked, func(t *testing.T) { immediateFireBeforeDevelopment(t, blocked) })
	}
}

func immediateFireBeforeDevelopment(t *testing.T, blocked string) {
	s, f := schedulerFixture(t)
	n := schedulerRounds(t, s, f)
	fixture, _ := fireSafetyFixture(t, 0.9, true)
	seed := fixture.native.(*roundsFireNative).roundsNative
	n.things, n.buildings = seed.things, seed.buildings
	n.reply.GetObserved().Upkeep = seed.reply.GetObserved().Upkeep
	n.reply.GetObserved().ColonistCount = proto.Uint32(1)
	n.reply.GetObserved().WorkerCount = proto.Uint32(1)
	n.pawnReply = seed.pawnReply
	n.pawnReply.GetObserved().Context = proto.Clone(f.status.Context).(*c.ObservationContext)
	source := &roundsFireNative{n}
	s.config.Rounds.native = source
	var err error
	s.config.FireSafety, err = NewRoundsFireSafetyPlanner(s.config.Rounds, source)
	if err != nil {
		t.Fatal(err)
	}
	window := &heldDevelopmentWindow{windowedScheduler: windowedScheduler{f, n}, entered: make(chan struct{}, 1), release: make(chan struct{}), block: blocked}
	s.native = window
	var fire plannerEntry
	for _, e := range plannerCatalog {
		if e.name == "fireSafety" {
			fire = e
		}
	}
	s.catalog = []plannerEntry{fire, quickPlanner("ordinary", classOptional)}
	s.queue.catalog = func() []plannerEntry { return s.catalog }
	s.queue.configured = func(e plannerEntry) bool { return e.configured(&s.config) }
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	type answer struct {
		result ClockSchedulerResult
		err    error
	}
	finished := make(chan answer, 1)
	go func() { result, err := s.Step(ctx); finished <- answer{result, err} }()
	var first answer
	select {
	case <-window.entered:
		close(window.release)
		<-finished
		t.Fatal("development read began before protective dispatch")
	case first = <-finished:
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	if first.err != nil || !first.result.Immediate || first.result.FireSafety == nil || first.result.FireSafety.NativeWorkTicks == 0 || first.result.Attempt == nil || first.result.Attempt.Phase != store.ClockApplied {
		t.Fatalf("protection cycle: %+v, %v", first.result, first.err)
	}
	if f.status.GetRunning() == nil || f.writes != 1 {
		t.Fatal("native could not advance after protection", f.status, f.writes)
	}
	go func() { result, err := s.Step(ctx); finished <- answer{result, err} }()
	select {
	case <-window.entered:
		// The native clock already runs while ordinary review is held.
		if f.status.GetRunning() == nil {
			t.Error("ordinary review paused native protection")
		}
		close(window.release)
	case next := <-finished:
		t.Fatalf("ordinary review was not scheduled: %+v, %v", next.result, next.err)
	case <-ctx.Done():
		t.Fatal(ctx.Err())
	}
	next := <-finished
	if next.err != nil || next.result.Immediate || next.result.Rounds == nil || next.result.Rounds.Review.Immediate {
		t.Fatalf("ordinary cycle: %+v, %v", next.result, next.err)
	}
}
