package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// zoneRefresher is the step's refresher for the policy zone census
// (#358), mirrored (#795): planners ask it through observation.WithZones,
// and a review step asks it once up front. A review step refreshes the
// held census through the mirror once (a delta since its watermark; a
// keyframe when none is held, the watermark has left the tombstone window
// or the bundle carried the census in full); any other ask serves the
// held census while it is fresh under FactColony's tolerance.
type zoneRefresher struct {
	native observation.ZonesNative
	store  *facts.Store
	mirror *mirror.Mirror
	scope  facts.Scope
	tick   int64
	review bool
	// refreshed is set once a review step has refreshed the census.
	refreshed bool
	// carried is whether the step's bundle carried the zone census in
	// full (#593): the full read is then the cache hit, not the delta.
	carried bool
}

func (p *zoneRefresher) Zones(ctx context.Context, id *c.Identity) (facts.Held[bridge.ZonesRead], error) {
	held, ok := facts.Get[bridge.ZonesRead](p.store, facts.Zones)
	ok = ok && p.store.Scope() == p.scope && held.Value.Context.GetIdentity().GetColonyId() == id.GetColonyId() && held.Value.Context.GetIdentity().GetMapId() == id.GetMapId()
	if ok && !held.Stale.Any() && (p.refreshed || !p.review && p.store.Fresh(facts.Zones, max(p.tick, held.AsOf))) {
		return held, nil
	}
	if p.mirror == nil {
		p.mirror = mirror.New()
	}
	ms, section := mirrorScope(p.scope, id), &zoneMirror{native: p.native, id: id}
	name := section.Name()
	if ok {
		section.last = held.Value
	}
	var table mirror.Table[string, *o.ZoneState]
	if p.carried || !ok {
		read, err := section.Read(ctx, mirror.Watermark{})
		if err != nil {
			return facts.Held[bridge.ZonesRead]{}, err
		}
		table = mirror.Put(p.mirror, ms, name, read.Rows, read.AsOf)
		p.carried = false
	} else {
		if mirrored, has := mirror.Get[string, *o.ZoneState](p.mirror, ms, name); !has || mirrored.AsOf != mirror.At(held.AsOf) {
			mirror.Put(p.mirror, ms, name, zoneRows(held.Value.Rows), mirror.At(held.AsOf))
		}
		if p.store.ResyncDue(facts.Zones) {
			p.mirror.RequestResync(name)
		}
		var out mirror.Outcome
		var err error
		table, out, err = mirror.Refresh(ctx, p.mirror, ms, p.tick, section)
		mirrorEvent(ctx, facts.Zones, out)
		if err != nil && out.Kind != mirror.Delta {
			return facts.Held[bridge.ZonesRead]{}, err
		}
	}
	p.refreshed = p.review
	out := facts.Held[bridge.ZonesRead]{Value: section.census(table), AsOf: table.AsOf.Tick, Complete: true, Source: "rimgovernor/observations_list_zones"}
	facts.Put(p.store, p.scope, facts.Zones, out)
	return out, nil
}

// zoneRows keys a held census by zone id.
func zoneRows(rows []*o.ZoneState) map[string]*o.ZoneState {
	out := make(map[string]*o.ZoneState, len(rows))
	for _, row := range rows {
		out[row.GetId()] = row
	}
	return out
}
