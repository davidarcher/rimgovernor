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

// Every ask cuts the window from the frame grid and files it; a
// failed read serves the held window of the same region, else fails.
func TestPlanningWindowReadsEveryAsk(t *testing.T) {
	store := facts.NewStore()
	scope := facts.Scope{Load: "load", Generation: 1}
	native := &planningWindowFake{tick: 100}
	rect := policy.Rectangle{X: 0, Z: 0, Width: 45, Height: 45}
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	w := &planningWindow{native: native, store: store, scope: scope}
	held, err := w.PlanningWindow(context.Background(), identity, rect)
	if err != nil || native.reads != 1 || native.region != rect || held.AsOf != 100 || held.Source != "rimgovernor/observations_get_cells" || !held.Complete || len(held.Value.Cells) != 1 {
		t.Fatalf("%+v %v reads=%d", held, err, native.reads)
	}
	if stored, ok := facts.Get[observation.PlanningCells](store, facts.PlanningCells); !ok || stored.AsOf != 100 {
		t.Fatalf("stored = %+v ok=%v", stored, ok)
	}
	native.tick = 2700
	other := policy.Rectangle{X: 5, Z: 5, Width: 45, Height: 45}
	if held, err = w.PlanningWindow(context.Background(), identity, other); err != nil || native.reads != 2 || held.AsOf != 2700 || held.Value.Region != other {
		t.Fatalf("%+v %v reads=%d", held, err, native.reads)
	}
	native.err = bridge.ErrUnavailable
	if held, err = w.PlanningWindow(context.Background(), identity, other); err != nil || held.Value.Region != other || held.AsOf != 2700 {
		t.Fatalf("%+v %v", held, err)
	}
	if _, err = w.PlanningWindow(context.Background(), identity, rect); !errors.Is(err, bridge.ErrUnavailable) {
		t.Fatal(err)
	}
}
