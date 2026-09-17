package bridge

import (
	"context"
	"encoding/json"
	"time"
)

// callTiming splits one Client.operation into the phases a flight-recorder
// consumer can attribute: waiting for the concurrency gate, the MCP round
// trip through GABS (which includes native main-thread scheduling and
// execution; the companion does not yet report its own share), decoding the
// raw MCP receipt, and, for typed adapters, ProtoJSON reply decoding. It is
// created by operation, attached to ctx, filled in by core and protoCall,
// and only ever read by the goroutine running that one operation.
type callTiming struct {
	began    time.Time
	gateWait time.Duration
	// request is the flight-recorder sequence of the native_request row core
	// wrote, so a later native_decode row can correlate with it.
	request uint64
}

type callTimingKey struct{}

func withCallTiming(ctx context.Context, timing *callTiming) context.Context {
	return context.WithValue(ctx, callTimingKey{}, timing)
}

func callTimingFrom(ctx context.Context) *callTiming {
	timing, _ := ctx.Value(callTimingKey{}).(*callTiming)
	return timing
}

func millis(d time.Duration) float64 { return float64(d) / float64(time.Millisecond) }

// nativeToolOf names the inner native tool for the GABS wrappers that carry
// one, so phase totals aggregate per rimgovernor/* method rather than under
// games_call_tool. Other core tools report their own name.
func nativeToolOf(name string, arguments json.RawMessage) string {
	if name != "games_call_tool" && name != "games_tool_detail" {
		return name
	}
	var inner struct {
		Tool string `json:"tool"`
	}
	if json.Unmarshal(arguments, &inner) != nil || inner.Tool == "" {
		return name
	}
	return inner.Tool
}
