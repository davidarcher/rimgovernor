package flightrecorder

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestRetentionAndTruncationAreExplicit(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	r, err := New(path, SegmentBytes(1024), Segments(2), PayloadBytes(128))
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	t.Cleanup(func() { r.Close() })
	for i := 0; i < 20; i++ {
		if _, err = r.Event("sample", nil, true, map[string]any{"value": strings.Repeat("x", 500), "index": i}); err != nil {
			t.Fatalf("Event: %v", err)
		}
	}
	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatalf("ReadTimeline: %v", err)
	}
	if len(rows) == 0 || rows[0].Kind != "recording_gap" {
		t.Fatalf("expected leading recording_gap from retention, got %+v", rows[0])
	}
	foundTruncated := false
	for _, row := range rows {
		if truncated, ok := row.Payload["truncated"]; ok {
			if b, ok := truncated.(bool); ok && b {
				foundTruncated = true
			}
		}
	}
	if !foundTruncated {
		t.Fatal("expected at least one truncated payload")
	}
	dir := filepath.Dir(path)
	files, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("ReadDir: %v", err)
	}
	if len(files) != 2 {
		t.Fatalf("expected 2 retained files, got %d", len(files))
	}
	stats := r.Stats()
	if stats.Truncated != 21 {
		t.Fatalf("expected 21 truncated records (20 events + coverage), got %d", stats.Truncated)
	}
}

func TestTruncatedReceiptKeepsRequestCorrelation(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	r, err := New(path, PayloadBytes(128))
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	t.Cleanup(func() { r.Close() })
	request, err := r.Event("native_request", nil, true, map[string]any{"tool": "games_call_tool", "arguments": map[string]any{}})
	if err != nil {
		t.Fatalf("Event: %v", err)
	}
	if _, err = r.Event("native_response", nil, false, map[string]any{"request": request, "result": map[string]any{"value": strings.Repeat("x", 2000)}}); err != nil {
		t.Fatalf("Event: %v", err)
	}
	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatalf("ReadTimeline: %v", err)
	}
	last := rows[len(rows)-1]
	if truncated, _ := last.Payload["truncated"].(bool); !truncated {
		t.Fatalf("expected truncated payload, got %+v", last.Payload)
	}
	got, ok := last.Payload["request"].(float64)
	if !ok || uint64(got) != request {
		t.Fatalf("expected correlated request %d, got %+v", request, last.Payload["request"])
	}
}

func TestSegmentRotationDropsOldestAndShiftsIndexes(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	r, err := New(path, SegmentBytes(minSegmentBytes), Segments(3), PayloadBytes(minPayloadBytes))
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	for i := 0; i < 60; i++ {
		if _, err = r.Event("sample", nil, false, map[string]any{"index": i, "pad": strings.Repeat("y", 40)}); err != nil {
			t.Fatalf("Event: %v", err)
		}
	}
	if err = r.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}
	dir := filepath.Dir(path)
	files, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("ReadDir: %v", err)
	}
	if len(files) != 3 {
		t.Fatalf("expected 3 retained files (active + 2 segments), got %d: %v", len(files), files)
	}
	stats := r.Stats()
	if stats.Rotations == 0 {
		t.Fatal("expected at least one rotation")
	}
}

func TestDurableRecordsFsyncImmediatelyNonDurableDoNot(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	r, err := New(path)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	t.Cleanup(func() { r.Close() })
	if _, err = r.Event("native_request", nil, true, map[string]any{"tool": "x"}); err != nil {
		t.Fatalf("Event: %v", err)
	}
	if _, err = r.Event("native_response", nil, false, map[string]any{"tool": "x"}); err != nil {
		t.Fatalf("Event: %v", err)
	}
	stats := r.Stats()
	// coverage + native_request are durable; native_response is not.
	if stats.DurableRecords != 2 {
		t.Fatalf("expected 2 durable records, got %d", stats.DurableRecords)
	}
	if stats.Records != 3 {
		t.Fatalf("expected 3 total records, got %d", stats.Records)
	}
}

func TestActionContextRoundTrips(t *testing.T) {
	ctx := WithAction(context.Background(), "action-1", "goal-1")
	got := ActionFrom(ctx)
	if got["action_id"] != "action-1" || got["goal_id"] != "goal-1" {
		t.Fatalf("unexpected action context: %+v", got)
	}
	if ActionFrom(context.Background()) != nil {
		t.Fatal("expected nil action context on bare context")
	}
	unattached := WithAction(context.Background(), "", "goal")
	if ActionFrom(unattached) != nil {
		t.Fatal("expected an empty action id to attach no correlation")
	}
}

func TestRejectsBoundsBelowMinimums(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	if _, err := New(path, SegmentBytes(1)); err == nil {
		t.Fatal("expected error for undersized segment bytes")
	}
	if _, err := New(path, Segments(1)); err == nil {
		t.Fatal("expected error for undersized segments")
	}
	if _, err := New(path, PayloadBytes(1)); err == nil {
		t.Fatal("expected error for undersized payload bytes")
	}
}
