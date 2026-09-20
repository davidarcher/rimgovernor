package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// BundlePlanningWindow is the planning window band a bundle request asks
// for: the region and, for a held window, the as-of tick the delta is
// taken since (zero reads every cell).
type BundlePlanningWindow struct {
	Region policy.Rectangle
	Since  int64
}

// BundleStepAsks names the step families a review step's planners asked
// for through its read cache (#593), hit or miss, each in the request
// shape the bundle can answer. The scheduler folds them into the next
// review step's bundle request so they ride its one hop; a family the
// planners stop asking for leaves the bundle a step later.
type BundleStepAsks struct {
	BuiltBuildings, Traders, WorldProgression bool
	Resources                                 []string
	PlanningWindow                            *BundlePlanningWindow
}

// Empty reports whether nothing was asked.
func (a BundleStepAsks) Empty() bool {
	return !a.BuiltBuildings && !a.Traders && !a.WorldProgression && len(a.Resources) == 0 && a.PlanningWindow == nil
}

// StepAsks decodes the step families among the reads asked through this
// cache. A read in another shape (a page cursor, exact building ids,
// storage included) is not one the bundle can carry and is left out.
func (s *StepReadCache) StepAsks() BundleStepAsks {
	if s == nil {
		return BundleStepAsks{}
	}
	s.mu.Lock()
	asked := append([]readCacheKey(nil), s.asked...)
	s.mu.Unlock()
	var asks BundleStepAsks
	seen := map[string]bool{}
	for _, key := range asked {
		switch key.method {
		case "rimgovernor/observations_list_buildings":
			request := &o.ListBuildingsRequest{}
			if proto.Unmarshal([]byte(key.request), request) == nil && sameRequest(request, constructionBuildingsRequest(request.GetScope().GetExpectedIdentity(), nil)) {
				asks.BuiltBuildings = true
			}
		case "rimgovernor/observations_list_traders":
			request := &o.TradersRequest{}
			if proto.Unmarshal([]byte(key.request), request) == nil && sameRequest(request, tradersRequest(request.GetScope().GetExpectedIdentity())) {
				asks.Traders = true
			}
		case "rimgovernor/observations_read_world_progression":
			request := &o.WorldProgressionRequest{}
			if proto.Unmarshal([]byte(key.request), request) == nil && sameRequest(request, worldProgressionRequest(request.GetScope().GetExpectedIdentity(), false)) {
				asks.WorldProgression = true
			}
		case "rimgovernor/observations_list_resource_sources":
			request := &o.ResourceSourcesRequest{}
			if proto.Unmarshal([]byte(key.request), request) == nil && validID(request.GetResource()) == nil && sameRequest(request, resourceSourcesRequest(request.GetScope().GetExpectedIdentity(), request.GetResource())) && !seen[request.GetResource()] {
				seen[request.GetResource()] = true
				asks.Resources = append(asks.Resources, request.GetResource())
			}
		case "rimgovernor/observations_get_cells":
			request := &o.GetCellsRequest{}
			if proto.Unmarshal([]byte(key.request), request) != nil {
				continue
			}
			rect := request.GetRectangle()
			if rect == nil {
				continue
			}
			region := policy.Rectangle{X: rect.GetMinimum().GetX(), Z: rect.GetMinimum().GetZ(), Width: rect.GetMaximum().GetX() - rect.GetMinimum().GetX() + 1, Height: rect.GetMaximum().GetZ() - rect.GetMinimum().GetZ() + 1}
			if sameRequest(request, planningBandRequest(request.GetScope().GetExpectedIdentity(), region, request.GetChangedSinceTick())) {
				// The last band asked wins: a window that moved is asked
				// for at its new place.
				asks.PlanningWindow = &BundlePlanningWindow{Region: region, Since: request.GetChangedSinceTick()}
			}
		}
	}
	return asks
}

func sameRequest(a, b proto.Message) bool {
	return proto.Equal(a, b)
}

// bundlePlanningBand is the one band a bundle's planning window is read
// as: the whole region when it fits one band, else none (the refresher
// then bands the read itself, natively).
func bundlePlanningBand(window BundlePlanningWindow) (policy.Rectangle, bool) {
	rect := window.Region
	if rect.X < 0 || rect.Z < 0 || rect.Width < 1 || rect.Height < 1 || int64(rect.Width)*int64(rect.Height) > planningWindowPage {
		return policy.Rectangle{}, false
	}
	return rect, true
}

// BundlePlanningWindowRequest is the request section for a planning
// window band, or nil when the region cannot ride the bundle.
func BundlePlanningWindowRequest(window *BundlePlanningWindow) *o.BundlePlanningWindowRequest {
	if window == nil {
		return nil
	}
	band, ok := bundlePlanningBand(*window)
	if !ok {
		return nil
	}
	request := &o.BundlePlanningWindowRequest{Region: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(band.X), Z: proto.Int32(band.Z)}, Maximum: &c.Cell{X: proto.Int32(band.X + band.Width - 1), Z: proto.Int32(band.Z + band.Height - 1)}}}
	if window.Since > 0 {
		request.ChangedSinceTick = proto.Int64(window.Since)
	}
	return request
}

// validateBundleStepFamilies checks the step families a bundle carries
// against the request: none unrequested, each under the bundle's context.
func validateBundleStepFamilies(request *o.BundleRequest, v *o.BundleSnapshot) error {
	if !request.GetBuildings() && v.Buildings != nil || !request.GetBuiltBuildings() && v.BuiltBuildings != nil || !request.GetBills() && v.Bills != nil || !request.GetZones() && v.Zones != nil || !request.GetTraders() && v.Traders != nil || !request.GetWorldProgression() && v.WorldProgression != nil || request.PlanningWindow == nil && v.PlanningWindow != nil {
		return contract("bundle carries an unrequested step family")
	}
	requested := map[string]bool{}
	for _, resource := range request.ResourceSources {
		requested[resource] = true
	}
	contexts := []*c.ObservationContext{v.Buildings.GetContext(), v.BuiltBuildings.GetContext(), v.Bills.GetContext(), v.Zones.GetContext(), v.Traders.GetContext(), v.WorldProgression.GetContext(), v.PlanningWindow.GetContext()}
	present := []bool{v.Buildings != nil, v.BuiltBuildings != nil, v.Bills != nil, v.Zones != nil, v.Traders != nil, v.WorldProgression != nil, v.PlanningWindow != nil}
	for _, sources := range v.ResourceSources {
		if sources == nil || !requested[sources.GetResource()] {
			return contract("bundle carries an unrequested resource family")
		}
		contexts, present = append(contexts, sources.GetContext()), append(present, true)
	}
	for i, context := range contexts {
		if !present[i] {
			continue
		}
		if err := ValidateContext(context); err != nil {
			return err
		}
		if !sameIdentity(context.Identity, v.Context.Identity) || context.GetTick() != v.Context.GetTick() {
			return contract("bundle step family context mismatch")
		}
	}
	return nil
}

// seedBundleStepFamilies files the step families under the keys their
// dedicated reads use, as seedBundle does the census families. An entity
// section rides as the full first page (no since tick, no cursor); the
// planning window rides as the band the request named, delta or full.
func (client *Client) seedBundleStepFamilies(ctx context.Context, request *o.BundleRequest, v *o.BundleSnapshot, seed func(method string, request, reply proto.Message)) {
	identity := v.Context.Identity
	if v.Buildings != nil {
		seed("rimgovernor/observations_list_buildings", buildingsListRequest(identity, 0, ""), &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: v.Buildings}})
	}
	if v.BuiltBuildings != nil {
		seed("rimgovernor/observations_list_buildings", constructionBuildingsRequest(identity, nil), &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: v.BuiltBuildings}})
	}
	if v.Bills != nil {
		seed("rimgovernor/observations_read_bills", billsListRequest(identity, 0, ""), &o.BillsReply{Outcome: &o.BillsReply_Observed{Observed: v.Bills}})
	}
	if v.Zones != nil {
		seed("rimgovernor/observations_list_zones", zoneSectionRequest(identity, 0, ""), &o.ListZonesReply{Outcome: &o.ListZonesReply_Observed{Observed: v.Zones}})
	}
	if v.Traders != nil {
		seed("rimgovernor/observations_list_traders", tradersRequest(identity), &o.TradersReply{Outcome: &o.TradersReply_Observed{Observed: v.Traders}})
	}
	if v.WorldProgression != nil {
		seed("rimgovernor/observations_read_world_progression", worldProgressionRequest(identity, false), &o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: v.WorldProgression}})
	}
	for _, sources := range v.ResourceSources {
		seed("rimgovernor/observations_list_resource_sources", resourceSourcesRequest(identity, sources.GetResource()), &o.ResourceSourcesReply{Outcome: &o.ResourceSourcesReply_Observed{Observed: sources}})
	}
	if window := request.PlanningWindow; window != nil && v.PlanningWindow != nil {
		rect := window.GetRegion()
		region := policy.Rectangle{X: rect.GetMinimum().GetX(), Z: rect.GetMinimum().GetZ(), Width: rect.GetMaximum().GetX() - rect.GetMinimum().GetX() + 1, Height: rect.GetMaximum().GetZ() - rect.GetMinimum().GetZ() + 1}
		if band, ok := bundlePlanningBand(BundlePlanningWindow{Region: region}); ok {
			seed("rimgovernor/observations_get_cells", planningBandRequest(identity, band, window.GetChangedSinceTick()), &o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: v.PlanningWindow}})
		}
	}
}
