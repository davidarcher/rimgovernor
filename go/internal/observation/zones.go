package observation

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type ZonesNative interface {
	ReadZoneSection(context.Context, *c.Identity, int64) (bridge.ZonesRead, bridge.Result, error)
}
type ZonesSource interface {
	Zones(context.Context, *c.Identity) (facts.Held[bridge.ZonesRead], error)
}
type zonesKey struct{}

func WithZones(ctx context.Context, source ZonesSource) context.Context {
	return context.WithValue(ctx, zonesKey{}, source)
}

// ReadZoneSection serves the scheduler's zone section, or a full census outside
// a scheduler step (for standalone planners and inspection).
func ReadZoneSection(ctx context.Context, native ZonesNative, id *c.Identity) (facts.Held[bridge.ZonesRead], error) {
	if zones, ok := ctx.Value(zonesKey{}).(ZonesSource); ok {
		return zones.Zones(ctx, id)
	}
	if native == nil {
		return facts.Held[bridge.ZonesRead]{}, bridge.ErrUnavailable
	}
	read, _, err := native.ReadZoneSection(ctx, id, 0)
	return facts.Held[bridge.ZonesRead]{Value: read, AsOf: read.AsOf, Complete: err == nil, Source: "rimgovernor/observations_list_zones"}, err
}

// FillZones supplies the policy facts intentionally omitted by DecodeColony.
func FillZones(ctx context.Context, native ZonesNative, id *c.Identity, expected Identity, p *ColonyProjection) error {
	held, err := ReadZoneSection(ctx, native, id)
	if errors.Is(err, bridge.ErrUnavailable) {
		return nil
	}
	if err != nil {
		return err
	}
	actual, err := contextIdentity(held.Value.Context)
	if err != nil {
		return err
	}
	actual.Paused = expected.Paused
	if !cachedColonyBoundary(actual, expected, bridge.FactColony) {
		return ErrChanged
	}
	p.Zones = held
	applyZones(p, held.Value)
	return nil
}

func (s *routineBracket) ReadZoneSection(ctx context.Context, id *c.Identity, since int64) (bridge.ZonesRead, bridge.Result, error) {
	if native, ok := s.RoutineSource.(ZonesNative); ok {
		return native.ReadZoneSection(ctx, id, since)
	}
	return bridge.ZonesRead{}, bridge.Result{}, bridge.ErrUnavailable
}

func applyZones(p *ColonyProjection, read bridge.ZonesRead) {
	farms := make([]*o.FarmFacts, 0)
	storage := false
	p.Farms = nil
	for _, row := range read.Rows {
		storage = storage || row.GetFoodStorage()
		if farm := row.Farm; farm != nil {
			farms = append(farms, farm)
			p.Farms = append(p.Farms, FarmZoneFact{ID: row.GetId(), Crop: farm.GetCrop(), UsableCells: optional(farm.UsableCells)})
		}
	}
	p.Facts.FoodStorage = domain.Known(storage)
	p.ZoneMapToken = domain.Unknown[string]()
	if read.MapSnapshot != nil {
		p.ZoneMapToken = domain.Known(read.MapSnapshot.GetToken())
	}
	zoneProduction(farms, &p.Facts)
	p.FieldCrops = colonyFieldCrops(farms, p.Definitions)
	p.FoodFields = colonyFoodFields(farms, p.Definitions)
	p.FieldCapacityCrops = colonyFieldCrops(farms, p.Definitions, true)
}
