package logview

import (
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

func TestFollowTracksSequencePerRing(t *testing.T) {
	dir := t.TempDir()
	path, explain := filepath.Join(dir, "flight.jsonl"), filepath.Join(dir, "explain.jsonl")
	write(t, path, row{seq: 1, kind: "old"}, row{seq: 2, kind: "old"}, row{seq: 3, kind: "old"})
	write(t, explain, row{seq: 1, kind: "old_explain"})
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	got := make(chan bridge.TimelineRecord, 8)
	done := make(chan error, 1)
	go func() {
		done <- Follow(ctx, path, Filter{}, 10*time.Millisecond, func(r bridge.TimelineRecord) { got <- r })
	}()
	time.Sleep(100 * time.Millisecond)
	// Explain sequence 2 is below flight's last (3): a shared counter would drop it.
	appends := []struct {
		file string
		r    row
	}{{path, row{seq: 4, kind: "new_flight"}}, {explain, row{seq: 2, kind: "new_explain"}}}
	for _, a := range appends {
		f, err := os.OpenFile(a.file, os.O_APPEND|os.O_WRONLY, 0o644)
		if err != nil {
			t.Fatal(err)
		}
		f.WriteString(a.r.line() + "\n")
		f.Close()
	}
	kinds := map[string]bool{}
	for len(kinds) < 2 {
		select {
		case r := <-got:
			if r.Kind == "recording_gap" {
				t.Fatalf("false gap: %+v", r)
			}
			kinds[r.Kind] = true
		case <-ctx.Done():
			t.Fatalf("timed out with %v", kinds)
		}
	}
	cancel()
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	if !kinds["new_flight"] || !kinds["new_explain"] {
		t.Fatalf("got %v", kinds)
	}
}
