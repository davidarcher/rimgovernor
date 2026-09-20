package policy

import (
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func tribal8Reach() ResourceReachRequest {
	return ResourceReachRequest{
		Extent: domain.Known(ColonyExtent{Regions: []ExtentRegion{{Cells: []ExtentCell{{Cell: domain.Cell{X: 20, Z: 20}}}}}}),
		Bounds: domain.Known(Bounds{Width: 100, Height: 100}), Threat: domain.Known(false),
		RaidPoints: domain.Known(120.0), Armed: domain.Known(int64(2)),
		FreeHaulers: domain.Known(int64(1)), StorageHeadroom: domain.Known(int64(100)), StorytellerQuiet: domain.Known(true),
	}
}

func TestResourceReachReadiness(t *testing.T) {
	for _, tt := range []struct {
		name   string
		edit   func(*ResourceReachRequest)
		stage  ResourceReachStage
		reason string
	}{
		{"early tribal8", func(r *ResourceReachRequest) {}, ResourceReachNear, "defense_limits_near"},
		{"tripled defense idle haulers", func(r *ResourceReachRequest) {
			r.Armed = domain.Known(int64(6))
			r.FreeHaulers = domain.Known(int64(2))
		}, ResourceReachFar, "ready_far"},
		{"map spare haulers", func(r *ResourceReachRequest) {
			r.Armed = domain.Known(int64(6))
			r.FreeHaulers = domain.Known(int64(3))
		}, ResourceReachMap, "ready_map"},
		{"threat", func(r *ResourceReachRequest) { r.Threat = domain.Known(true) }, ResourceReachBase, "threat_present"},
		{"raid pressure", func(r *ResourceReachRequest) { r.RaidPoints = domain.Known(201.0) }, ResourceReachBase, "raid_points_above"},
		{"no haulers", func(r *ResourceReachRequest) { r.FreeHaulers = domain.Known(int64(0)) }, ResourceReachBase, "no_hauler_capacity"},
		{"no storage", func(r *ResourceReachRequest) { r.StorageHeadroom = domain.Known(int64(0)) }, ResourceReachBase, "no_storage"},
		{"unquiet", func(r *ResourceReachRequest) {
			r.Armed = domain.Known(int64(6))
			r.FreeHaulers = domain.Known(int64(3))
			r.StorytellerQuiet = domain.Known(false)
		}, ResourceReachNear, "storyteller_not_quiet"},
		{"unknown quiet", func(r *ResourceReachRequest) {
			r.Armed = domain.Known(int64(6))
			r.FreeHaulers = domain.Known(int64(3))
			r.StorytellerQuiet = domain.Unknown[bool]()
		}, ResourceReachNear, "storyteller_unknown"},
		{"unknown extent", func(r *ResourceReachRequest) { r.Extent = domain.Unknown[ColonyExtent]() }, ResourceReachBase, "extent_unknown"},
		{"empty extent", func(r *ResourceReachRequest) { r.Extent = domain.Known(ColonyExtent{}) }, ResourceReachBase, "extent_empty"},
		{"unknown threat", func(r *ResourceReachRequest) { r.Threat = domain.Unknown[bool]() }, ResourceReachBase, "threat_unknown"},
		{"unknown armed", func(r *ResourceReachRequest) { r.Armed = domain.Unknown[int64]() }, ResourceReachBase, "armed_unknown"},
		{"unknown raid", func(r *ResourceReachRequest) { r.RaidPoints = domain.Unknown[float64]() }, ResourceReachBase, "raid_points_unknown"},
		{"invalid raid", func(r *ResourceReachRequest) { r.RaidPoints = domain.Known(math.NaN()) }, ResourceReachBase, "raid_points_invalid"},
		{"unknown haulers", func(r *ResourceReachRequest) { r.FreeHaulers = domain.Unknown[int64]() }, ResourceReachBase, "hauler_capacity_unknown"},
		{"unknown storage", func(r *ResourceReachRequest) { r.StorageHeadroom = domain.Unknown[int64]() }, ResourceReachBase, "storage_unknown"},
	} {
		t.Run(tt.name, func(t *testing.T) {
			r := tribal8Reach()
			tt.edit(&r)
			got := ResourceReach(r)
			if got.Stage != tt.stage || got.Reason != tt.reason {
				t.Fatal(got)
			}
		})
	}
}

func TestResourceReachCandidateIntersection(t *testing.T) {
	for _, stage := range []ResourceReachStage{ResourceReachBase, ResourceReachNear, ResourceReachFar, ResourceReachMap} {
		t.Run(string(stage), func(t *testing.T) {
			r := tribal8Reach()
			switch stage {
			case ResourceReachBase:
				r.Threat = domain.Known(true)
			case ResourceReachFar:
				r.Armed = domain.Known(int64(6))
				r.FreeHaulers = domain.Known(int64(2))
			case ResourceReachMap:
				r.Armed = domain.Known(int64(6))
				r.FreeHaulers = domain.Known(int64(3))
			}
			for _, tt := range []struct {
				cell    domain.Cell
				allowed bool
			}{
				{domain.Cell{X: 20, Z: 20}, true},
				{domain.Cell{X: 32, Z: 32}, stage != ResourceReachBase},
				{domain.Cell{X: 33, Z: 20}, stage == ResourceReachFar || stage == ResourceReachMap},
				{domain.Cell{X: 100, Z: 20}, false},
			} {
				c := ResourceReachCandidate{Cell: tt.cell, Eligible: domain.Known(true), RouteObservedPassable: domain.Known(true)}
				if got := FilterResourceReach(r, c); got.Allowed != tt.allowed || got.Reason == "" {
					t.Fatalf("%+v: %+v", c, got)
				}
				for _, fact := range []domain.Fact[bool]{domain.Known(false), domain.Unknown[bool]()} {
					c.RouteObservedPassable = fact
					if got := FilterResourceReach(r, c); got.Allowed || !strings.HasPrefix(got.Reason, "route_") {
						t.Fatal(got)
					}
					c.RouteObservedPassable = domain.Known(true)
					c.Eligible = fact
					if got := FilterResourceReach(r, c); got.Allowed {
						t.Fatal(got)
					}
					c.Eligible = domain.Known(true)
				}
			}
		})
	}
}

func TestResourceReachDoesNotBridgeExtentIslands(t *testing.T) {
	r := tribal8Reach()
	r.Extent = domain.Known(ColonyExtent{Regions: []ExtentRegion{
		{Cells: []ExtentCell{{Cell: domain.Cell{X: 10, Z: 10}}}},
		{Cells: []ExtentCell{{Cell: domain.Cell{X: 90, Z: 90}}}},
	}})
	c := ResourceReachCandidate{Cell: domain.Cell{X: 50, Z: 50}, Eligible: domain.Known(true), RouteObservedPassable: domain.Known(true)}
	if got := FilterResourceReach(r, c); got.Allowed {
		t.Fatal(got)
	}
	r.Extent = domain.Unknown[ColonyExtent]()
	if got := FilterResourceReach(r, c); got.Allowed || got.Reason != "extent_unknown" {
		t.Fatal(got)
	}
}
