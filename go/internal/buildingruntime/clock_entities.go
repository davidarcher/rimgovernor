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
// whose native lacks them holds no zones, buildings or bills section.
type EntityNative interface {
	ReadZones(context.Context, *c.Identity) (bridge.EntityRows[*o.ZoneState], bridge.Result, error)
	ReadBuildings(context.Context, *c.Identity) (bridge.EntityRows[*o.BuildingState], bridge.Result, error)
	ReadBillStacks(context.Context, *c.Identity) (bridge.EntityRows[*o.BillStack], bridge.Result, error)
}

// EntitySection is one held entity section as the store keys it: the
// rows by entity id.
type EntitySection[T proto.Message] map[string]T

// refreshEntitySections is the review step's refresher for the zones,
// buildings and bills sections (#358). It runs once per full review step
// after the bundle has fixed the step's scope, and reads each section
// whole: the snapshot stream's frame serves them. A failed read keeps the
// held section: a plan may reason over stale state, apply refuses stale
// intent.
func refreshEntitySections(ctx context.Context, native EntityNative, f *clockFacts, identity *c.Identity, scope facts.Scope) {
	if native == nil || f == nil {
		return
	}
	// The policy zone refresher owns the typed zone census when available.
	// Do not overwrite it with a second read under a different store type.
	if _, policyZones := native.(observation.ZonesNative); !policyZones {
		refreshEntitySection(f, scope, identity, facts.Zones, "rimgovernor/observations_list_zones", func() (bridge.EntityRows[*o.ZoneState], error) {
			rows, _, err := native.ReadZones(ctx, identity)
			return rows, err
		})
	}
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
		clockSchedulerLog("%s: read failed, keeping the held section: %v", section, err)
		return
	}
	mirror.Put(f.mirror, mirrorScope(scope, identity), string(section), full.Rows, mirror.At(full.AsOf()))
	facts.Put(f.store, scope, section, facts.Held[EntitySection[T]]{Value: full.Rows, AsOf: full.AsOf(), Complete: true, Source: source})
}
