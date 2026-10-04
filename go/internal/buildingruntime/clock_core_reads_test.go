package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The scheduler requires the zone, planning-window and entity reads
// (ClockWindowNative): the core fake answers them empty at its status tick.
// The speed matrix native serves them at the tick of the moment without
// counting them: its reads-per-step bound (#593) is about the bundle and the
// coordinator's status read, not the review's section refreshes.
func (n *speedNative) observedNow() *c.ObservationContext {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	return n.context()
}

func (n *speedNative) ReadZoneSection(ctx context.Context, _ *c.Identity) (bridge.ZonesRead, bridge.Result, error) {
	observed := n.observedNow()
	return bridge.ZonesRead{Context: observed, AsOf: observed.GetTick()}, bridge.Result{}, ctx.Err()
}

func (n *speedNative) ReadPlanningWindow(ctx context.Context, _ *c.Identity, rect policy.Rectangle) (bridge.PlanningWindow, bridge.Result, error) {
	return bridge.PlanningWindow{Context: n.observedNow(), Region: rect}, bridge.Result{}, ctx.Err()
}

func (n *speedNative) ReadBuildings(ctx context.Context, _ *c.Identity) (bridge.EntityRows[*o.BuildingState], bridge.Result, error) {
	return bridge.EntityRows[*o.BuildingState]{Context: n.observedNow(), Rows: bridge.Table[*o.BuildingState]{}}, bridge.Result{}, ctx.Err()
}

func (n *speedNative) ReadBillStacks(ctx context.Context, _ *c.Identity) (bridge.EntityRows[*o.BillStack], bridge.Result, error) {
	return bridge.EntityRows[*o.BillStack]{Context: n.observedNow(), Rows: bridge.Table[*o.BillStack]{}}, bridge.Result{}, ctx.Err()
}

// The joined native shares one lock across every surface.
func (f *joinedClockNative) ReadZoneSection(ctx context.Context, id *c.Identity) (bridge.ZonesRead, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.source.ReadZoneSection(ctx, id)
}

func (f *joinedClockNative) ReadPlanningWindow(ctx context.Context, id *c.Identity, rect policy.Rectangle) (bridge.PlanningWindow, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.source.ReadPlanningWindow(ctx, id, rect)
}

func (f *joinedClockNative) ReadBuildings(ctx context.Context, id *c.Identity) (bridge.EntityRows[*o.BuildingState], bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.source.ReadBuildings(ctx, id)
}

func (f *joinedClockNative) ReadBillStacks(ctx context.Context, id *c.Identity) (bridge.EntityRows[*o.BillStack], bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.source.ReadBillStacks(ctx, id)
}

func (f *clockCoreFake) observed() *c.ObservationContext {
	return proto.Clone(f.status.Context).(*c.ObservationContext)
}

func (f *clockCoreFake) ReadZoneSection(ctx context.Context, _ *c.Identity) (bridge.ZonesRead, bridge.Result, error) {
	return bridge.ZonesRead{Context: f.observed(), AsOf: f.status.Context.GetTick()}, bridge.Result{}, ctx.Err()
}

func (f *clockCoreFake) ReadPlanningWindow(ctx context.Context, _ *c.Identity, rect policy.Rectangle) (bridge.PlanningWindow, bridge.Result, error) {
	return bridge.PlanningWindow{Context: f.observed(), Region: rect}, bridge.Result{}, ctx.Err()
}

func (f *clockCoreFake) ReadBuildings(ctx context.Context, _ *c.Identity) (bridge.EntityRows[*o.BuildingState], bridge.Result, error) {
	return bridge.EntityRows[*o.BuildingState]{Context: f.observed(), Rows: bridge.Table[*o.BuildingState]{}}, bridge.Result{}, ctx.Err()
}

func (f *clockCoreFake) ReadBillStacks(ctx context.Context, _ *c.Identity) (bridge.EntityRows[*o.BillStack], bridge.Result, error) {
	return bridge.EntityRows[*o.BillStack]{Context: f.observed(), Rows: bridge.Table[*o.BillStack]{}}, bridge.Result{}, ctx.Err()
}
