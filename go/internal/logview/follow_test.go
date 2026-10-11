package logview

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

func event(kind, msg string) telemetrytest.Row {
	return telemetrytest.Row{Kind: kind, Context: map[string]any{"component": "t"}, Payload: map[string]any{"msg": msg}}
}

// rename retries while the follower has the file open for a moment (Windows
// refuses to rename an open file).
func rename(from, to string) error {
	var err error
	for range 500 {
		if err = os.Rename(from, to); err == nil {
			return nil
		}
		time.Sleep(time.Millisecond)
	}
	return err
}

func appendLines(t *testing.T, path string, lines ...string) {
	t.Helper()
	f, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	for _, l := range lines {
		if _, err := f.WriteString(l + "\n"); err != nil {
			t.Fatal(err)
		}
	}
}

// Follow prints only rows appended after it started, honours the filter, and
// keeps reading across rotations: the active file renamed to logs.jsonl.1 (and
// again, shifting it to .2) while it was being followed loses no row.
func TestFollowAcrossRotation(t *testing.T) {
	lines := telemetrytest.LogLines(t, event("old", "old"), event("new", "new1"), event("skip", "skip"), event("new", "new2"), event("new", "new3"), event("new", "new4"), event("new", "new5"))
	dir := t.TempDir()
	path := filepath.Join(dir, "logs.jsonl")
	appendLines(t, path, lines[0])

	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	got := make(chan string, 16)
	done := make(chan error, 1)
	go func() {
		done <- Follow(ctx, path, Filter{Kind: "new"}, 5*time.Millisecond, func(r bridge.TimelineRecord) { got <- r.Payload["msg"].(string) })
	}()
	time.Sleep(100 * time.Millisecond) // the backlog is behind the starting point
	want := func(msgs ...string) {
		t.Helper()
		for _, m := range msgs {
			select {
			case g := <-got:
				if g != m {
					t.Fatalf("got %q, want %q", g, m)
				}
			case <-ctx.Done():
				t.Fatalf("timed out waiting for %q", m)
			}
		}
	}
	appendLines(t, path, lines[1], lines[2])
	want("new1")

	// Rotation: the active file becomes .1, a fresh one carries on.
	if err := rename(path, path+".1"); err != nil {
		t.Fatal(err)
	}
	appendLines(t, path, lines[3])
	want("new2")

	// Two rotations between polls, with an unread row in the first.
	appendLines(t, path, lines[4])
	if err := rename(path+".1", path+".2"); err != nil {
		t.Fatal(err)
	}
	if err := rename(path, path+".1"); err != nil {
		t.Fatal(err)
	}
	appendLines(t, path, lines[5])
	want("new3", "new4")

	// A line still being written is not emitted until its newline arrives.
	half := lines[6][:len(lines[6])/2]
	f, err := os.OpenFile(path, os.O_APPEND|os.O_WRONLY, 0o644)
	if err != nil {
		t.Fatal(err)
	}
	f.WriteString(half)
	time.Sleep(50 * time.Millisecond)
	select {
	case g := <-got:
		t.Fatalf("emitted a torn line: %q", g)
	default:
	}
	f.WriteString(strings.TrimPrefix(lines[6], half) + "\n")
	f.Close()
	want("new5")

	cancel()
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}
