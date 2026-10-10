package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// DeclareOrders is the declaration owned by policy.MaintainResource.
func (r *RoundsResourcePlanner) DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	declared, err := r.declareOrders(ctx, snapshot, projection, benches)
	return declared.For(policy.MaintainResource), err
}

// declareOrders declares MaintainResource's bench production: the stock-target
// bill of each floor below its target and the beer reserve (OrderDeclarer).
// The mining and sourcing methods stay the planner's own.
func (r *RoundsResourcePlanner) declareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	var floors []policy.ResourceFloor
	for resource, target := range r.reviewer.resourceTargets(snapshot) {
		if resource == "Beer" {
			wort := projection.Facts.Items.Wort
			if wort == "" {
				return policy.Abstaining(policy.UnreadWort), nil
			}
			floors = append(floors, policy.ResourceFloor{Resource: wort, Target: target, Reserve: true})
			continue
		}
		floors = append(floors, policy.ResourceFloor{Resource: resource, Target: target})
	}
	return policy.DeclareResourceOrders(policy.ResourceOrderRequest{Floors: floors, Stock: projection.Resources, Benches: domain.Known(benches)})
}
