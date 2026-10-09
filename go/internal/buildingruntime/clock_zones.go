package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// zoneRefresher is the step's refresher for the policy zone census:
// planners ask it through observation.WithZones, and a review step
// asks it once up front. A review step reads the census whole once; any
// other ask serves the held census until an invalidation marks it.
type zoneRefresher struct {
	native observation.ZonesNative
	store  *facts.Store
	scope  facts.Scope
	tick   int64
	review bool
	// refreshed is set once a review step has refreshed the census.
	refreshed bool
}

func (p *zoneRefresher) Zones(ctx context.Context, id *c.Identity) (facts.Held[bridge.ZonesRead], error) {
	held, ok := facts.Read[bridge.ZonesRead](ctx, p.store, facts.Zones)
	ok = ok && p.store.Scope() == p.scope && held.Value.Context.GetIdentity().GetColonyId() == id.GetColonyId() && held.Value.Context.GetIdentity().GetMapId() == id.GetMapId()
	if ok && (p.refreshed || !p.review) {
		return held, nil
	}
	read, _, err := p.native.ReadZoneSection(ctx, id)
	if err != nil {
		return facts.Held[bridge.ZonesRead]{}, err
	}
	p.refreshed = p.review
	out := facts.Held[bridge.ZonesRead]{Value: read, AsOf: read.AsOf, Complete: true, Source: "rimgovernor/observations_list_zones"}
	facts.Put(p.store, p.scope, facts.Zones, out)
	return out, nil
}
