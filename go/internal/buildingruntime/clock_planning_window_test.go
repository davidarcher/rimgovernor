package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// The refresher's decision table (#356): a full review step reads a stale
// window and serves a fresh one; a timer or event step serves whatever it
// holds; nothing held is read on demand whatever the step.
func TestPlanningWindowReadDecision(t *testing.T) {
	for _, tc := range []struct {
		name                string
		review, held, fresh bool
		read                bool
	}{
		{"review, fresh", true, true, true, false},
		{"review, stale", true, true, false, true},
		{"review, nothing held", true, false, false, true},
		{"timer, fresh", false, true, true, false},
		{"timer, stale", false, true, false, false},
		{"planner demand, nothing held", false, false, false, true},
	} {
		if got := planningWindowRead(tc.review, tc.held, tc.fresh); got != tc.read {
			t.Errorf("%s: read=%v, want %v", tc.name, got, tc.read)
		}
	}
}

type planningWindowFake struct {
	reads  int
	tick   int64
	err    error
	region policy.Rectangle
	// since records every ask's since tick; delta, when set, answers a
	// positive ask the way a native with the change grid does.
	since []int64
	delta func(rect policy.Rectangle, since int64) bridge.PlanningWindow
}

func (f *planningWindowFake) ReadPlanningWindow(_ context.Context, _ *c.Identity, rect policy.Rectangle, since int64) (bridge.PlanningWindow, bridge.Result, error) {
	f.reads++
	f.region = rect
	f.since = append(f.since, since)
	if f.err != nil {
		return bridge.PlanningWindow{}, bridge.Result{}, f.err
	}
	if since > 0 && f.delta != nil {
		out := f.delta(rect, since)
		out.Context, out.Region, out.Delta = &c.ObservationContext{Tick: proto.Int64(f.tick)}, rect, true
		return out, bridge.Result{}, nil
	}
	return bridge.PlanningWindow{Context: &c.ObservationContext{Tick: proto.Int64(f.tick)}, Region: rect, Cells: []policy.SiteCell{{Cell: domain.Cell{X: rect.X, Z: rect.Z}, Walkable: domain.Known(true)}}}, bridge.Result{}, nil
}

func TestPlanningWindowServesStoreAndReadsOnDemand(t *testing.T) {
	store := facts.NewStore()
	scope := facts.Scope{Load: "load", Generation: 1}
	native := &planningWindowFake{tick: 100}
	rect := policy.Rectangle{X: 0, Z: 0, Width: 45, Height: 45}
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	// A planner's demand with nothing held reads and files the section.
	w := &planningWindow{native: native, store: store, scope: scope, tick: 100, review: false}
	held, err := w.PlanningWindow(context.Background(), identity, rect)
	if err != nil || native.reads != 1 || native.region != rect || held.AsOf != 100 || held.Source != "rimgovernor/observations_get_cells" || !held.Complete || len(held.Value.Cells) != 1 {
		t.Fatalf("%+v %v reads=%d", held, err, native.reads)
	}
	if stored, ok := facts.Get[observation.PlanningCells](store, facts.PlanningCells); !ok || stored.AsOf != 100 {
		t.Fatalf("stored = %+v ok=%v", stored, ok)
	}
	// A later timer step far past the tolerance still serves the held window.
	w = &planningWindow{native: native, store: store, scope: scope, tick: 100 + bridge.FactTickToleranceColony + 1, review: false}
	if held, err = w.PlanningWindow(context.Background(), identity, rect); err != nil || native.reads != 1 || held.AsOf != 100 {
		t.Fatalf("%+v %v reads=%d", held, err, native.reads)
	}
	// A full review at that tick reads again; one within tolerance does not.
	native.tick = 2700
	w = &planningWindow{native: native, store: store, scope: scope, tick: 100 + bridge.FactTickToleranceColony + 1, review: true}
	if held, err = w.PlanningWindow(context.Background(), identity, rect); err != nil || native.reads != 2 || held.AsOf != 2700 {
		t.Fatalf("%+v %v reads=%d", held, err, native.reads)
	}
	w = &planningWindow{native: native, store: store, scope: scope, tick: 2800, review: true}
	if held, err = w.PlanningWindow(context.Background(), identity, rect); err != nil || native.reads != 2 || held.AsOf != 2700 {
		t.Fatalf("%+v %v reads=%d", held, err, native.reads)
	}
	// A region within planningWindowSlack of the held one is served from
	// it, at the held place (#593); one further off is read at its own.
	if held, err = w.PlanningWindow(context.Background(), identity, policy.Rectangle{X: 4, Z: 4, Width: 45, Height: 45}); err != nil || native.reads != 2 || held.Value.Region != rect {
		t.Fatalf("%+v %v reads=%d", held, err, native.reads)
	}
	other := policy.Rectangle{X: 5, Z: 5, Width: 45, Height: 45}
	if held, err = w.PlanningWindow(context.Background(), identity, other); err != nil || native.reads != 3 || held.Value.Region != other {
		t.Fatalf("%+v %v reads=%d", held, err, native.reads)
	}
	// A failed read serves the held window when one covers the region and
	// fails the ask when none does.
	native.err = bridge.ErrUnavailable
	w = &planningWindow{native: native, store: store, scope: scope, tick: 9000, review: true}
	if held, err = w.PlanningWindow(context.Background(), identity, other); err != nil || held.Value.Region != other {
		t.Fatalf("%+v %v", held, err)
	}
	facts.Put(store, facts.Scope{Load: "other", Generation: 1}, facts.Colony, facts.Held[string]{})
	if _, err = w.PlanningWindow(context.Background(), identity, other); !errors.Is(err, bridge.ErrUnavailable) {
		t.Fatal(err)
	}
	// Every refresh of a held window asks since its as-of tick; a native
	// without the grid answers in full and the refresher keeps counting.
	if native.since[0] != 0 || native.since[1] != 100 || native.since[2] != 0 || native.since[3] != 2700 || native.since[4] != 0 {
		t.Fatal(native.since)
	}
}

func TestPlanningWindowCovers(t *testing.T) {
	held := policy.Rectangle{X: 10, Z: 10, Width: 45, Height: 45}
	for _, tc := range []struct {
		region policy.Rectangle
		covers bool
	}{
		{held, true},
		{policy.Rectangle{X: 14, Z: 6, Width: 45, Height: 45}, true},
		{policy.Rectangle{X: 15, Z: 10, Width: 45, Height: 45}, false},
		{policy.Rectangle{X: 10, Z: 5, Width: 45, Height: 45}, false},
		{policy.Rectangle{X: 10, Z: 10, Width: 44, Height: 45}, false},
	} {
		if got := planningWindowCovers(held, tc.region); got != tc.covers {
			t.Errorf("%+v: covers=%v, want %v", tc.region, got, tc.covers)
		}
	}
}

func siteCell(x, z int32, walkable bool) policy.SiteCell {
	return policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(walkable)}
}

// A refresh of a held window merges the delta over it (#357): changed
// rows replace held ones, fogged cells leave, the rest stay, in row order.
func TestPlanningWindowMergesDelta(t *testing.T) {
	store := facts.NewStore()
	scope := facts.Scope{Load: "load", Generation: 1}
	rect := policy.Rectangle{X: 0, Z: 0, Width: 3, Height: 1}
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	facts.Put(store, scope, facts.PlanningCells, facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: rect, Cells: []policy.SiteCell{siteCell(0, 0, true), siteCell(1, 0, true), siteCell(2, 0, true)}}, AsOf: 100, Complete: true})
	native := &planningWindowFake{tick: 5000, delta: func(_ policy.Rectangle, since int64) bridge.PlanningWindow {
		return bridge.PlanningWindow{Cells: []policy.SiteCell{siteCell(1, 0, false)}, Fogged: []domain.Cell{{X: 2, Z: 0}}, Unchanged: 1}
	}}
	refreshes := 0
	w := &planningWindow{native: native, store: store, refreshes: &refreshes, scope: scope, tick: 5000, review: true}
	held, err := w.PlanningWindow(context.Background(), identity, rect)
	if err != nil || native.reads != 1 || native.since[0] != 100 || held.AsOf != 5000 || refreshes != 1 {
		t.Fatalf("%+v %v reads=%d since=%v", held, err, native.reads, native.since)
	}
	if want := []policy.SiteCell{siteCell(0, 0, true), siteCell(1, 0, false)}; len(held.Value.Cells) != 2 || held.Value.Cells[0] != want[0] || held.Value.Cells[1] != want[1] {
		t.Fatalf("merged = %+v", held.Value.Cells)
	}
	// An invalidation keeps the window and marks it stale; the next review
	// refresh is again a delta since the merged as-of.
	store.InvalidateFamily(bridge.FactColony)
	w = &planningWindow{native: native, store: store, refreshes: &refreshes, scope: scope, tick: 5001, review: true}
	if held, err = w.PlanningWindow(context.Background(), identity, rect); err != nil || native.reads != 2 || native.since[1] != 5000 || len(held.Value.Cells) != 2 {
		t.Fatalf("%+v %v reads=%d since=%v", held, err, native.reads, native.since)
	}
}

// The resync backstop: every planningWindowResyncEvery-th refresh, and the
// first after a requested resync, reads the whole window beside the delta
// and files the full read; the two are compared for drift.
func TestPlanningWindowResyncCadence(t *testing.T) {
	for _, tc := range []struct {
		name      string
		requested bool
		refreshes int
		resync    bool
	}{
		{"first refresh", false, 0, false},
		{"seventh", false, 6, false},
		{"eighth", false, 7, true},
		{"ninth", false, 8, false},
		{"sixteenth", false, 15, true},
		{"requested", true, 3, true},
	} {
		if got := planningWindowResync(tc.requested, tc.refreshes); got != tc.resync {
			t.Errorf("%s: resync=%v, want %v", tc.name, got, tc.resync)
		}
	}
	store := facts.NewStore()
	scope := facts.Scope{Load: "load", Generation: 1}
	rect := policy.Rectangle{X: 0, Z: 0, Width: 2, Height: 1}
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	facts.Put(store, scope, facts.PlanningCells, facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: rect, Cells: []policy.SiteCell{siteCell(0, 0, true), siteCell(1, 0, true)}}, AsOf: 100, Complete: true})
	native := &planningWindowFake{tick: 5000, delta: func(policy.Rectangle, int64) bridge.PlanningWindow { return bridge.PlanningWindow{Unchanged: 2} }}
	refreshes := 0
	store.RequestResync(facts.PlanningCells)
	w := &planningWindow{native: native, store: store, refreshes: &refreshes, scope: scope, tick: 5000, review: true}
	held, err := w.PlanningWindow(context.Background(), identity, rect)
	// The full read (one row) replaces the merged delta (two rows).
	if err != nil || native.reads != 2 || native.since[0] != 100 || native.since[1] != 0 || len(held.Value.Cells) != 1 || held.AsOf != 5000 {
		t.Fatalf("%+v %v reads=%d since=%v", held, err, native.reads, native.since)
	}
	// The request was consumed: the next stale review refresh is a delta alone.
	store.Invalidate(facts.PlanningCells)
	w = &planningWindow{native: native, store: store, refreshes: &refreshes, scope: scope, tick: 5001, review: true}
	if _, err = w.PlanningWindow(context.Background(), identity, rect); err != nil || native.reads != 3 || native.since[2] != 5000 {
		t.Fatalf("%v reads=%d since=%v", err, native.reads, native.since)
	}
}

func TestPlanningWindowDrift(t *testing.T) {
	merged := []policy.SiteCell{siteCell(0, 0, true), siteCell(1, 0, true), siteCell(2, 0, true)}
	if got := planningWindowDrift(merged, merged); got != 0 {
		t.Fatal(got)
	}
	// One row differs, one is missing from the full read, one is extra in it.
	full := []policy.SiteCell{siteCell(0, 0, true), siteCell(1, 0, false), siteCell(3, 0, true)}
	if got := planningWindowDrift(merged, full); got != 3 {
		t.Fatal(got)
	}
}
