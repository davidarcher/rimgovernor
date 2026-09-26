package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// EntityNative is the optional native side of the entity sections
// (bridge.Client.ReadZones, ReadBuildings, ReadBillStacks): a scheduler
// whose native lacks them holds no zones, buildings or bills section. The
// since tick asks for a delta over a held section (#358).
type EntityNative interface {
	ReadZones(context.Context, *c.Identity, int64) (bridge.EntityRows[*o.ZoneState], bridge.Result, error)
	ReadBuildings(context.Context, *c.Identity, int64) (bridge.EntityRows[*o.BuildingState], bridge.Result, error)
	ReadBillStacks(context.Context, *c.Identity, int64) (bridge.EntityRows[*o.BillStack], bridge.Result, error)
}

// EntitySection is one held entity section as the store keys it: the
// rows by entity id.
type EntitySection[T proto.Message] map[string]T

// refreshEntitySections is the review step's refresher for the zones,
// buildings and bills sections (#358), mirrored (#795). It runs once per
// full review step after the bundle has fixed the step's scope and tick.
// A section the bundle carried in full (#593: none is held, or its
// watermark has left the tombstone window) is filed as that keyframe;
// every other one is refreshed through the mirror, a delta since its
// watermark merged by id with removed ids dropped (a keyframe when
// nothing is held or the native refuses the delta as expired), with the
// mirror's resync backstop compared and logged as `[facts] <section>
// resync drift=<n>`. Refreshing every review, not only once a section
// passes its cadence, keeps the watermark inside the tombstone window, so
// a routine review costs the rows that changed rather than whole sections.
// A section already complete through the step's tick is left alone. A
// failed read keeps the held section: a plan may reason over stale
// state, apply refuses stale intent.
func refreshEntitySections(ctx context.Context, native EntityNative, f *clockFacts, identity *c.Identity, scope facts.Scope, tick int64, carried entitySectionsCarried) {
	if native == nil || f == nil {
		return
	}
	// The policy zone refresher owns the typed zone census when available.
	// Do not overwrite it with a second read under a different store type.
	if _, policyZones := native.(observation.ZonesNative); !policyZones {
		refreshEntitySection(ctx, f, scope, identity, tick, carried.zones, facts.Zones, "rimgovernor/observations_list_zones", func(since int64) (bridge.EntityRows[*o.ZoneState], error) {
			rows, _, err := native.ReadZones(ctx, identity, since)
			return rows, err
		})
	}
	refreshEntitySection(ctx, f, scope, identity, tick, carried.buildings, facts.Buildings, "rimgovernor/observations_list_buildings", func(since int64) (bridge.EntityRows[*o.BuildingState], error) {
		rows, _, err := native.ReadBuildings(ctx, identity, since)
		return rows, err
	})
	refreshEntitySection(ctx, f, scope, identity, tick, carried.bills, facts.Bills, "rimgovernor/observations_read_bills", func(since int64) (bridge.EntityRows[*o.BillStack], error) {
		rows, _, err := native.ReadBillStacks(ctx, identity, since)
		return rows, err
	})
}

// entitySectionsCarried names the entity sections the step's bundle
// carried in full (#593).
type entitySectionsCarried struct {
	zones, buildings, bills bool
}

func refreshEntitySection[T proto.Message](ctx context.Context, f *clockFacts, scope facts.Scope, identity *c.Identity, tick int64, carried bool, section facts.Section, source string, read func(since int64) (bridge.EntityRows[T], error)) {
	store := f.store
	held, ok := facts.Get[EntitySection[T]](store, section)
	ok = ok && store.Scope() == scope
	if ok && !carried && store.FreshWithin(section, tick, 0) {
		return
	}
	put := func(rows map[string]T, asOf int64) {
		facts.Put(store, scope, section, facts.Held[EntitySection[T]]{Value: rows, AsOf: asOf, Complete: true, Source: source})
	}
	ms, name := mirrorScope(scope, identity), string(section)
	if carried {
		// The bundle's keyframe, seeded under the full read's key: a
		// cache hit.
		full, err := read(0)
		if err != nil {
			clockSchedulerLog("%s: full read failed, held=%v: %v", section, ok, err)
			return
		}
		mirror.Put(f.mirror, ms, name, full.Rows, mirror.At(full.AsOf()))
		put(full.Rows, full.AsOf())
		return
	}
	if table, mirrored := mirror.Get[string, T](f.mirror, ms, name); ok && (!mirrored || table.AsOf != mirror.At(held.AsOf)) {
		mirror.Put(f.mirror, ms, name, map[string]T(held.Value), mirror.At(held.AsOf))
	}
	if store.ResyncDue(section) {
		f.mirror.RequestResync(name)
	}
	table, out, err := mirror.Refresh(ctx, f.mirror, ms, tick, entityMirror[T]{section: section, read: read})
	if out.Expired {
		clockSchedulerLog("%s: delta since %d expired, read in full", section, out.Since.Tick)
	}
	mirrorEvent(ctx, section, out)
	if err != nil {
		if out.Kind != mirror.Delta {
			clockSchedulerLog("%s: read failed, held=%v, serving it as of %d: %v", section, ok, held.AsOf, err)
			return
		}
		clockSchedulerLog("%s: resync read failed, keeping the delta as of %d: %v", section, table.AsOf.Tick, err)
	}
	put(table.Rows, table.AsOf.Tick)
}

// entitySectionsAsOf adds the held entity sections' as-of ticks to a
// review's as_of map, so the journal shows the spread a review planned
// against including the sections the refresher keeps (#358).
func entitySectionsAsOf(store *facts.Store, asOf map[facts.Section]int64) map[facts.Section]int64 {
	held := store.AsOf()
	for _, section := range []facts.Section{facts.Zones, facts.Buildings, facts.Bills} {
		if tick, ok := held[section]; ok {
			if asOf == nil {
				asOf = map[facts.Section]int64{}
			}
			asOf[section] = tick
		}
	}
	return asOf
}
