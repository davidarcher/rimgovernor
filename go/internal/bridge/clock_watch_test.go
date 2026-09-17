package bridge

import (
	"errors"
	"testing"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func clockTestAttempt(n uint64) *c.AttemptKey {
	return &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("action"), AttemptId: proto.Uint64(n)}
}
func clockTestOutcome() *k.OperationOutcome {
	return &k.OperationOutcome{Attempt: clockTestAttempt(1), LatchedTick: proto.Int64(40), Outcome: &k.OperationOutcome_Completed{Completed: &r.CompletedEffect{Evidence: &r.EffectEvidence{Effect: &r.EffectEvidence_Construction{Construction: &r.ConstructionEffect{}}}}}}
}
func clockWatchPage(row *k.Event) *k.EventsPage {
	row.Cursor = proto.Int64(1)
	row.Context = authorityTestContext(7)
	row.ObservedAtUnixMs = proto.Int64(1)
	p := clockEventPage(1)
	p.Events[0] = row
	return p
}

// Operation outcomes and authority changes are typed facts; an authority
// change is the only event that may arrive without an epoch owner.
func TestClockWatchEventsAndOwnerlessAuthority(t *testing.T) {
	owned := clockTestEpoch().Owner
	for name, row := range map[string]*k.Event{
		"outcome":           {Owner: owned, Event: &k.Event_OperationOutcome{OperationOutcome: clockTestOutcome()}},
		"unsuccessful":      {Owner: owned, Event: &k.Event_OperationOutcome{OperationOutcome: &k.OperationOutcome{Attempt: clockTestAttempt(1), LatchedTick: proto.Int64(1), Outcome: &k.OperationOutcome_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_CANCELLED.Enum()}}}}},
		"absent":            {Owner: owned, Event: &k.Event_OperationOutcome{OperationOutcome: &k.OperationOutcome{Attempt: clockTestAttempt(1), LatchedTick: proto.Int64(1), Outcome: &k.OperationOutcome_Absent{Absent: &r.AbsentEffect{InspectionToken: proto.String("token")}}}}},
		"unknown":           {Owner: owned, Event: &k.Event_OperationOutcome{OperationOutcome: &k.OperationOutcome{Attempt: clockTestAttempt(1), LatchedTick: proto.Int64(1), Outcome: &k.OperationOutcome_Unknown{Unknown: &r.UnknownEffect{}}}}},
		"authority owned":   {Owner: owned, Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{Generation: proto.Uint64(3), PreviousGeneration: proto.Uint64(2), Reason: proto.String("Manual")}}},
		"authority unowned": {Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{Generation: proto.Uint64(3)}}},
		"watch stop":        {Owner: owned, Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_WATCH_LATCHED.Enum(), Evidence: &k.StopEvent_Watch{Watch: &k.WatchLatched{Outcome: clockTestOutcome(), TickDeadline: proto.Int64(612)}}}}},
	} {
		if err := clockEventsPage(clockWatchPage(row), clockEventsRequest()); err != nil {
			t.Fatal(name, err)
		}
	}
	for name, row := range map[string]*k.Event{
		"ownerless outcome":  {Event: &k.Event_OperationOutcome{OperationOutcome: clockTestOutcome()}},
		"ownerless stop":     {Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_TICK_BUDGET.Enum(), Evidence: &k.StopEvent_Pause{Pause: &k.PauseEvidence{}}}}},
		"zero generation":    {Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{Generation: proto.Uint64(0)}}},
		"previous not older": {Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{Generation: proto.Uint64(2), PreviousGeneration: proto.Uint64(2)}}},
		"no outcome":         {Owner: owned, Event: &k.Event_OperationOutcome{OperationOutcome: &k.OperationOutcome{Attempt: clockTestAttempt(1), LatchedTick: proto.Int64(1)}}},
		"no attempt":         {Owner: owned, Event: &k.Event_OperationOutcome{OperationOutcome: &k.OperationOutcome{LatchedTick: proto.Int64(1), Outcome: &k.OperationOutcome_Unknown{Unknown: &r.UnknownEffect{}}}}},
		"no tick":            {Owner: owned, Event: &k.Event_OperationOutcome{OperationOutcome: &k.OperationOutcome{Attempt: clockTestAttempt(1), Outcome: &k.OperationOutcome_Unknown{Unknown: &r.UnknownEffect{}}}}},
		"empty completion":   {Owner: owned, Event: &k.Event_OperationOutcome{OperationOutcome: &k.OperationOutcome{Attempt: clockTestAttempt(1), LatchedTick: proto.Int64(1), Outcome: &k.OperationOutcome_Completed{Completed: &r.CompletedEffect{}}}}},
		"watch stop bare":    {Owner: owned, Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_WATCH_LATCHED.Enum(), Evidence: &k.StopEvent_Watch{Watch: &k.WatchLatched{TickDeadline: proto.Int64(612)}}}}},
		"watch no deadline":  {Owner: owned, Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_WATCH_LATCHED.Enum(), Evidence: &k.StopEvent_Watch{Watch: &k.WatchLatched{Outcome: clockTestOutcome()}}}}},
	} {
		if err := clockEventsPage(clockWatchPage(row), clockEventsRequest()); !errors.Is(err, ErrContract) {
			t.Fatal(name, err)
		}
	}
}

func TestClockPolicyWatchedAttemptsBounded(t *testing.T) {
	policy := clockTestPolicy()
	for i := uint64(1); i <= ClockWatchedAttemptsMax; i++ {
		policy.WatchedAttempts = append(policy.WatchedAttempts, clockTestAttempt(i))
	}
	if err := clockPolicy(policy, 600); err != nil {
		t.Fatal(err)
	}
	over := proto.Clone(policy).(*k.WatchPolicy)
	over.WatchedAttempts = append(over.WatchedAttempts, clockTestAttempt(ClockWatchedAttemptsMax+1))
	duplicate := proto.Clone(policy).(*k.WatchPolicy)
	duplicate.WatchedAttempts[1] = clockTestAttempt(1)
	incomplete := proto.Clone(policy).(*k.WatchPolicy)
	incomplete.WatchedAttempts[0].AttemptId = nil
	for name, p := range map[string]*k.WatchPolicy{"over": over, "duplicate": duplicate, "incomplete": incomplete} {
		if err := clockPolicy(p, 600); !errors.Is(err, ErrContract) {
			t.Fatal(name, err)
		}
	}
}

func TestClockEventsWaitBound(t *testing.T) {
	request := clockEventsRequest()
	request.WaitMs = proto.Uint32(ClockEventsMaxWaitMs + 1)
	if _, _, err := (&Client{}).ReadClockEvents(t.Context(), request); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}
