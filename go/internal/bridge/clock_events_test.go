package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"testing"
	"time"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func clockEventsRequest() *k.EventsRequest {
	return &k.EventsRequest{Identity: pbIdentity(), AfterCursor: proto.Int64(0), Limit: proto.Uint32(128)}
}
func clockEventRow(cursor int64) *k.Event {
	return &k.Event{Cursor: proto.Int64(cursor), Owner: clockTestEpoch().Owner, Context: authorityTestContext(7), ObservedAtUnixMs: proto.Int64(1000 - cursor), Event: &k.Event_SpeedChanged{SpeedChanged: &k.SpeedChanged{Speed: k.Speed_SPEED_NORMAL.Enum()}}}
}
func clockEventPage(count int) *k.EventsPage {
	p := &k.EventsPage{Context: authorityTestContext(7), OldestCursor: proto.Int64(1), NewestCursor: proto.Int64(int64(count)), NextCursor: proto.Int64(int64(count)), Gap: proto.Bool(false), LostCount: proto.Uint64(0)}
	if count == 0 {
		p.OldestCursor = nil
	}
	for i := 1; i <= count; i++ {
		p.Events = append(p.Events, clockEventRow(int64(i)))
	}
	return p
}
func TestClockEventsFixedSDKRead(t *testing.T) {
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		if arg.Tool != "rimgovernor/clock_read_events" {
			t.Fatal(arg.Tool)
		}
		var wrapper struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &wrapper); err != nil {
			t.Fatal(err)
		}
		request := &k.EventsRequest{}
		if err := protojson.Unmarshal([]byte(wrapper.Request), request); err != nil || !proto.Equal(request, clockEventsRequest()) {
			t.Fatal(request, err)
		}
		return pbResult(&k.EventsReply{Outcome: &k.EventsReply_Page{Page: clockEventPage(128)}}), nil
	}}, time.Second)
	r, raw, err := client.ReadClockEvents(context.Background(), clockEventsRequest())
	if err != nil || len(r.GetPage().Events) != 128 || calls != 1 || len(raw.Envelope) == 0 {
		t.Fatal(r, err, calls)
	}
}
func TestClockEventPagesEmptyBoundedAndLoss(t *testing.T) {
	for _, count := range []int{0, 1, 128} {
		if err := clockEventsPage(clockEventPage(count), clockEventsRequest()); err != nil {
			t.Fatal(count, err)
		}
	}
	p := clockEventPage(2)
	p.NewestCursor = proto.Int64(4)
	q := clockEventsRequest()
	q.Limit = proto.Uint32(2)
	if err := clockEventsPage(p, q); err != nil {
		t.Fatal(err)
	}
	// A bounded page is not a completed journal: the untouched newest/next values
	// expose remaining rows. Loss stays explicit without inventing recovery rows.
	if p.GetNextCursor() == p.GetNewestCursor() {
		t.Fatal("truncation erased")
	}
	p = clockEventPage(2)
	p.Events[0].Cursor = proto.Int64(8)
	p.Events[1].Cursor = proto.Int64(10)
	p.OldestCursor = proto.Int64(8)
	p.NewestCursor = proto.Int64(10)
	p.NextCursor = proto.Int64(10)
	p.Gap = proto.Bool(true)
	p.LostCount = proto.Uint64(8)
	if err := clockEventsPage(p, clockEventsRequest()); err != nil {
		t.Fatal(err)
	}
	if p.GetLostCount() != 8 || len(p.Events) != 2 {
		t.Fatal("loss fabricated")
	}
	// Event origin may predate the current world and wall time may regress.
	p.Events[0].Context.Identity.LoadToken = proto.String("previous-load")
	if err := clockEventsPage(p, clockEventsRequest()); err != nil {
		t.Fatal(err)
	}
	q = clockEventsRequest()
	q.AfterCursor = proto.Int64(math.MaxInt64)
	p = clockEventPage(0)
	p.NewestCursor = proto.Int64(math.MaxInt64)
	p.NextCursor = proto.Int64(math.MaxInt64)
	if err := clockEventsPage(p, q); err != nil {
		t.Fatal("cursor overflow", err)
	}
}
func TestClockEventsRejectMalformedPage(t *testing.T) {
	for name, edit := range map[string]func(*k.EventsPage){"wrong world": func(p *k.EventsPage) { p.Context.Identity.LoadToken = proto.String("wrong") }, "missing gap": func(p *k.EventsPage) { p.Gap = nil }, "missing lost": func(p *k.EventsPage) { p.LostCount = nil }, "regressed newest": func(p *k.EventsPage) { p.NewestCursor = proto.Int64(-1) }, "duplicate": func(p *k.EventsPage) { p.Events[1].Cursor = proto.Int64(1) }, "before cursor": func(p *k.EventsPage) { p.Events[0].Cursor = proto.Int64(0) }, "bad next": func(p *k.EventsPage) { p.NextCursor = proto.Int64(1) }, "silent loss": func(p *k.EventsPage) { p.LostCount = proto.Uint64(1) }, "missing variant": func(p *k.EventsPage) { p.Events[0].Event = nil }, "unknown variant": func(p *k.EventsPage) { p.Events[0].ProtoReflect().SetUnknown([]byte{0xa0, 6, 1}) }, "unknown speed": func(p *k.EventsPage) { p.Events[0].GetSpeedChanged().Speed = k.Speed(99).Enum() }, "missing timestamp": func(p *k.EventsPage) { p.Events[0].ObservedAtUnixMs = nil }, "oldest beyond newest": func(p *k.EventsPage) { p.OldestCursor = proto.Int64(3) }} {
		t.Run(name, func(t *testing.T) {
			p := clockEventPage(2)
			edit(p)
			if err := clockEventsPage(p, clockEventsRequest()); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
}
func TestClockEventVariantsAndPartialFacts(t *testing.T) {
	pawn := &k.PawnEvent{PawnId: proto.String("pawn")}
	complete := &c.PageInfo{Complete: proto.Bool(false)}
	stop := &k.StopEvent{Reason: k.StopReason_STOP_REASON_REQUESTED_PAUSE.Enum(), Evidence: &k.StopEvent_Pause{Pause: &k.PauseEvidence{}}}
	rows := []*k.Event{
		{Event: &k.Event_Started{Started: &k.EpochStarted{Epoch: clockTestEpoch()}}},
		{Event: &k.Event_SpeedChanged{SpeedChanged: &k.SpeedChanged{Speed: k.Speed_SPEED_FAST.Enum()}}},
		{Event: &k.Event_Stopped{Stopped: stop}},
		{Event: &k.Event_Notification{Notification: &k.Notification{Source: &k.Notification_Letter{Letter: &k.Letter{Id: proto.String("letter")}}}}},
		{Event: &k.Event_Notification{Notification: &k.Notification{Source: &k.Notification_Message{Message: &k.TransientMessage{Id: proto.String("message")}}}}},
		{Event: &k.Event_Alert{Alert: &k.Alert{Key: proto.String("alert")}}},
		{Event: &k.Event_InjuryObserved{InjuryObserved: &k.Injury{Pawn: pawn, Before: &k.Health{}, After: &k.Health{InjuryCount: proto.Int32(0)}}}},
		{Event: &k.Event_HostilesCleared{HostilesCleared: &k.HostilesCleared{Completeness: complete}}},
		{Event: &k.Event_PauseFailed{PauseFailed: &k.PauseFailed{Pending: stop}}},
		{Event: &k.Event_ForcePauseWaiting{ForcePauseWaiting: &k.ForcePauseWaiting{Pause: &k.PauseEvidence{}}}},
		{Event: &k.Event_ForcePauseCleared{ForcePauseCleared: &k.ForcePauseCleared{}}},
	}
	for i, row := range rows {
		t.Run(string(row.ProtoReflect().Descriptor().FullName().Name())+string(rune('A'+i)), func(t *testing.T) {
			row.Cursor = proto.Int64(1)
			row.Owner = clockTestEpoch().Owner
			row.Context = authorityTestContext(7)
			row.ObservedAtUnixMs = proto.Int64(1)
			p := clockEventPage(1)
			p.Events[0] = row
			if err := clockEventsPage(p, clockEventsRequest()); err != nil {
				t.Fatal(err)
			}
		})
	}
	injury := rows[6].GetInjuryObserved()
	if injury.Before.InjuryCount != nil || injury.After.InjuryCount == nil {
		t.Fatal("unknown and zero conflated")
	}
	for _, e := range []*k.StopEvent{
		{Reason: k.StopReason_STOP_REASON_NOTIFICATION_BATCH.Enum(), Evidence: &k.StopEvent_Notifications{Notifications: &k.NotificationBatch{Completeness: complete}}},
		{Reason: k.StopReason_STOP_REASON_HOSTILE.Enum(), Evidence: &k.StopEvent_Pawn{Pawn: pawn}},
		{Reason: k.StopReason_STOP_REASON_COLONIST_INJURY.Enum(), Evidence: &k.StopEvent_Injury{Injury: injury}},
		{Reason: k.StopReason_STOP_REASON_COLONIST_HEALTH.Enum(), Evidence: &k.StopEvent_Health{Health: &k.HealthThreshold{Pawn: pawn}}},
		{Reason: k.StopReason_STOP_REASON_TICK_BUDGET.Enum(), Evidence: &k.StopEvent_Budget{Budget: &k.BudgetReached{StartTick: proto.Int64(1), TickDeadline: proto.Int64(2), ActualTick: proto.Int64(2)}}},
		{Reason: k.StopReason_STOP_REASON_UNAVAILABLE.Enum(), Evidence: &k.StopEvent_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_LIMIT_EXCEEDED.Enum()}}},
	} {
		if err := clockStopEvent(e); err != nil {
			t.Fatal(err)
		}
	}
	if err := clockStopEvent(&k.StopEvent{Reason: k.StopReason_STOP_REASON_HOSTILE.Enum()}); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}
func TestClockEventsValidationAndTypedRefusal(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		r := pbResult(&k.EventsReply{Outcome: &k.EventsReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_IDENTITY.Enum()}}})
		r.IsError = true
		return r, nil
	}}, time.Second)
	for _, limit := range []uint32{0, 129} {
		q := clockEventsRequest()
		q.Limit = proto.Uint32(limit)
		if _, _, err := client.ReadClockEvents(context.Background(), q); !errors.Is(err, ErrContract) {
			t.Fatal(err)
		}
	}
	q := clockEventsRequest()
	q.AfterCursor = proto.Int64(-1)
	if _, _, err := client.ReadClockEvents(context.Background(), q); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	r, raw, err := client.ReadClockEvents(context.Background(), clockEventsRequest())
	if !errors.Is(err, ErrRefused) || r.GetFailure() == nil || len(raw.Envelope) == 0 {
		t.Fatal(r, err)
	}
}

func TestClockEventsOptionalOldestCursor(t *testing.T) {
	for _, count := range []int{0, 2} {
		page := clockEventPage(count)
		page.OldestCursor = nil
		client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
			return pbResult(&k.EventsReply{Outcome: &k.EventsReply_Page{Page: page}}), nil
		}}, time.Second)
		reply, _, err := client.ReadClockEvents(context.Background(), clockEventsRequest())
		if err != nil || reply.GetPage().OldestCursor != nil || len(reply.GetPage().Events) != count {
			t.Fatal(reply, err)
		}
	}
	// An omitted retention boundary neither proves continuity nor replaces loss
	// evidence. Ordered event gaps remain legal and their reported loss is kept.
	page := clockEventPage(2)
	page.OldestCursor = nil
	page.Events[1].Cursor = proto.Int64(5)
	page.NewestCursor = proto.Int64(5)
	page.NextCursor = proto.Int64(5)
	page.Gap = proto.Bool(true)
	page.LostCount = proto.Uint64(3)
	if err := clockEventsPage(page, clockEventsRequest()); err != nil {
		t.Fatal(err)
	}
	for name, edit := range map[string]func(*k.EventsPage){
		"missing newest":      func(p *k.EventsPage) { p.NewestCursor = nil },
		"missing next":        func(p *k.EventsPage) { p.NextCursor = nil },
		"missing gap":         func(p *k.EventsPage) { p.Gap = nil },
		"missing loss":        func(p *k.EventsPage) { p.LostCount = nil },
		"inconsistent loss":   func(p *k.EventsPage) { p.LostCount = proto.Uint64(0) },
		"unordered":           func(p *k.EventsPage) { p.Events[1].Cursor = proto.Int64(1) },
		"zero oldest":         func(p *k.EventsPage) { p.OldestCursor = proto.Int64(0) },
		"negative oldest":     func(p *k.EventsPage) { p.OldestCursor = proto.Int64(-1) },
		"beyond newest":       func(p *k.EventsPage) { p.OldestCursor = proto.Int64(6) },
		"event before oldest": func(p *k.EventsPage) { p.OldestCursor = proto.Int64(2) },
	} {
		t.Run(name, func(t *testing.T) {
			changed := proto.Clone(page).(*k.EventsPage)
			edit(changed)
			if !errors.Is(clockEventsPage(changed, clockEventsRequest()), ErrContract) {
				t.Fatal("invalid page accepted")
			}
		})
	}
}
