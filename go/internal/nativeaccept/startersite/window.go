package startersite

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// window serves the colony read its planning cells straight from the
// native read: a current native's colony facts list none, and the
// harness has no scheduler step to attach its refresher, so without this
// the start-site search saw an empty map.
type window struct {
	native buildingruntime.PlanningWindowNative
}

func (w window) PlanningWindow(ctx context.Context, identity *c.Identity, region policy.Rectangle) (facts.Held[observation.PlanningCells], error) {
	read, _, err := w.native.ReadPlanningWindow(ctx, identity, region)
	if err != nil {
		return facts.Held[observation.PlanningCells]{}, err
	}
	return facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: read.Region, Cells: read.Cells}, AsOf: read.Context.GetTick(), Complete: true}, nil
}
