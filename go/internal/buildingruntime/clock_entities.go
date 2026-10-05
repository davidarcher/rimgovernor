package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// EntityNative is the native side of the buildings and bills sections
// (bridge.Client.ReadBuildings, ReadBillStacks); the scheduler requires it.
// The zone census is observation.ZonesNative's.
type EntityNative interface {
	ReadBuildings(context.Context, *c.Identity) (bridge.EntityRows[*o.BuildingState], bridge.Result, error)
	ReadBillStacks(context.Context, *c.Identity) (bridge.EntityRows[*o.BillStack], bridge.Result, error)
}

// EntitySection is one held entity section as the store keys it: the
// rows by entity id.
type EntitySection[T proto.Message] = bridge.Table[T]

// refreshEntitySections is the review step's refresher for the buildings
// and bills sections (#358). It runs once per full review step
// after the bundle has fixed the step's scope, and reads each section
// whole: the snapshot stream's frame serves them. A failed read keeps the
// held section: a plan may reason over stale state, apply refuses stale
// intent.
func refreshEntitySections(ctx context.Context, native EntityNative, f *clockFacts, identity *c.Identity, scope facts.Scope) {
	// The zone census is the policy zone refresher's (observation.ZonesNative),
	// typed differently under facts.Zones; this refresher never reads zones.
	refreshEntitySection(f, scope, identity, facts.Buildings, "rimgovernor/observations_list_buildings", func() (bridge.EntityRows[*o.BuildingState], error) {
		rows, _, err := native.ReadBuildings(ctx, identity)
		return rows, err
	})
	refreshEntitySection(f, scope, identity, facts.Bills, "rimgovernor/observations_read_bills", func() (bridge.EntityRows[*o.BillStack], error) {
		rows, _, err := native.ReadBillStacks(ctx, identity)
		return rows, err
	})
}

func refreshEntitySection[T proto.Message](f *clockFacts, scope facts.Scope, identity *c.Identity, section facts.Section, source string, read func() (bridge.EntityRows[T], error)) {
	full, err := read()
	if err != nil {
		return
	}
	facts.PutKeyed(f.store, scope, string(section), full.Rows, facts.At(full.AsOf()))
	facts.Put(f.store, scope, section, facts.Held[EntitySection[T]]{Value: full.Rows, AsOf: full.AsOf(), Complete: true, Source: source})
}
