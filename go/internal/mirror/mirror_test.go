package mirror

import (
	"context"
	"errors"
	"fmt"
	"math/rand"
	"testing"
)

// window is the native tombstone window (EntityTracking.TombstoneWindow,
// one game day, #795).
const window = 60000

// world is a native section with change tracking: rows stamped with the
// tick they last changed, tombstones kept for window ticks; an older ask
// is answered in full inline.
type world struct {
	tick    int64
	rows    map[string]string
	changed map[string]int64
	removed map[string]int64
	reads   []int64
	// miss drops the next change from tracking (a native bug the resync
	// must surface as drift).
	miss bool
}

func newWorld() *world {
	return &world{rows: map[string]string{}, changed: map[string]int64{}, removed: map[string]int64{}}
}

func (w *world) set(id, value string) {
	w.rows[id] = value
	delete(w.removed, id)
	if w.miss {
		w.miss = false
		return
	}
	w.changed[id] = w.tick
}

func (w *world) drop(id string) {
	delete(w.rows, id)
	delete(w.changed, id)
	w.removed[id] = w.tick
}

func (w *world) Name() string           { return "things" }
func (w *world) Equal(a, b string) bool { return a == b }
func (w *world) Read(_ context.Context, mark Watermark) (Read[string, string], error) {
	since := mark.Tick
	w.reads = append(w.reads, since)
	if since > 0 && w.tick-since > window {
		since = 0
	}
	out := Read[string, string]{AsOf: At(w.tick), Delta: since > 0, Rows: map[string]string{}, Counted: since > 0}
	for id, value := range w.rows {
		if since == 0 || w.changed[id] >= since {
			out.Rows[id] = value
		} else {
			out.Unchanged++
		}
	}
	for id, at := range w.removed {
		if since > 0 && at >= since {
			out.Removed = append(out.Removed, id)
		}
	}
	return out, nil
}

// The mirror refreshed by deltas describes exactly what a whole read of
// the same tick does, across random upserts and removals.
func TestMirrorDeltasMatchWholeRead(t *testing.T) {
	ctx := context.Background()
	m := New()
	w := newWorld()
	scope := Scope{Load: "a", Generation: 1}
	rng := rand.New(rand.NewSource(7))
	for step := 0; step < 200; step++ {
		w.tick += int64(1 + rng.Intn(400))
		for i := rng.Intn(4); i > 0; i-- {
			id := fmt.Sprintf("r%d", rng.Intn(30))
			if rng.Intn(3) == 0 {
				w.drop(id)
			} else {
				w.set(id, fmt.Sprintf("v%d", rng.Intn(5)))
			}
		}
		table, out, err := Refresh(ctx, m, scope, w.tick, w)
		if err != nil {
			t.Fatal(err)
		}
		if out.Checked && out.Drift != 0 {
			t.Fatalf("step %d: drift %d", step, out.Drift)
		}
		whole, _ := w.Read(ctx, Watermark{})
		if d := Drift(table.Rows, whole.Rows, w.Equal); d != 0 || table.AsOf != At(w.tick) {
			t.Fatalf("step %d: mirror differs from the whole read by %d rows (as of %d, tick %d)", step, d, table.AsOf, w.tick)
		}
	}
}

func TestMirrorRefreshKinds(t *testing.T) {
	ctx := context.Background()
	m := New()
	w := newWorld()
	scope := Scope{Load: "a", Generation: 1}
	w.tick = 10
	w.set("a", "1")
	kinds := []Kind{}
	refresh := func() Outcome {
		t.Helper()
		_, out, err := Refresh(ctx, m, scope, w.tick, w)
		if err != nil {
			t.Fatal(err)
		}
		kinds = append(kinds, out.Kind)
		return out
	}
	refresh() // keyframe
	w.tick = 20
	refresh() // delta
	w.tick = 20 + 2501
	refresh() // delta: the old 2500-tick bound no longer applies
	w.drop("a")
	w.tick = 2521 + window + 1
	reads := len(w.reads)
	refresh() // keyframe: the window was outrun and the native answered in full
	if len(w.reads) != reads+1 {
		t.Fatalf("an outrun window took %d reads, want 1", len(w.reads)-reads)
	}
	if table, _ := Get[string, string](m, scope, "things"); len(table.Rows) != 0 || table.AsOf != At(w.tick) {
		t.Fatalf("inline full reply did not replace the section: %+v", table)
	}
	w.tick++
	w.miss = true
	w.set("b", "2")
	m.RequestResync("things")
	if out := refresh(); !out.Checked || out.Drift != 1 || !out.Requested {
		t.Fatalf("resync %+v", out)
	}
	scope.Generation = 2
	refresh() // keyframe: generation flip
	want := []Kind{Keyframe, Delta, Delta, Keyframe, Resync, Keyframe}
	if fmt.Sprint(kinds) != fmt.Sprint(want) {
		t.Fatalf("kinds %v, want %v", kinds, want)
	}
	for i := 0; i < ResyncEvery; i++ {
		w.tick++
		refresh()
	}
	if kinds[len(kinds)-1] != Resync && kinds[len(kinds)-2] != Resync {
		t.Fatalf("no cadence resync in %v", kinds)
	}
}

func TestMirrorViewIsConsistentAndFailedReadKeepsTable(t *testing.T) {
	ctx := context.Background()
	m := New()
	w := newWorld()
	scope := Scope{Load: "a"}
	w.tick = 5
	w.set("a", "1")
	if _, _, err := Refresh(ctx, m, scope, w.tick, w); err != nil {
		t.Fatal(err)
	}
	view := m.View()
	w.tick = 6
	w.set("a", "2")
	if _, _, err := Refresh(ctx, m, scope, w.tick, w); err != nil {
		t.Fatal(err)
	}
	old, _ := Of[string, string](view, "things")
	now, _ := Of[string, string](m.View(), "things")
	if mark, ok := Watermarks(m.View()).Watermark("things"); !ok || mark != At(6) {
		t.Fatalf("watermark %+v", mark)
	}
	Put(m, scope, "other", map[string]string{}, Watermark{Tick: 6, Seq: 0})
	Put(m, scope, "older", map[string]string{}, Watermark{Tick: 5, Seq: 3})
	if through, ok := m.View().CompleteThrough(); !ok || through != (Watermark{Tick: 5, Seq: 3}) {
		t.Fatalf("complete through %+v", through)
	}
	if old.Rows["a"] != "1" || now.Rows["a"] != "2" || now.Version <= old.Version {
		t.Fatalf("view moved: old %+v now %+v", old, now)
	}
	failing := failingSection{w}
	w.tick = 7
	table, _, err := Refresh[string, string](ctx, m, scope, w.tick, failing)
	if err == nil || table.Rows["a"] != "2" {
		t.Fatalf("failed read: %v %+v", err, table)
	}
}

type failingSection struct{ *world }

func (failingSection) Read(context.Context, Watermark) (Read[string, string], error) {
	return Read[string, string]{}, errors.New("boom")
}
