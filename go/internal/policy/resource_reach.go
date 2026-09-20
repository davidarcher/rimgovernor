package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type ResourceReachStage string

const (
	ResourceReachBase ResourceReachStage = "base"
	ResourceReachNear ResourceReachStage = "near"
	ResourceReachFar  ResourceReachStage = "far"
	ResourceReachMap  ResourceReachStage = "map"
)

// ResourceReachRequest contains current-map observations, not forecasts. Unknown
// readiness never widens reach. FreeHaulers counts available hauling workers;
// StorageHeadroom counts free item units accepting the resource being considered.
type ResourceReachRequest struct {
	Extent                              domain.Fact[ColonyExtent]
	Bounds                              domain.Fact[Bounds]
	Threat                              domain.Fact[bool]
	RaidPoints                          domain.Fact[float64]
	Armed, FreeHaulers, StorageHeadroom domain.Fact[int64]
	StorytellerQuiet                    domain.Fact[bool]
}

type ResourceReachDecision struct {
	Stage  ResourceReachStage `json:"stage"`
	Reason string             `json:"reason"`
}

// ResourceReach is a candidate-selection ceiling, never dispatch authority.
// Two armed colonists support near work; six support far work. Raid points
// above 100 per armed colonist hold outside work. Two free haulers permit far
// work and three permit map-wide consideration, only with a quiet storyteller.
func ResourceReach(r ResourceReachRequest) ResourceReachDecision {
	base := func(reason string) ResourceReachDecision { return ResourceReachDecision{ResourceReachBase, reason} }
	threat, tk := r.Threat.Value()
	if tk && threat {
		return base("threat_present")
	}
	e, ek := r.Extent.Value()
	if !ek {
		return base("extent_unknown")
	}
	if !resourceExtentPresent(e) {
		return base("extent_empty")
	}
	b, bk := r.Bounds.Value()
	if !bk || b.Width <= 0 || b.Height <= 0 {
		return base("bounds_unknown")
	}
	if !tk {
		return base("threat_unknown")
	}
	armed, ak := r.Armed.Value()
	if !ak {
		return base("armed_unknown")
	}
	if armed < 2 {
		return base("insufficient_defense")
	}
	points, pk := r.RaidPoints.Value()
	if !pk {
		return base("raid_points_unknown")
	}
	if math.IsNaN(points) || math.IsInf(points, 0) || points < 0 {
		return base("raid_points_invalid")
	}
	if points > float64(armed)*100 {
		return base("raid_points_above")
	}
	haulers, hk := r.FreeHaulers.Value()
	if !hk {
		return base("hauler_capacity_unknown")
	}
	if haulers <= 0 {
		return base("no_hauler_capacity")
	}
	storage, sk := r.StorageHeadroom.Value()
	if !sk {
		return base("storage_unknown")
	}
	if storage <= 0 {
		return base("no_storage")
	}
	near := func(reason string) ResourceReachDecision { return ResourceReachDecision{ResourceReachNear, reason} }
	if armed < 6 {
		return near("defense_limits_near")
	}
	if haulers < 2 {
		return near("hauler_capacity_limits_near")
	}
	quiet, qk := r.StorytellerQuiet.Value()
	if !qk {
		return near("storyteller_unknown")
	}
	if !quiet {
		return near("storyteller_not_quiet")
	}
	if haulers < 3 {
		return ResourceReachDecision{ResourceReachFar, "ready_far"}
	}
	return ResourceReachDecision{ResourceReachMap, "ready_map"}
}

func resourceExtentPresent(e ColonyExtent) bool {
	for _, region := range e.Regions {
		if len(region.Cells) > 0 {
			return true
		}
	}
	return false
}

// ResourceReachCandidate is the narrow boundary for an eligibility view (#518).
// Eligible is its complete safety/permission verdict; RouteObservedPassable is
// native route evidence for this candidate. Neither defaults to permission.
type ResourceReachCandidate struct {
	Cell                            domain.Cell
	Eligible, RouteObservedPassable domain.Fact[bool]
}

type ResourceReachFilterDecision struct {
	Allowed bool   `json:"allowed"`
	Reason  string `json:"reason"`
}

// FilterResourceReach intersects readiness, exact extent geometry and observed
// eligibility. The near margin is 12 Chebyshev cells, never an inferred route.
// Even base/map candidates require positive route and eligibility observations.
func FilterResourceReach(r ResourceReachRequest, c ResourceReachCandidate) ResourceReachFilterDecision {
	deny := func(reason string) ResourceReachFilterDecision { return ResourceReachFilterDecision{false, reason} }
	eligible, known := c.Eligible.Value()
	if !known {
		return deny("eligibility_unknown")
	}
	if !eligible {
		return deny("ineligible")
	}
	route, known := c.RouteObservedPassable.Value()
	if !known {
		return deny("route_unknown")
	}
	if !route {
		return deny("route_impassable")
	}
	b, known := r.Bounds.Value()
	if !known || b.Width <= 0 || b.Height <= 0 {
		return deny("bounds_unknown")
	}
	if c.Cell.X < 0 || c.Cell.Z < 0 || c.Cell.X >= b.Width || c.Cell.Z >= b.Height {
		return deny("outside_map")
	}
	e, known := r.Extent.Value()
	if !known {
		return deny("extent_unknown")
	}
	if !resourceExtentPresent(e) {
		return deny("extent_empty")
	}
	d := ResourceReach(r)
	for _, region := range e.Regions {
		for _, cell := range region.Cells {
			dx, dz := int64(c.Cell.X)-int64(cell.Cell.X), int64(c.Cell.Z)-int64(cell.Cell.Z)
			if dx == 0 && dz == 0 || d.Stage == ResourceReachNear && dx >= -12 && dx <= 12 && dz >= -12 && dz <= 12 {
				return ResourceReachFilterDecision{true, d.Reason}
			}
		}
	}
	if d.Stage == ResourceReachFar || d.Stage == ResourceReachMap {
		return ResourceReachFilterDecision{true, d.Reason}
	}
	return deny("outside_" + string(d.Stage) + ":" + d.Reason)
}
