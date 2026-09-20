package bridge

import (
	"errors"
	"math"
	"testing"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

func TestValidateClockEventsPageProfileHistory(t *testing.T) {
	page, request := clockEventPage(2), clockEventsRequest()
	page.Events[0].Context.Identity.LoadToken = proto.String("old-load")
	page.Events[0].Owner.Epoch = proto.Int64(99)
	page.Events[1].Cursor = proto.Int64(5)
	page.NewestCursor, page.NextCursor = proto.Int64(5), proto.Int64(5)
	page.Gap, page.LostCount = proto.Bool(true), proto.Uint64(3)
	before, query := proto.Clone(page), proto.Clone(request)
	if err := ValidateClockEventsPage(page, request); err != nil {
		t.Fatal(err)
	}
	for _, event := range page.Events {
		if err := ValidateClockEvent(event); err != nil {
			t.Fatal(err)
		}
	}
	if !proto.Equal(page, before) || !proto.Equal(request, query) {
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

func TestValidateClockEventsScannedLoss(t *testing.T) {
	for name, cursors := range map[string][]int64{"tail": {1}, "all lost": {}, "middle": {1, 3}} {
		t.Run(name, func(t *testing.T) {
			p := clockEventPage(0)
			p.NewestCursor = proto.Int64(3)
			p.NextCursor = proto.Int64(3)
			for _, cursor := range cursors {
				p.Events = append(p.Events, clockEventRow(cursor))
			}
			p.Gap = proto.Bool(true)
			p.LostCount = proto.Uint64(uint64(3 - len(cursors)))
			if err := ValidateClockEventsPage(p, clockEventsRequest()); err != nil {
				t.Fatal(err)
			}
		})
	}
	for name, edit := range map[string]func(*k.EventsPage, *k.EventsRequest){
		"unexplained holes": func(p *k.EventsPage, _ *k.EventsRequest) {
			p.NextCursor = proto.Int64(3)
			p.NewestCursor = proto.Int64(3)
		},
		"beyond limit": func(p *k.EventsPage, r *k.EventsRequest) {
			r.Limit = proto.Uint32(1)
			p.NextCursor = proto.Int64(2)
			p.NewestCursor = proto.Int64(2)
			p.Gap = proto.Bool(true)
			p.LostCount = proto.Uint64(1)
		},
		"overflow loss": func(p *k.EventsPage, _ *k.EventsRequest) {
			p.Gap = proto.Bool(true)
			p.LostCount = proto.Uint64(math.MaxUint64)
		},
		"regressed next": func(p *k.EventsPage, r *k.EventsRequest) {
			r.AfterCursor = proto.Int64(1)
			p.NextCursor = proto.Int64(0)
		},
		"event beyond scanned window": func(p *k.EventsPage, _ *k.EventsRequest) {
			p.Events[0].Cursor = proto.Int64(2)
			p.NewestCursor = proto.Int64(2)
		},
	} {
		t.Run(name, func(t *testing.T) {
			p, r := clockEventPage(1), clockEventsRequest()
			edit(p, r)
			if err := ValidateClockEventsPage(p, r); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
	p, r := clockEventPage(0), clockEventsRequest()
	r.AfterCursor = proto.Int64(math.MaxInt64 - 1)
	p.NewestCursor = proto.Int64(math.MaxInt64)
	p.NextCursor = proto.Int64(math.MaxInt64)
	p.Gap = proto.Bool(true)
	p.LostCount = proto.Uint64(1)
	if err := ValidateClockEventsPage(p, r); err != nil {
		t.Fatal(err)
	}
}

func TestValidateClockEventsEmptyNativeRange(t *testing.T) {
	page := clockEventPage(0)
	page.OldestCursor = proto.Int64(0)
	if err := ValidateClockEventsPage(page, clockEventsRequest()); err != nil {
		t.Fatal(err)
	}
	page.OldestCursor = proto.Int64(1)
	if err := ValidateClockEventsPage(page, clockEventsRequest()); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	page = clockEventPage(1)
	page.OldestCursor = proto.Int64(0)
	if err := ValidateClockEventsPage(page, clockEventsRequest()); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}
