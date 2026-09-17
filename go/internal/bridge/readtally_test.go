package bridge

import (
	"bytes"
	"context"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// TestReadTallyCountsPerStepAndSummarizes: calls made under a tallied context
// are counted by native tool (describe round trips under their GABS wrapper),
// Publish leaves one clock_step row per step, and the profiler reports
// reads per step from those rows.
func TestReadTallyCountsPerStepAndSummarizes(t *testing.T) {
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(pbLoaded()), nil
	}}
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	rec, err := NewFlightRecorder(path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { rec.Close() })
	client, err := open(context.Background(), "fixture-game", time.Second, rec, s.factory(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Close() })

	// An untallied call is never counted anywhere.
	if _, _, err = client.Identity(context.Background()); err != nil {
		t.Fatal(err)
	}
	for step, calls := range []int{3, 1} {
		ctx, tally := WithReadTally(context.Background())
		for i := 0; i < calls; i++ {
			if _, _, err = client.Identity(ctx); err != nil {
				t.Fatal(err)
			}
		}
		if _, err = client.GameStatus(ctx); err != nil {
			t.Fatal(err)
		}
		counts := tally.Counts()
		if tally.Total() != uint64(calls+1) || counts["rimgovernor/lifecycle_read_identity"] != uint64(calls) || counts["games_status"] != 1 || counts["games_tool_detail"] != 0 {
			t.Fatalf("step %d tally: total %d counts %v", step, tally.Total(), counts)
		}
		if step == 0 && tally.String() != "total=4 lifecycle_read_identity=3 games_status=1" {
			t.Fatalf("tally text: %q", tally.String())
		}
		tally.Publish(ctx, map[string]any{"running": false, "cache_hits": 2, "parent_hits": 3})
	}
	var empty ReadTally
	empty.Publish(context.Background(), nil) // nothing tallied: no row, no panic
	if err = rec.Close(); err != nil {
		t.Fatal(err)
	}
	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	summary := SummarizePhases(rows)
	steps := summary.Steps
	if steps.Steps != 2 || steps.Reads != 6 || steps.MaxReads != 4 || steps.CacheHits != 4 || steps.ParentHits != 6 || steps.Tools["rimgovernor/lifecycle_read_identity"] != 4 || steps.Tools["games_status"] != 2 {
		t.Fatalf("step summary: %+v", steps)
	}
	var report bytes.Buffer
	WritePhaseReport(&report, summary)
	if text := report.String(); !strings.Contains(text, "steps: 2, reads/step mean 3.0 max 4, cache hits/step 2.0, parent hits/step 3.0") || !strings.Contains(text, "rimgovernor/lifecycle_read_identity") {
		t.Fatalf("report lacks step reads:\n%s", text)
	}
}
