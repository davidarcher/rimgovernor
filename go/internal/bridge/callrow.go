package bridge

import (
	"context"
	"encoding/json"
	"sync"
	"time"
)

// slowCallMarker is how long a native call runs before core writes the
// in-flight native_request marker. A call that finishes sooner writes
// only its completed native_call row; a hung call leaves the marker as the
// last thing in the recording, so crash analysis and `trace` still show "no
// reply recorded". Var so tests can shorten it.
var slowCallMarker = 2 * time.Second

// framesSampleEvery is how often a native_call row carries the frame
// recorder's account (timing.native_frames). The account is cumulative and
// about 2.7 KB of histogram, so one row per interval keeps the first-to-last
// difference the phases report takes without repeating it on every call.
const framesSampleEvery = 5 * time.Second

// sampleFrames reports whether this call's row should carry the frame account.
func (c *Client) sampleFrames() bool {
	now := time.Now().UnixNano()
	last := c.framesSampledAt.Load()
	return now-last >= int64(framesSampleEvery) && c.framesSampledAt.CompareAndSwap(last, now)
}

// recordedArguments is what a row keeps of a call's arguments: for a
// games_call_tool wrapper only the inner request (the game id, encoding and
// class repeat on every row, and the trace and tool are named by the row's
// context and native_tool); any other call's arguments are kept as sent.
func recordedArguments(name string, arguments json.RawMessage) any {
	if name != "games_call_tool" {
		return arguments
	}
	var wrapper struct {
		Arguments struct {
			Request string `json:"request"`
		} `json:"arguments"`
	}
	if json.Unmarshal(arguments, &wrapper) != nil || wrapper.Arguments.Request == "" {
		return arguments
	}
	return map[string]any{"request": wrapper.Arguments.Request}
}

// recordedResult is a reply's structured content without its "timing" block:
// the row's own timing already carries those figures (native_queue_ms,
// native_execute_ms, native_observation), and the block's frame histogram
// alone was most of a small reply's bytes. Readers decode the rest with
// RecordedReply.
func recordedResult(structured json.RawMessage) json.RawMessage {
	var fields map[string]json.RawMessage
	if json.Unmarshal(structured, &fields) != nil {
		return structured
	}
	if _, ok := fields["timing"]; !ok {
		return structured
	}
	delete(fields, "timing")
	out, err := json.Marshal(fields)
	if err != nil {
		return structured
	}
	return out
}

// callMarker writes the native_request row of one call once it has been
// outstanding for slowCallMarker, and tells the completed row which marker
// it answers.
type callMarker struct {
	mu       sync.Mutex
	finished bool
	sequence uint64
	timer    *time.Timer
}

func (c *Client) startCallMarker(recordCtx map[string]any, name, nativeTool string, arguments json.RawMessage) *callMarker {
	m := &callMarker{}
	m.timer = time.AfterFunc(slowCallMarker, func() {
		m.mu.Lock()
		defer m.mu.Unlock()
		if m.finished {
			return
		}
		// Durable: the marker brackets a call that may never return.
		m.sequence, _ = c.recorder.Event("native_request", recordCtx, true, map[string]any{"tool": name, "native_tool": nativeTool, "arguments": recordedArguments(name, arguments)})
	})
	return m
}

// finish stops the timer and returns the marker's sequence, zero when the
// call finished before one was written.
func (m *callMarker) finish() uint64 {
	m.timer.Stop()
	m.mu.Lock()
	defer m.mu.Unlock()
	m.finished = true
	return m.sequence
}

// pendingCall is a completed call's native_call row held back so the typed
// adapter can add its reply decode to the same row (proto_decode_ms,
// payload_bytes, wire_bytes). A call that is not typed writes at once.
type pendingCall struct {
	c         *Client
	recordCtx map[string]any
	row       map[string]any
}

type pendingCallKey struct{}

// deferredCall is the holder protoCall puts on its context; core parks the
// row of the call it ran in it.
type deferredCall struct {
	mu      sync.Mutex
	pending *pendingCall
}

func withDeferredCall(ctx context.Context) (context.Context, *deferredCall) {
	d := &deferredCall{}
	return context.WithValue(ctx, pendingCallKey{}, d), d
}

// record writes row now, or parks it when the context carries a holder. A row
// already parked (a call retried after an attention acknowledgement) is
// written first, since its call is over.
func (c *Client) recordCall(ctx context.Context, recordCtx map[string]any, row map[string]any) {
	d, _ := ctx.Value(pendingCallKey{}).(*deferredCall)
	if d == nil {
		c.recorder.Event("native_call", recordCtx, false, row)
		return
	}
	d.mu.Lock()
	previous := d.pending
	d.pending = &pendingCall{c: c, recordCtx: recordCtx, row: row}
	d.mu.Unlock()
	if previous != nil {
		previous.write(nil)
	}
}

// flush writes the parked row with the typed decode's fields merged into its
// timing. It is a no-op when nothing is parked.
func (d *deferredCall) flush(extra map[string]any) {
	d.mu.Lock()
	pending := d.pending
	d.pending = nil
	d.mu.Unlock()
	if pending != nil {
		pending.write(extra)
	}
}

func (p *pendingCall) write(extra map[string]any) {
	if timing, ok := p.row["timing"].(map[string]any); ok {
		for k, v := range extra {
			timing[k] = v
		}
	}
	p.c.recorder.Event("native_call", p.recordCtx, false, p.row)
}
