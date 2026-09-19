package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// CaravanCatalogRead is the validated subset of native's caravan catalog
// census the departure boundary needs: the catalog's own CAS token
// (FormCaravan's expected_catalog_token), the cargo groups with their food
// facts (#464: per-unit nutrition, unrefrigerated shelf life, the reserve
// flag and the colonists whose diet admits each group), and the route
// facts for the requested destination. Crew eligibility is read separately
// through ReadPawns, the shape every pawn-order boundary uses, so this type
// does not surface CaravanCatalog.pawns.
type CaravanCatalogRead struct {
	Context     *c.ObservationContext
	Token       string
	CargoGroups []*o.CargoGroup
	Routes      []*o.WorldRoute
}

// ReadCaravanCatalog reads the native FormCaravan catalog for one destination
// tile (NativeCaravanCatalog.BuildDialog: no window opens, no camera moves).
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

func finiteNonnegative(v float64) bool { return !math.IsNaN(v) && !math.IsInf(v, 0) && v >= 0 }

// caravanCargoGroupValid checks one cargo row's shape and its optional food
// facts: nutrition, when present, is finite and positive; rot_days, when
// present, is finite, nonnegative and only reported for a perishable group;
// eater ids are valid and unique.
func caravanCargoGroupValid(group *o.CargoGroup) bool {
	if group == nil || validID(group.GetGroupId()) != nil || validID(group.GetDefName()) != nil || group.Count == nil || group.GetCount() < 0 {
		return false
	}
	if group.Nutrition != nil && (!finiteNonnegative(group.GetNutrition()) || group.GetNutrition() == 0) {
		return false
	}
	if group.RotDays != nil && (!finiteNonnegative(group.GetRotDays()) || !group.GetPerishable()) {
		return false
	}
	if len(group.EaterIds) > 256 {
		return false
	}
	seen := make(map[string]bool, len(group.EaterIds))
	for _, eater := range group.EaterIds {
		if validID(eater) != nil || seen[eater] {
			return false
		}
		seen[eater] = true
	}
	return true
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
		if !caravanCargoGroupValid(group) || seenGroups[group.GetGroupId()] || seenDefs[group.GetDefName()] {
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
