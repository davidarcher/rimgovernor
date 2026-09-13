package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// CaravanCatalogRead is the validated subset of native's caravan catalog
// census CaravanDepartureBoundary needs: the catalog's own CAS token
// (referenced by FormCaravan's expected_catalog_token), the cargo groups a
// domain CargoItem definition must be resolved against before dispatch, and
// destination-tile route facts. Pawn eligibility for the requested crew is
// read separately via ReadPawns/ReadHomeColonists, the same shape every
// other pawn-order boundary already uses, so this type does not surface
// CaravanCatalog.pawns.
type CaravanCatalogRead struct {
	Context     *c.ObservationContext
	Token       string
	CargoGroups []*o.CargoGroup
	Routes      []*o.WorldRoute
}

// ReadCaravanCatalog reads the native FormCaravan catalog for one destination
// tile. As of this writing native implements no handler for
// rimgovernor/observations_read_caravan_catalog (confirmed by exhaustive
// source search of the C# mod); this wrapper exists so the Go boundary layer
// is real and ready the moment that handler ships, the same "proto ahead of
// use" shape already accepted for GearReplace/ImproveGear.
func (client *Client) ReadCaravanCatalog(ctx context.Context, identity *c.Identity, destination int32) (CaravanCatalogRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return CaravanCatalogRead{}, Result{}, err
	}
	if destination < 0 {
		return CaravanCatalogRead{}, Result{}, contract("invalid caravan catalog destination")
	}
	request := &o.CaravanCatalogRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Destination: proto.Int32(destination), Page: &c.PageRequest{Limit: proto.Uint32(256)}}
	reply := &o.CaravanCatalogReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_caravan_catalog", request, reply)
	if err != nil {
		return CaravanCatalogRead{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return CaravanCatalogRead{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.CaravanCatalogReply_Failure:
		return CaravanCatalogRead{}, raw, failure(v.Failure, raw)
	case *o.CaravanCatalogReply_Unavailable:
		return CaravanCatalogRead{}, raw, unavailable(v.Unavailable, raw)
	case *o.CaravanCatalogReply_Observed:
		out, err := caravanCatalogSelected(v.Observed, identity, destination)
		return out, raw, err
	default:
		return CaravanCatalogRead{}, raw, contract("caravan catalog outcome missing")
	}
}

func caravanCatalogSelected(v *o.CaravanCatalog, identity *c.Identity, destination int32) (CaravanCatalogRead, error) {
	if v == nil || v.Snapshot == nil {
		return CaravanCatalogRead{}, contract("caravan catalog snapshot missing")
	}
	if err := ValidateContext(v.Snapshot.Context); err != nil {
		return CaravanCatalogRead{}, err
	}
	if !sameIdentity(v.Snapshot.Context.Identity, identity) || validID(v.Snapshot.GetToken()) != nil {
		return CaravanCatalogRead{}, contract("caravan catalog world or token mismatch")
	}
	counts := v.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" {
		return CaravanCatalogRead{}, contract("incomplete caravan catalog page")
	}
	if len(v.CargoGroups) > 256 {
		return CaravanCatalogRead{}, contract("caravan catalog cargo groups exceed bound")
	}
	seenGroups := map[string]bool{}
	seenDefs := map[string]bool{}
	for _, group := range v.CargoGroups {
		if group == nil || validID(group.GetGroupId()) != nil || validID(group.GetDefName()) != nil || seenGroups[group.GetGroupId()] || seenDefs[group.GetDefName()] || group.Count == nil || group.GetCount() < 0 {
			return CaravanCatalogRead{}, contract("invalid caravan cargo group")
		}
		seenGroups[group.GetGroupId()] = true
		seenDefs[group.GetDefName()] = true
	}
	if len(v.Routes) > 4096 {
		return CaravanCatalogRead{}, contract("caravan catalog routes exceed bound")
	}
	seenRoutes := map[int32]bool{}
	requestedSeen := false
	for _, route := range v.Routes {
		if route == nil || route.Destination == nil || route.GetDestination() < 0 || seenRoutes[route.GetDestination()] {
			return CaravanCatalogRead{}, contract("invalid caravan route")
		}
		seenRoutes[route.GetDestination()] = true
		if route.GetDestination() == destination {
			requestedSeen = true
		}
		if route.EstimatedTicks != nil && route.GetEstimatedTicks() < 0 {
			return CaravanCatalogRead{}, contract("invalid caravan route estimate")
		}
	}
	if !requestedSeen {
		return CaravanCatalogRead{}, contract("caravan catalog missing requested destination route")
	}
	return CaravanCatalogRead{Context: v.Snapshot.Context, Token: v.Snapshot.GetToken(), CargoGroups: v.CargoGroups, Routes: v.Routes}, nil
}
