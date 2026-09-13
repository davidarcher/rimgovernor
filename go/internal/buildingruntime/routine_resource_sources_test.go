package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// fakeResourceSourceNative implements only the ReadResourceSources half of
// RoutineResourceSource that sourcesForDeficit actually calls; the other
// methods are unused by this narrow test and simply panic if ever reached.
type fakeResourceSourceNative struct {
	rows []bridge.ResourceSourceRow
	err  error
}

func (f *fakeResourceSourceNative) ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error) {
	panic("unused")
}
func (f *fakeResourceSourceNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	panic("unused")
}
func (f *fakeResourceSourceNative) ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error) {
	panic("unused")
}
func (f *fakeResourceSourceNative) PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error) {
	panic("unused")
}
func (f *fakeResourceSourceNative) ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, bridge.Result, error) {
	return f.rows, bridge.Result{}, f.err
}

func TestSourcesForDeficitSelectsAgainstOutstandingNeed(t *testing.T) {
	native := &fakeResourceSourceNative{rows: []bridge.ResourceSourceRow{
		{ThingID: "rock1", Yield: 40, Distance: 3, Method: policy.ResourceSourceMine, Safety: "open_surface"},
		{ThingID: "rock2", Yield: 40, Distance: 30, Method: policy.ResourceSourceMine, Safety: "open_surface"},
	}}
	planner := &RoutineResourcePlanner{native: native}
	stock := domain.Known([]policy.Amount{{Resource: "Steel", Count: 10}})
	selected := planner.sourcesForDeficit(context.Background(), &c.Identity{}, "Steel", 50, stock)
	if len(selected) != 1 || selected[0].ThingID != "rock1" {
		t.Fatal(selected)
	}
}

func TestSourcesForDeficitReturnsNilWhenStockUnknown(t *testing.T) {
	native := &fakeResourceSourceNative{}
	planner := &RoutineResourcePlanner{native: native}
	selected := planner.sourcesForDeficit(context.Background(), &c.Identity{}, "Steel", 50, domain.Unknown[[]policy.Amount]())
	if selected != nil {
		t.Fatal(selected)
	}
}

func TestSourcesForDeficitSwallowsNativeReadFailure(t *testing.T) {
	native := &fakeResourceSourceNative{err: errors.New("native unavailable")}
	planner := &RoutineResourcePlanner{native: native}
	stock := domain.Known([]policy.Amount{{Resource: "Steel", Count: 0}})
	selected := planner.sourcesForDeficit(context.Background(), &c.Identity{}, "Steel", 50, stock)
	if selected != nil {
		t.Fatal(selected)
	}
}
