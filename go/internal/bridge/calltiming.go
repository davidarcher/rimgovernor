package bridge

import (
	"context"
	"encoding/json"
	"time"
)

// callTiming splits one Client.operation into the phases a flight-recorder
// consumer can attribute: waiting for the concurrency gate, the MCP round
// trip through GABS, decoding the raw MCP receipt, and, for typed adapters,
// ProtoJSON reply decoding. The round trip's native share (main-thread
// queueing and the tool body) is reported by the companion inside the reply
// wrapper and split out by nativeTiming. It is
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

// nativeTiming reads the companion's own phase report out of a ProtoBoundary
// reply wrapper: {"payload": "...", "timing": {"queueMs": <wait for the
// main thread>, "executeMs": <tool body>}}. Companions that predate the
// field report nothing, so a consumer can tell absent from zero.
func nativeTiming(structured json.RawMessage) (queueMs, executeMs float64, ok bool) {
	if len(structured) == 0 {
		return 0, 0, false
	}
	var wrapper struct {
		Timing *struct {
			QueueMs   *float64 `json:"queueMs"`
			ExecuteMs *float64 `json:"executeMs"`
		} `json:"timing"`
	}
	if json.Unmarshal(structured, &wrapper) != nil || wrapper.Timing == nil || wrapper.Timing.QueueMs == nil || wrapper.Timing.ExecuteMs == nil {
		return 0, 0, false
	}
	if *wrapper.Timing.QueueMs < 0 || *wrapper.Timing.ExecuteMs < 0 {
		return 0, 0, false
	}
	return *wrapper.Timing.QueueMs, *wrapper.Timing.ExecuteMs, true
}

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
