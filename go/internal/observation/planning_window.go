package observation

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// PlanningWindowSource serves the planning window (the site cells around
// the colony centre) to a colony read whose reply no longer carries
// planning.cells: a current native serves the window on demand through
// observations_get_cells and the scheduler decides, per step, whether the
// window it holds still serves or is read again (#356). The Held it returns
// names the tick the window describes and the method that produced it, so
// the reading's sections file it with its own provenance.
type PlanningWindowSource interface {
	PlanningWindow(ctx context.Context, identity *c.Identity, region policy.Rectangle) (facts.Held[PlanningCells], error)
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
// planning reply observed planning facts without listing the cells. A reply
// that lists them (an older native) is already decoded; a reading with no
// source keeps an empty window, which plans no site.
func fillPlanningWindow(ctx context.Context, reply *o.ColonyFactsReply, identity *c.Identity, projection *ColonyProjection) error {
	planning := reply.GetObserved().GetPlanning().GetObserved()
	if planning == nil || planning.Cells != nil {
		return nil
	}
	source := PlanningWindowFrom(ctx)
	if source == nil {
		return nil
	}
	held, err := source.PlanningWindow(ctx, identity, bridge.PlanningWindowRect(projection.Center, projection.Bounds))
	if err != nil {
		return err
	}
	projection.Region, projection.Cells, projection.Window = held.Value.Region, held.Value.Cells, held
	return nil
}
