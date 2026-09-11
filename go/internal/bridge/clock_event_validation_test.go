package bridge

import (
	"errors"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func TestValidateClockEventsPageProfileHistory(t *testing.T) {
	page, request := clockEventPage(2), clockEventsRequest()
	page.Events[0].Context.Identity.LoadToken = proto.String("old-load")
	page.Events[0].Owner.Epoch = proto.Int64(99)
	page.Events[1].Cursor = proto.Int64(5)
	page.NewestCursor, page.NextCursor = proto.Int64(5), proto.Int64(5)
	before, query := proto.Clone(page), proto.Clone(request)
	if err := ValidateClockEventsPage(page, request); err != nil {
		t.Fatal(err)
	}
	for _, event := range page.Events {
		if err := ValidateClockEvent(event); err != nil {
			t.Fatal(err)
		}
	}
	if !proto.Equal(page, before) || !proto.Equal(request, query) || page.GetGap() {
		t.Fatal("validation changed evidence")
	}
}

func TestValidateClockEventsPageRejectsInvalidRequest(t *testing.T) {
	for name, edit := range map[string]func(*k.EventsRequest){
		"missing identity": func(r *k.EventsRequest) { r.Identity = nil },
		"missing cursor":   func(r *k.EventsRequest) { r.AfterCursor = nil },
		"negative cursor":  func(r *k.EventsRequest) { r.AfterCursor = proto.Int64(-1) },
		"missing limit":    func(r *k.EventsRequest) { r.Limit = nil },
		"zero limit":       func(r *k.EventsRequest) { r.Limit = proto.Uint32(0) },
		"large limit":      func(r *k.EventsRequest) { r.Limit = proto.Uint32(129) },
		"unknown":          func(r *k.EventsRequest) { r.ProtoReflect().SetUnknown([]byte{0xa0, 6, 1}) },
	} {
		t.Run(name, func(t *testing.T) {
			r := clockEventsRequest()
			edit(r)
			if err := ValidateClockEventsPage(clockEventPage(0), r); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
	if err := ValidateClockEventsPage(clockEventPage(0), nil); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	if err := ValidateClockEventsPage(nil, clockEventsRequest()); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	for name, edit := range map[string]func(*k.EventsPage){
		"wrong page world":  func(p *k.EventsPage) { p.Context.Identity.LoadToken = proto.String("wrong") },
		"duplicate cursor":  func(p *k.EventsPage) { p.Events[1].Cursor = proto.Int64(1) },
		"cursor regression": func(p *k.EventsPage) { p.NewestCursor = proto.Int64(-1) },
		"missing loss":      func(p *k.EventsPage) { p.LostCount = nil },
	} {
		t.Run(name, func(t *testing.T) {
			p := clockEventPage(2)
			edit(p)
			if err := ValidateClockEventsPage(p, clockEventsRequest()); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
}

func TestValidateClockEventRejectsMalformedEvidence(t *testing.T) {
	if err := ValidateClockEvent(nil); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	for name, edit := range map[string]func(*k.Event){
		"missing variant":      func(e *k.Event) { e.Event = nil },
		"unknown field":        func(e *k.Event) { e.ProtoReflect().SetUnknown([]byte{0xa0, 6, 1}) },
		"unknown nested field": func(e *k.Event) { e.GetSpeedChanged().ProtoReflect().SetUnknown([]byte{0xa0, 6, 1}) },
		"unsupported enum":     func(e *k.Event) { e.GetSpeedChanged().Speed = k.Speed(99).Enum() },
		"nonfinite": func(e *k.Event) {
			e.Event = &k.Event_InjuryObserved{InjuryObserved: &k.Injury{Pawn: &k.PawnEvent{PawnId: proto.String("p")}, After: &k.Health{Severity: proto.Float32(float32(math.NaN()))}}}
		},
		"repeated window": func(e *k.Event) {
			e.Event = &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_REQUESTED_PAUSE.Enum(), Evidence: &k.StopEvent_Pause{Pause: &k.PauseEvidence{ForcePausingWindowIds: []string{"w", "w"}}}}}
		},
	} {
		t.Run(name, func(t *testing.T) {
			e := clockEventRow(1)
			edit(e)
			if err := ValidateClockEvent(e); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
}
