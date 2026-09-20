package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestExtentEligibilityCurrentEvidence(t *testing.T) {
	e := ColonyExtent{Regions: []ExtentRegion{{Cells: []ExtentCell{{Cell: domain.Cell{X: 1, Z: 1}, Provenance: []ExtentProvenance{{Origin: ExtentFacility, Facility: "bed", Plan: "plan", Action: "build", Goal: "sleep"}}}}}}}
	r := ExtentEligibilityRequest{Extent: domain.Known(e), Facilities: domain.Known([]string{"bed"}), Threat: domain.Known(false), Regions: map[int]ExtentRegionObservation{0: {RouteObservedPassable: domain.Known(true)}}}
	for _, tt := range []struct {
		name   string
		edit   func(*ExtentEligibilityRequest)
		reason string
	}{
		{"ready", func(*ExtentEligibilityRequest) {}, ""},
		{"threat", func(r *ExtentEligibilityRequest) { r.Threat = domain.Known(true) }, "threat_present"},
		{"lost", func(r *ExtentEligibilityRequest) { r.Facilities = domain.Known([]string{}) }, "facility_lost"},
		{"unknown safety", func(r *ExtentEligibilityRequest) { r.Threat = domain.Unknown[bool]() }, "threat_unknown"},
		{"unknown facilities", func(r *ExtentEligibilityRequest) { r.Facilities = domain.Unknown[[]string]() }, "facilities_unknown"},
		{"unknown route", func(r *ExtentEligibilityRequest) { r.Regions = nil }, "route_unknown"},
		{"blocked route", func(r *ExtentEligibilityRequest) {
			r.Regions = map[int]ExtentRegionObservation{0: {RouteObservedPassable: domain.Known(false)}}
		}, "route_impassable"},
	} {
		t.Run(tt.name, func(t *testing.T) {
			q := r
			tt.edit(&q)
			v := ExtentEligibility(q)
			row := v.Regions[0]
			if row.Eligible != (tt.reason == "") || tt.reason != "" && !reflect.DeepEqual(row.HoldReasons, []string{tt.reason}) {
				t.Fatal(row)
			}
			if tt.reason == "facility_lost" && (len(row.ActiveFacilities) != 0 || !reflect.DeepEqual(row.Facilities, []string{"bed"})) {
				t.Fatal(row)
			}
			row.Facilities[0] = "mutated"
			if e.Regions[0].Cells[0].Provenance[0].Facility != "bed" {
				t.Fatal("history mutated")
			}
		})
	}
	if !ExtentEligibility(r).Regions[0].Eligible {
		t.Fatal("temporary hold survived recovered evidence")
	}
}

func TestExtentEligibilityRegionThreats(t *testing.T) {
	r := ExtentEligibilityRequest{Extent: domain.Known(ColonyExtent{Regions: []ExtentRegion{
		{Cells: []ExtentCell{{Provenance: []ExtentProvenance{{Origin: ExtentFacility, Facility: "a"}}}}},
		{Cells: []ExtentCell{{Provenance: []ExtentProvenance{{Origin: ExtentFacility, Facility: "b"}}}}},
	}}), Facilities: domain.Known([]string{"a", "b"}), Regions: map[int]ExtentRegionObservation{
		0: {Threat: domain.Known(true), RouteObservedPassable: domain.Known(true)},
		1: {Threat: domain.Known(false), RouteObservedPassable: domain.Known(true)},
	}}
	v := ExtentEligibility(r)
	if v.Regions[0].Eligible || !v.Regions[1].Eligible {
		t.Fatal(v)
	}
	if v := ExtentEligibility(ExtentEligibilityRequest{}); v.Known || v.Reason != "extent_unknown" {
		t.Fatal(v)
	}
}
