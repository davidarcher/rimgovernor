package bridge

import (
	"encoding/json"
	"strings"
	"sync/atomic"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/gabp"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// modLogChannel is the GABP event channel the mod publishes its diagnostics
// on (the C# ModLog helper). Entries are raw GABP event frames; the mod
// replays what it held while no one was subscribed with late=true.
const modLogChannel = "rimgovernor.log"

// modLogQueueDepth bounds the hand-off between the GABP reader goroutine and
// the writer. A burst past it is dropped and counted in one log_overflow row.
const modLogQueueDepth = 1024

// modLogEvent is one rimgovernor.log payload. Type "log" is an entry; type
// "overflow" is the mod's summary of entries its ring dropped.
type modLogEvent struct {
	Type       string `json:"type"`
	Seq        int64  `json:"seq"`
	Level      string `json:"level"`
	Component  string `json:"component"`
	Tick       *int64 `json:"tick"`
	Trace      string `json:"trace"`
	Msg        string `json:"msg"`
	AtUnixMs   int64  `json:"at_unix_ms"`
	Late       bool   `json:"late"`
	Suppressed int64  `json:"suppressed"`
	Dropped    int64  `json:"dropped"`
}

// modLogSink turns rimgovernor.log events into mod_log flight rows. offer
// runs on the GABP reader goroutine and never blocks: it hands the frame to
// a queue a writer goroutine drains in order.
type modLogSink struct {
	recorder *FlightRecorder
	queue    chan json.RawMessage
	dropped  atomic.Int64
}

func newModLogSink(recorder *FlightRecorder) *modLogSink {
	if recorder == nil {
		return nil
	}
	return &modLogSink{recorder: recorder, queue: make(chan json.RawMessage, modLogQueueDepth)}
}

func (s *modLogSink) offer(event gabp.Event) {
	if s == nil || event.Channel != modLogChannel {
		return
	}
	select {
	case s.queue <- append(json.RawMessage(nil), event.Payload...):
	default:
		s.dropped.Add(1)
	}
}

// run writes rows until done closes, then drains what is already queued. The
// summary of frames dropped on a full queue is written once the queue empties:
// the dropped frames are newer than everything queued.
func (s *modLogSink) run(done <-chan struct{}) {
	for {
		select {
		case payload := <-s.queue:
			s.write(payload)
			if len(s.queue) == 0 {
				s.flushDropped()
			}
		case <-done:
			for {
				select {
				case payload := <-s.queue:
					s.write(payload)
				default:
					s.flushDropped()
					return
				}
			}
		}
	}
}

func (s *modLogSink) flushDropped() {
	if n := s.dropped.Swap(0); n > 0 {
		_, _ = s.recorder.Event("log_overflow", map[string]any{"level": "WARN", "component": "mod-log"}, false,
			map[string]any{"dropped": n, "side": "controller"})
	}
}

func (s *modLogSink) write(payload json.RawMessage) {
	var e modLogEvent
	if json.Unmarshal(payload, &e) != nil {
		return
	}
	context := map[string]any{"component": "mod"}
	if e.Tick != nil {
		context["tick"] = *e.Tick
	}
	if e.AtUnixMs > 0 {
		context["at"] = time.UnixMilli(e.AtUnixMs).UTC().Format(time.RFC3339Nano)
	}
	if trace, span, ok := strings.Cut(e.Trace, "/"); ok && trace != "" && span != "" {
		context[telemetry.TraceIDKey], context[telemetry.SpanIDKey] = trace, span
	}
	if e.Type == "overflow" {
		context["level"] = "WARN"
		_, _ = s.recorder.Event("log_overflow", context, false, map[string]any{"seq": e.Seq, "dropped": e.Dropped, "side": "mod"})
		return
	}
	switch strings.ToLower(e.Level) {
	case "error":
		context["level"] = "ERROR"
	case "warn", "warning":
		context["level"] = "WARN"
	}
	row := map[string]any{"seq": e.Seq, "level": strings.ToLower(e.Level), "source": e.Component, "msg": e.Msg}
	if e.Late {
		row["late"] = true
	}
	if e.Suppressed > 0 {
		row["suppressed"] = e.Suppressed
	}
	_, _ = s.recorder.Event("mod_log", context, false, row)
}
