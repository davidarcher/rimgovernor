package observation

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// PlanningWindowSource reads site cells through observations_get_cells. The scheduler
// decides whether its held window remains usable or needs refreshing. Returned Held evidence
// preserves the observed tick and producing method for section provenance.
type PlanningWindowSource interface {
	PlanningWindow(ctx context.Context, identity *c.Identity, region policy.Rectangle) (facts.Held[PlanningCells], error)
}

// PlanExtentSource is a PlanningWindowSource that also knows the stored
// layout plan's extent (policy.LayoutPlan.Extent): the window then covers
// the plan as well as the colonists. An empty rect means no plan.
type PlanExtentSource interface {
	PlanExtent(ctx context.Context) (policy.Rectangle, error)
}

type planningWindowKey struct{}

// WithPlanningWindow attaches source to ctx so every planning colony read
// under it (a review, a planner's own observation) fills its window from
// source when the reply carries none.
func WithPlanningWindow(ctx context.Context, source PlanningWindowSource) context.Context {
	return context.WithValue(ctx, planningWindowKey{}, source)
}

// PlanningWindowFrom returns the source ctx carries, or nil.
func PlanningWindowFrom(ctx context.Context) PlanningWindowSource {
	source, _ := ctx.Value(planningWindowKey{}).(PlanningWindowSource)
	return source
}

// fillPlanningWindow sets the projection's window from ctx's source when a
// planning reply observed planning facts; a reading with no source keeps an
// empty window, which plans no site.
func fillPlanningWindow(ctx context.Context, reply *o.ColonyFactsReply, identity *c.Identity, projection *ColonyProjection) error {
	planning := reply.GetObserved().GetPlanning().GetObserved()
	if planning == nil {
		return nil
	}
	source := PlanningWindowFrom(ctx)
	if source == nil {
		return nil
	}
	var extent policy.Rectangle
	if plans, ok := source.(PlanExtentSource); ok {
		var err error
		if extent, err = plans.PlanExtent(ctx); err != nil {
			return err
		}
	}
	held, err := source.PlanningWindow(ctx, identity, bridge.PlanningWindowRect(projection.Bounds, extent))
	if err != nil {
		return err
	}
	projection.Region, projection.Cells, projection.Window = held.Value.Region, held.Value.Cells, held
	return nil
}
