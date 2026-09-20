package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type zoneRefresher struct {
	native    observation.ZonesNative
	store     *facts.Store
	scope     facts.Scope
	tick      int64
	refreshes *int
	// carried is whether the step's bundle carried the zone census in
	// full (#593): the full read is then the cache hit, not the delta.
	carried bool
}

func (p *zoneRefresher) Zones(ctx context.Context, id *c.Identity) (facts.Held[bridge.ZonesRead], error) {
	held, ok := facts.Get[bridge.ZonesRead](p.store, facts.Zones)
	ok = ok && p.store.Scope() == p.scope && held.Value.Context.GetIdentity().GetColonyId() == id.GetColonyId() && held.Value.Context.GetIdentity().GetMapId() == id.GetMapId() && (held.AsOf <= p.tick || domain.Tick(held.AsOf).FreshFor(domain.Tick(p.tick)))
	if ok && p.store.Fresh(facts.Zones, max(p.tick, held.AsOf)) {
		return held, nil
	}
	since := int64(0)
	if ok && !p.carried {
		since = held.AsOf
	}
	read, _, err := p.native.ReadZoneSection(ctx, id, since)
	if err != nil {
		return facts.Held[bridge.ZonesRead]{}, err
	}
	merged := bridge.MergeZones(held.Value, read)
	if read.Delta && uint64(len(merged.Rows)) != uint64(len(read.Rows))+uint64(read.Unchanged) {
		return facts.Held[bridge.ZonesRead]{}, fmt.Errorf("zone delta coverage does not match the held census")
	}
	if ok {
		refreshes := *p.refreshes
		*p.refreshes++
		requested := p.store.ResyncDue(facts.Zones)
		if read.Delta && planningWindowResync(requested, refreshes) {
			full, _, err := p.native.ReadZoneSection(ctx, id, 0)
			if err != nil {
				return facts.Held[bridge.ZonesRead]{}, err
			}
			if full.AsOf == read.AsOf {
				drift := bridge.ZoneDrift(merged, full)
				clockEvent(ctx, "facts", "zones_resync", fmt.Sprintf("zones resync drift=%d", drift), "drift", drift, "requested", requested, "since", since, "changed", len(read.Rows)+len(read.Removed), "unchanged", read.Unchanged)
			}
			merged = full
		}
	}
	out := facts.Held[bridge.ZonesRead]{Value: merged, AsOf: merged.AsOf, Complete: true, Source: "rimgovernor/observations_list_zones"}
	facts.Put(p.store, p.scope, facts.Zones, out)
	return out, nil
}
