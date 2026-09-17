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
type ReadTally struct {
	mu     sync.Mutex
	counts map[string]uint64
	total  uint64
	client *Client
}

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
	t.counts[tool]++
	t.total++
	if t.client == nil {
		t.client = client
	}
	t.mu.Unlock()
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
	return b.String()
}

// Publish writes the tally as one "clock_step" flight-recorder row (payload:
// reads, tools, plus the caller's extra fields) on the recorder of the Client
// that served the tallied calls. Without a recorder, or when nothing was
// tallied, it is a no-op; the profiler (SummarizePhases) aggregates the rows.
func (t *ReadTally) Publish(ctx context.Context, extra map[string]any) {
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
	payload := map[string]any{"reads": t.Total(), "tools": tools}
	for key, value := range extra {
		payload[key] = value
	}
	client.recorder.Event("clock_step", client.snapshotRecordingContext(ctx), false, payload)
}
