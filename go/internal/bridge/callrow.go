package bridge

import (
	"context"
	"encoding/json"
	"sync"
	"time"
)

// slowCallMarker is how long a native call runs before core writes the
// in-flight native_request marker (#2057). A call that finishes sooner writes
// only its completed native_call row; a hung call leaves the marker as the
// last thing in the recording, so crash analysis and `trace` still show "no
// reply recorded". Var so tests can shorten it.
var slowCallMarker = 2 * time.Second

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
		m.sequence, _ = c.recorder.Event("native_request", recordCtx, true, map[string]any{"tool": name, "native_tool": nativeTool, "arguments": arguments})
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
