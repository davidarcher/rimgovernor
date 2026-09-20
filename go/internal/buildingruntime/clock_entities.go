package buildingruntime

import (
	"context"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
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

// entitySectionsResyncEvery is the entity refresher's backstop cadence,
// the planning window's (#357): every this-many refreshes of a held
// section is a full read compared against the delta, so an entity change
// the native tracker missed surfaces as drift instead of living on.
const entitySectionsResyncEvery = planningWindowResyncEvery

// EntitySection is one held entity section as the store keys it: the
// rows by entity id.
type EntitySection[T proto.Message] map[string]T

// refreshEntitySections is the review step's refresher for the zones,
// buildings and bills sections (#358). It runs once per full review step
// after the bundle has fixed the step's scope and tick: a section the
// store does not hold is read in full; a held section still fresh under
// FactColony's tolerance is left alone; a stale one (its cadence passed,
// or an invalidation marked it) is read as a delta since its as-of tick
// and merged by id, removed ids dropped. A delta the native refuses as
// expired (the since tick older than its tombstone window) is read in
// full, as is one the step's bundle carried in full (#593) and one
// whose as-of tick the tombstone window has already outrun (the delta
// would only be refused). Every entitySectionsResyncEvery-th refresh of
// a section, and the first after a resync request, reads the section in
// full beside the delta and logs how many rows the delta got wrong as
// `[facts] <section> resync drift=<n>`; non-zero drift is a bug against
// the native tracker.
// A failed read keeps the held section: a plan may reason over stale
// state, apply refuses stale intent.
func refreshEntitySections(ctx context.Context, native EntityNative, f *clockFacts, identity *c.Identity, scope facts.Scope, tick int64, carried entitySectionsCarried) {
	if native == nil || f == nil {
		return
	}
	// The policy zone refresher owns the typed zone census when available.
	// Do not overwrite it with a second read under a different store type.
	if _, policyZones := native.(observation.ZonesNative); !policyZones {
		refreshEntitySection(ctx, f, scope, tick, carried.zones, facts.Zones, "rimgovernor/observations_list_zones", func(since int64) (bridge.EntityRows[*o.ZoneState], error) {
			rows, _, err := native.ReadZones(ctx, identity, since)
			return rows, err
		})
	}
	refreshEntitySection(ctx, f, scope, tick, carried.buildings, facts.Buildings, "rimgovernor/observations_list_buildings", func(since int64) (bridge.EntityRows[*o.BuildingState], error) {
		rows, _, err := native.ReadBuildings(ctx, identity, since)
		return rows, err
	})
	refreshEntitySection(ctx, f, scope, tick, carried.bills, facts.Bills, "rimgovernor/observations_read_bills", func(since int64) (bridge.EntityRows[*o.BillStack], error) {
		rows, _, err := native.ReadBillStacks(ctx, identity, since)
		return rows, err
	})
}

// entitySectionsCarried names the entity sections the step's bundle
// carried in full (#593).
type entitySectionsCarried struct {
	zones, buildings, bills bool
}

func refreshEntitySection[T proto.Message](ctx context.Context, f *clockFacts, scope facts.Scope, tick int64, carried bool, section facts.Section, source string, read func(since int64) (bridge.EntityRows[T], error)) {
	store := f.store
	held, ok := facts.Get[EntitySection[T]](store, section)
	ok = ok && store.Scope() == scope
	if ok && store.Fresh(section, tick) {
		return
	}
	put := func(rows map[string]T, asOf int64) {
		facts.Put(store, scope, section, facts.Held[EntitySection[T]]{Value: rows, AsOf: asOf, Complete: true, Source: source})
	}
	requested := store.ResyncDue(section)
	if ok && (carried || tick-held.AsOf > bridge.EntityTombstoneWindow) {
		f.entityRefresh(section)
	}
	if !ok || carried || tick-held.AsOf > bridge.EntityTombstoneWindow {
		full, err := read(0)
		if err != nil {
			clockSchedulerLog("%s: full read failed, held=%v: %v", section, ok, err)
			return
		}
		put(full.Rows, full.AsOf())
		return
	}
	refreshes := f.entityRefresh(section)
	delta, err := read(held.AsOf)
	if errors.Is(err, bridge.ErrDeltaExpired) {
		clockSchedulerLog("%s: delta since %d expired, reading in full", section, held.AsOf)
		delta, err = read(0)
	}
	if err != nil {
		clockSchedulerLog("%s: read failed, serving the held section as of %d: %v", section, held.AsOf, err)
		return
	}
	rows := bridge.MergeEntities(held.Value, delta)
	if delta.Delta && entitySectionsResync(requested, refreshes) {
		full, err := read(0)
		if err != nil {
			clockSchedulerLog("%s: resync read failed, keeping the delta as of %d: %v", section, delta.AsOf(), err)
		} else {
			// Drift is meaningful only when both reads describe one tick.
			if full.AsOf() == delta.AsOf() {
				drift := bridge.EntityDrift(rows, full.Rows)
				clockEvent(ctx, "facts", string(section)+"_resync", fmt.Sprintf("%s resync drift=%d", section, drift), "drift", drift, "requested", requested, "since", held.AsOf, "changed", len(delta.Rows), "removed", len(delta.Removed), "unchanged", delta.Unchanged)
			}
			delta, rows = full, full.Rows
		}
	}
	put(rows, delta.AsOf())
}

// entitySectionsResync is whether a refresh of a held section is also a
// full read: one was requested, or the cadence is due.
func entitySectionsResync(requested bool, refreshes int) bool {
	return requested || refreshes%entitySectionsResyncEvery == entitySectionsResyncEvery-1
}

// entityRefresh counts one refresh of a held entity section and returns
// the count before it; the step goroutine alone touches the counters.
func (f *clockFacts) entityRefresh(section facts.Section) int {
	if f.entityRefreshes == nil {
		f.entityRefreshes = map[facts.Section]int{}
	}
	n := f.entityRefreshes[section]
	f.entityRefreshes[section] = n + 1
	return n
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
