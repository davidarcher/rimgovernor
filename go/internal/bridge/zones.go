package bridge

import (
	"context"
	"sort"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ZonesRead is the complete zone census, rows in id order. Rows are
// immutable after publication to the facts store.
type ZonesRead struct {
	Context *c.ObservationContext
	Rows    []*o.ZoneState
	AsOf    int64
}

// zoneSectionRequest is the zone census read, shared with the bundle's zones
// family (#593).
func zoneSectionRequest(identity *c.Identity) *o.ListZonesRequest {
	return &o.ListZonesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
}

// ReadZoneSection reads the whole census. A failure never silently
// replaces a held census.
func (client *Client) ReadZoneSection(ctx context.Context, identity *c.Identity) (ZonesRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return ZonesRead{}, Result{}, err
	}
	reply := &o.ListZonesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_zones", zoneSectionRequest(identity), reply)
	if err != nil {
		return ZonesRead{}, raw, err
	}
	if err := buildingUnknown(reply); err != nil {
		return ZonesRead{}, raw, err
	}
	if f := reply.GetFailure(); f != nil {
		return ZonesRead{}, raw, failure(f, raw)
	}
	if u := reply.GetUnavailable(); u != nil {
		return ZonesRead{}, raw, unavailable(u, raw)
	}
	out, err := decodeZones(reply.GetObserved(), identity)
	return out, raw, err
}

// decodeZones validates and decodes a complete zone census.
func decodeZones(v *o.ZonesSnapshot, identity *c.Identity) (ZonesRead, error) {
	if err := validateZonePage(v, identity); err != nil {
		return ZonesRead{}, err
	}
	out := ZonesRead{Context: v.Context, AsOf: v.Context.GetTick()}
	seen := map[string]bool{}
	for _, row := range v.Zones {
		if seen[row.GetId()] {
			return ZonesRead{}, contract("duplicate zone")
		}
		seen[row.GetId()] = true
		out.Rows = append(out.Rows, row)
	}
	sort.Slice(out.Rows, func(i, j int) bool { return out.Rows[i].GetId() < out.Rows[j].GetId() })
	return out, nil
}

func validateZonePage(v *o.ZonesSnapshot, identity *c.Identity) error {
	if v == nil {
		return contract("missing zone snapshot")
	}
	if err := ValidateContext(v.Context); err != nil {
		return err
	}
	if !sameIdentity(v.Context.Identity, identity) {
		return contract("zone identity mismatch")
	}
	for _, row := range v.Zones {
		if row == nil || validID(row.GetId()) != nil || row.FoodStorage == nil {
			return contract("invalid zone row")
		}
		if farm := row.Farm; farm != nil {
			if farm.GetZone().GetId() != row.GetId() || validID(farm.GetCrop()) != nil || farm.UsableCells == nil || farm.PlantedCells == nil || farm.GrowingCells == nil || farm.EdibleCrop == nil || farm.GetGrowingCells() > farm.GetPlantedCells() || !combatNumber(farm.HarvestLowerBoundDays, true) || !combatNumber(farm.NutritionPerHarvestCell, true) {
				return contract("invalid zone farm facts")
			}
		} else if row.GetType() == "growing" {
			return contract("missing zone farm facts")
		}
	}
	return nil
}
