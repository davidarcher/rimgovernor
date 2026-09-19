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
}

func (f *planningWindowFake) ReadPlanningWindow(_ context.Context, _ *c.Identity, rect policy.Rectangle) (bridge.PlanningWindow, bridge.Result, error) {
	f.reads++
	f.region = rect
	if f.err != nil {
		return bridge.PlanningWindow{}, bridge.Result{}, f.err
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
	// Another region is never served from a held one.
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
	store.InvalidateAll()
	if _, err = w.PlanningWindow(context.Background(), identity, other); !errors.Is(err, bridge.ErrUnavailable) {
		t.Fatal(err)
	}
}
