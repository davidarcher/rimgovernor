package bridge

import (
	"context"
	"fmt"
	"sort"
	"strings"
	"sync"
)

// ReadTally counts the native round trips issued under one context: every
// core call the Client makes with a context derived from WithReadTally is
// tallied by native tool (the inner rimgovernor/* method for games_call_tool,
// the GABS tool name otherwise), errors included. A ClockScheduler step
// attaches one at entry so the reads a full planner composition costs are
// visible per step (RIMGOVERNOR_CLOCK_DEBUG=1) and, through Publish, in the
// flight recorder the throughput profiler summarizes. It is safe for
// concurrent use; planners run in parallel under the same step context.
//
// The one-off schema fetches (games_tool_detail) the Client issues before
// a tool's first call are counted apart, as Schema: they are a session's
// startup cost, not a step's reads, and would otherwise make the first
// full step look several reads heavier than any other (issue #180).
type ReadTally struct {
	mu     sync.Mutex
	counts map[string]uint64
	total  uint64
	schema uint64
	client *Client
}

// schemaTool is the GABS tool the Client's ensureDescribed fetches a
// native tool's schema with.
const schemaTool = "games_tool_detail"

type readTallyKey struct{}

// WithReadTally returns ctx with a fresh tally attached. A tally already on
// ctx is replaced, never nested: each step reports its own reads.
func WithReadTally(ctx context.Context) (context.Context, *ReadTally) {
	tally := &ReadTally{counts: map[string]uint64{}}
	return context.WithValue(ctx, readTallyKey{}, tally), tally
}

func readTallyFrom(ctx context.Context) *ReadTally {
	tally, _ := ctx.Value(readTallyKey{}).(*ReadTally)
	return tally
}

func (t *ReadTally) add(client *Client, tool string) {
	if t == nil {
		return
	}
	t.mu.Lock()
	if tool == schemaTool {
		t.schema++
	} else {
		t.counts[tool]++
		t.total++
	}
	if t.client == nil {
		t.client = client
	}
	t.mu.Unlock()
}

// Schema is the number of schema fetches tallied so far, apart from Total.
func (t *ReadTally) Schema() uint64 {
	if t == nil {
		return 0
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.schema
}

// Total is the number of round trips tallied so far.
func (t *ReadTally) Total() uint64 {
	if t == nil {
		return 0
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.total
}

// Counts is a copy of the per-tool tally.
func (t *ReadTally) Counts() map[string]uint64 {
	out := map[string]uint64{}
	if t == nil {
		return out
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	for tool, n := range t.counts {
		out[tool] = n
	}
	return out
}

// String renders the tally as "total=N tool=n ..." with tools in descending
// count order, the one-line form the clock debug log prints per step.
func (t *ReadTally) String() string {
	counts := t.Counts()
	names := make([]string, 0, len(counts))
	for tool := range counts {
		names = append(names, tool)
	}
	sort.Slice(names, func(i, j int) bool {
		if counts[names[i]] != counts[names[j]] {
			return counts[names[i]] > counts[names[j]]
		}
		return names[i] < names[j]
	})
	var b strings.Builder
	fmt.Fprintf(&b, "total=%d", t.Total())
	for _, tool := range names {
		fmt.Fprintf(&b, " %s=%d", strings.TrimPrefix(tool, "rimgovernor/"), counts[tool])
	}
	if schema := t.Schema(); schema > 0 {
		fmt.Fprintf(&b, " schema=%d", schema)
	}
	return b.String()
}

// Publish writes the tally as one "clock_step" flight-recorder row (payload:
// reads, tools, schema_fetches, plus the caller's extra fields such as the
// step cache's cache_hits and parent_hits) on the recorder of the Client
// that served the tallied calls. Without a recorder, or when nothing was
// tallied, it is a no-op; the profiler (SummarizePhases) aggregates the rows.
func (t *ReadTally) Publish(ctx context.Context, extra map[string]any) {
	t.PublishAs(ctx, "clock_step", extra)
}

// PublishAs is Publish under another row kind: "worker_dispatch" for the
// Worker's per-dispatch row (#243).
func (t *ReadTally) PublishAs(ctx context.Context, kind string, extra map[string]any) {
	if t == nil {
		return
	}
	t.mu.Lock()
	client := t.client
	t.mu.Unlock()
	if client == nil || client.recorder == nil {
		return
	}
	tools := map[string]any{}
	for tool, n := range t.Counts() {
		tools[tool] = n
	}
	payload := map[string]any{"reads": t.Total(), "tools": tools, "schema_fetches": t.Schema()}
	for key, value := range extra {
		payload[key] = value
	}
	client.recorder.Event(kind, client.snapshotRecordingContext(ctx), false, payload)
}
