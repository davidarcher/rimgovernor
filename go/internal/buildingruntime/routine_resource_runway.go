package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type resourceRunwaySource interface {
	ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error)
}

func (r *RoutineReviewer) resourceSurfaceOre(ctx context.Context, snapshot domain.GenerationSnapshot) map[policy.Resource]domain.Fact[int64] {
	out := map[policy.Resource]domain.Fact[int64]{}
	reader, ok := r.native.(resourceRunwaySource)
	if !ok {
		return out
	}
	for _, resource := range []policy.Resource{"Steel", "ComponentIndustrial", "Plasteel"} {
		rows, _, _, err := reader.ReadResourceSources(ctx, boundary.Identity(snapshot), string(resource))
		if err == nil {
			out[resource] = policy.SurfaceOre(rows)
		}
	}
	return out
}
