package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func clockPollOutcome(attempt uint64) *k.OperationOutcome {
	key := &c.AttemptKey{ControllerSessionId: proto.String("session"), ActionId: proto.String("wall"), AttemptId: proto.Uint64(attempt)}
	return &k.OperationOutcome{Attempt: key, LatchedTick: proto.Int64(40), Outcome: &k.OperationOutcome_Completed{Completed: &r.CompletedEffect{Evidence: &r.EffectEvidence{Effect: &r.EffectEvidence_Construction{Construction: &r.ConstructionEffect{}}}}}}
}

// A page carrying a latched outcome and its WATCH_LATCHED stop is benign:
// nothing is held, authority stays enabled, and the committed evidence is
// summarized as a wake. An owner-less authority change is accepted too.
func TestClockPollWatchLatchedIsBenignAndWakes(t *testing.T) {
	t.Parallel()
	s, f, _ := clockPollFixture(t)
	if _, err := s.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	page := clockPollPage(f, 0, "benign")
	owner := page.Events[0].Owner
	context1 := page.Events[0].Context
	page.Events = []*k.Event{
		{Cursor: proto.Int64(1), Owner: owner, Context: context1, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_OperationOutcome{OperationOutcome: clockPollOutcome(3)}},
		{Cursor: proto.Int64(2), Owner: owner, Context: context1, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_WATCH_LATCHED.Enum(), Evidence: &k.StopEvent_Watch{Watch: &k.WatchLatched{Outcome: clockPollOutcome(3), TickDeadline: proto.Int64(612)}}}}},
		{Cursor: proto.Int64(3), Context: context1, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{Generation: proto.Uint64(9), Reason: proto.String("acquired")}}},
	}
	page.NextCursor, page.NewestCursor = proto.Int64(3), proto.Int64(3)
	native := &clockPollNative{page: page}
	result, err := s.PollEvents(context.Background(), native, 128, 1500*time.Millisecond)
	if err != nil || result.Interrupted || !result.Captured || len(result.Review.Holds) != 0 || result.Review.ReviewedCursor != 3 || !s.session.State().Enabled {
		t.Fatal(result, err, s.session.State())
	}
	if native.request.GetWaitMs() != 1500 {
		t.Fatal("wait not forwarded", native.request)
	}
	if len(result.Wake) != 2 || result.Wake[0] != (WakeOutcome{Action: domain.ActionID("wall"), Attempt: 3, Terminal: true}) || !result.AuthorityChanged {
		t.Fatal(result.Wake, result.AuthorityChanged)
	}
	// An empty page commits nothing and therefore wakes nothing.
	result, err = s.PollEvents(context.Background(), &clockPollNative{page: clockPollPage(f, 3, "empty")}, 128, 0)
	if err != nil || result.Captured || len(result.Wake) != 0 || result.AuthorityChanged {
		t.Fatal(result, err)
	}
	if _, err = s.PollEvents(context.Background(), native, 128, 6*time.Second); err == nil {
		t.Fatal("wait above the bound must be refused")
	}
}

func TestClockSchedulerWatchesConstructionOnly(t *testing.T) {
	t.Parallel()
	items := []clockWorkItem{
		{Action: "allow", Kind: domain.SupplyAllowAction, Stage: domain.Dispatched, Attempt: 1},
		{Action: "haul", Kind: domain.HaulAction, Stage: domain.Dispatched, Attempt: 1},
		{Action: "planned", Kind: domain.BuildingAction, Stage: domain.Prepared, Attempt: 1},
		{Action: "done", Kind: domain.BuildingAction, Stage: domain.Completed, Attempt: 1},
		{Action: "wall", Kind: domain.BuildingAction, Stage: domain.Dispatched, Attempt: 2},
		{Action: "door", Kind: domain.BuildingAction, Stage: domain.AwaitingObservation, Attempt: 1},
	}
	watched := clockSchedulerWatches(items, "session")
	if len(watched) != 2 || watched[0].GetActionId() != "wall" || watched[0].GetAttemptId() != 2 || watched[1].GetActionId() != "door" || watched[0].GetControllerSessionId() != "session" {
		t.Fatal(watched)
	}
	var many []clockWorkItem
	for i := 0; i < 40; i++ {
		many = append(many, clockWorkItem{Action: domain.ActionID(string(rune('a' + i))), Kind: domain.BuildingAction, Stage: domain.Dispatched, Attempt: 1})
	}
	if got := clockSchedulerWatches(many, "session"); len(got) != 16 || got[0].GetActionId() != "a" {
		t.Fatal(len(got))
	}
}
