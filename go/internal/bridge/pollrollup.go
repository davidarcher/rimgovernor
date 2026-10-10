package bridge

import (
	"sync"
	"time"
)

// pollRollupEvery is how often a quiet poll writes a full native_call row and
// how often the folded remainder is written as one native_poll row. Var so
// tests can set it to zero, which folds nothing.
var pollRollupEvery = 5 * time.Second

// quietPolls are the native reads a loop repeats whether or not anything
// changed: heartbeats and the static recipe list. A successful one keeps its
// own native_call row at most once per pollRollupEvery; the others are counted
// in the native_poll rollup, so `phases` still sees every call and its timing.
var quietPolls = map[string]bool{
	"rimgovernor/lifecycle_read_tick":        true,
	"rimgovernor/lifecycle_read_identity":    true,
	"rimgovernor/clock_read_events":          true,
	"rimgovernor/clock_read_status":          true,
	"rimgovernor/observations_read_recipes":  true,
	"rimgovernor/observations_list_supplies": true,
}

// polledTimingSums are the timing fields a rollup adds up per tool; they are
// the figures SummarizePhases sums from native_call rows.
var polledTimingSums = []string{"gate_wait_ms", "call_ms", "decode_ms", "total_ms", "response_bytes", "proto_decode_ms", "native_queue_ms", "native_execute_ms"}

type polledTool struct {
	calls   uint64
	timed   uint64
	sums    map[string]float64
	wrapper string
}

// pollRollup folds the rows a quiet poll or a frame cache hit would write.
type pollRollup struct {
	mu        sync.Mutex
	sampled   map[string]time.Time
	tools     map[string]*polledTool
	hits      map[string]uint64
	flushedAt time.Time
}

// foldCall reports whether row, a completed call's native_call row, was
// folded into the rollup instead of being written. Failures and the first
// call of each interval are never folded.
func (c *Client) foldCall(row map[string]any) bool {
	native, _ := row["native_tool"].(string)
	if ok, _ := row["ok"].(bool); !ok || !quietPolls[native] {
		return false
	}
	now := time.Now()
	r := &c.rollup
	r.mu.Lock()
	if r.sampled == nil {
		r.sampled = map[string]time.Time{}
	}
	if now.Sub(r.sampled[native]) >= pollRollupEvery {
		r.sampled[native] = now
		r.mu.Unlock()
		return false
	}
	if r.tools == nil {
		r.tools = map[string]*polledTool{}
	}
	t := r.tools[native]
	if t == nil {
		t = &polledTool{sums: map[string]float64{}}
		r.tools[native] = t
	}
	t.calls++
	t.wrapper, _ = row["tool"].(string)
	if timing, ok := row["timing"].(map[string]any); ok {
		for _, k := range polledTimingSums {
			if v, ok := number(timing[k]); ok {
				t.sums[k] += v
			}
		}
		if _, ok := number(timing["native_queue_ms"]); ok {
			t.timed++
		}
	}
	r.mu.Unlock()
	c.flushRollup(now)
	return true
}

// foldHit counts a frame cache hit, which writes no native_frame row.
func (c *Client) foldHit(nativeTool string) {
	r := &c.rollup
	r.mu.Lock()
	if r.hits == nil {
		r.hits = map[string]uint64{}
	}
	r.hits[nativeTool]++
	r.mu.Unlock()
	c.flushRollup(time.Now())
}

// flushRollup writes the folded counts as one native_poll row once
// pollRollupEvery has passed since the last.
func (c *Client) flushRollup(now time.Time) {
	r := &c.rollup
	r.mu.Lock()
	if r.flushedAt.IsZero() {
		r.flushedAt = now
	}
	if now.Sub(r.flushedAt) < pollRollupEvery || (len(r.tools) == 0 && len(r.hits) == 0) {
		r.mu.Unlock()
		return
	}
	tools := map[string]any{}
	for name, t := range r.tools {
		entry := map[string]any{"wrapper": t.wrapper, "calls": t.calls, "native_timed": t.timed}
		for k, v := range t.sums {
			entry[k] = v
		}
		tools[name] = entry
	}
	hits := map[string]any{}
	for name, n := range r.hits {
		hits[name] = n
	}
	window := now.Sub(r.flushedAt)
	r.tools, r.hits, r.flushedAt = nil, nil, now
	r.mu.Unlock()
	if c.recorder != nil {
		c.recorder.Event("native_poll", map[string]any{"component": "bridge"}, false, map[string]any{"window_ms": millis(window), "tools": tools, "frame_hits": hits})
	}
}

// recordNativeCall writes a native_call row unless it folds into the rollup.
func (c *Client) recordNativeCall(recordCtx map[string]any, row map[string]any) {
	if c.foldCall(row) {
		return
	}
	c.recorder.Event("native_call", recordCtx, false, row)
}
