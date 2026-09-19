package nativeaccept

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestParseBreakpoint(t *testing.T) {
	for spec, want := range map[string]Breakpoint{
		"stage=roofed": {Stage: "roofed"},
		"tick=60000":   {Tick: 60000},
		"minute=7":     {Minute: 7 * time.Minute},
		"minute=1.5":   {Minute: 90 * time.Second},
		"minute=1m30s": {Minute: 90 * time.Second},
	} {
		got, err := ParseBreakpoint(spec)
		if err != nil || got != want {
			t.Errorf("%s: %+v %v, want %+v", spec, got, err, want)
		}
		if _, err := ParseBreakpoint(got.String()); err != nil {
			t.Errorf("%s: String %q does not parse: %v", spec, got.String(), err)
		}
	}
	for _, spec := range []string{"", "roofed", "stage=", "tick=0", "tick=x", "minute=0", "minute=-1", "hour=1"} {
		if b, err := ParseBreakpoint(spec); err == nil {
			t.Errorf("%q parsed as %+v", spec, b)
		}
	}
	if (Breakpoint{Minute: 7 * time.Minute}).String() != "minute=7" || (Breakpoint{}).String() != "" || !(Breakpoint{}).IsZero() {
		t.Fatal("String/IsZero")
	}
}

// A minute breakpoint trips at the first natural pause once the run-phase
// offset (Base included) reaches it; OnBreak runs once, and Broke reads
// the cause back off the cancelled context.
func TestBreakpointTripsOnMinute(t *testing.T) {
	ring, _ := ringFixture(t, time.Hour)
	ring.Break = Breakpoint{Minute: 7 * time.Minute}
	ctx, cut := context.WithCancelCause(context.Background())
	fired := 0
	ring.OnBreak = func(reason string) { fired++; cut(&BreakError{Reason: reason}) }
	ring.Activate()
	defer ring.Deactivate()
	ring.Base = 6 * time.Minute
	checkpointPause(ctx)
	if ring.Tripped() != "" || ctx.Err() != nil {
		t.Fatalf("tripped early: %q", ring.Tripped())
	}
	ring.Base = 7 * time.Minute
	checkpointPause(ctx)
	checkpointPause(ctx)
	if fired != 1 || ring.Tripped() == "" {
		t.Fatalf("fired %d, tripped %q", fired, ring.Tripped())
	}
	broke := Broke(ctx)
	if broke == nil || broke.Reason != ring.Tripped() || !errors.Is(context.Cause(ctx), broke) {
		t.Fatalf("cause %v, broke %+v", context.Cause(ctx), broke)
	}
	if Broke(context.Background()) != nil {
		t.Fatal("an uncut context broke")
	}
}

// A tick breakpoint under the bridge reads the tick the replies carried.
func TestBreakpointTripsOnObservedTick(t *testing.T) {
	ResetTickStats()
	t.Cleanup(ResetTickStats)
	ring, _ := ringFixture(t, time.Hour)
	ring.Break = Breakpoint{Tick: 5000}
	var reason string
	ring.OnBreak = func(r string) { reason = r }
	ring.Activate()
	defer ring.Deactivate()
	ctx := context.Background()
	checkpointPause(ctx)
	if reason != "" {
		t.Fatalf("tripped with no tick observed: %s", reason)
	}
	observeTick(4999)
	ring.nextBreakTick = time.Time{}
	checkpointPause(ctx)
	if reason != "" {
		t.Fatalf("tripped below the mark: %s", reason)
	}
	observeTick(5000)
	ring.nextBreakTick = time.Time{}
	checkpointPause(ctx)
	if reason != "game tick 5000 reached 5000" {
		t.Fatalf("reason %q", reason)
	}
}

// The break bundle is taken through a forced capture, capped ring or not,
// and is not a ring entry (Entries) itself.
func TestBreakBundleIgnoresCap(t *testing.T) {
	ring, _ := ringFixture(t, time.Hour)
	ring.Activate()
	defer ring.Deactivate()
	CapCheckpoints("point of no return")
	entry := ring.BreakBundle(context.Background())
	if entry == nil {
		t.Fatal(ring.Errors())
	}
	if entry.Label != BreakCheckpoint || entry.Path != filepath.Join(ring.Dir, BreakCheckpoint) || entry.Tick != 4242 {
		t.Fatalf("%+v", entry)
	}
	if _, err := os.Stat(filepath.Join(entry.Path, CheckpointSidecar)); err != nil {
		t.Fatal(err)
	}
	if len(ring.Entries()) != 0 {
		t.Fatalf("entries %v", ring.Entries())
	}
}
