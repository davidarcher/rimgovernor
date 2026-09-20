package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ExtentEligibilityRequest overlays current, same-world evidence on established
// history. Region observations use the input region index; absent entries are
// unknown. Facilities is a complete census of current building and zone IDs.
// A map-wide threat may conservatively hold every region.
type ExtentEligibilityRequest struct {
	Extent     domain.Fact[ColonyExtent]
	Facilities domain.Fact[[]string]
	Threat     domain.Fact[bool]
	Regions    map[int]ExtentRegionObservation
}

type ExtentRegionObservation struct {
	Threat                domain.Fact[bool]
	RouteObservedPassable domain.Fact[bool]
}

// ExtentEligibilityView is neither Home coverage nor resource-operation reach.
// It contains no write authority and never edits its history or evidence inputs.
type ExtentEligibilityView struct {
	Known   bool                      `json:"known"`
	Reason  string                    `json:"reason"`
	Regions []ExtentRegionEligibility `json:"regions"`
}

type ExtentRegionEligibility struct {
	Region           int            `json:"region"`
	Stage            string         `json:"stage"`
	Cells            int            `json:"cells"`
	Origins          []ExtentOrigin `json:"origins"`
	Facilities       []string       `json:"facilities"`
	ActiveFacilities []string       `json:"activeFacilities"`
	Eligible         bool           `json:"eligible"`
	HoldReasons      []string       `json:"holdReasons"`
}

func ExtentEligibility(r ExtentEligibilityRequest) ExtentEligibilityView {
	e, known := r.Extent.Value()
	v := ExtentEligibilityView{Known: known, Regions: []ExtentRegionEligibility{}}
	if !known {
		v.Reason = "extent_unknown"
		return v
	}
	if len(e.Regions) == 0 {
		v.Reason = "extent_empty"
	}
	facilities, fk := r.Facilities.Value()
	active := map[string]bool{}
	for _, id := range facilities {
		active[id] = true
	}
	for i, region := range e.Regions {
		row := ExtentRegionEligibility{Region: i, Stage: "established", Cells: len(region.Cells), Origins: []ExtentOrigin{}, Facilities: []string{}, ActiveFacilities: []string{}, HoldReasons: []string{}}
		origins, ids := map[ExtentOrigin]bool{}, map[string]bool{}
		for _, cell := range region.Cells {
			for _, p := range cell.Provenance {
				origins[p.Origin] = true
				if p.Facility != "" {
					ids[p.Facility] = true
				}
			}
		}
		for origin := range origins {
			row.Origins = append(row.Origins, origin)
		}
		for id := range ids {
			row.Facilities = append(row.Facilities, id)
			if fk && active[id] {
				row.ActiveFacilities = append(row.ActiveFacilities, id)
			}
		}
		sort.Slice(row.Origins, func(i, j int) bool { return row.Origins[i] < row.Origins[j] })
		sort.Strings(row.Facilities)
		sort.Strings(row.ActiveFacilities)
		obs := r.Regions[i]
		threat, tk := obs.Threat.Value()
		global, gk := r.Threat.Value()
		if gk && global {
			threat, tk = true, true
		} else if !tk {
			threat, tk = global, gk
		}
		if !tk {
			row.HoldReasons = append(row.HoldReasons, "threat_unknown")
		} else if threat {
			row.HoldReasons = append(row.HoldReasons, "threat_present")
		}
		if !fk {
			row.HoldReasons = append(row.HoldReasons, "facilities_unknown")
		} else if len(ids) == 0 {
			row.HoldReasons = append(row.HoldReasons, "provenance_unknown")
		} else if len(row.ActiveFacilities) != len(ids) {
			row.HoldReasons = append(row.HoldReasons, "facility_lost")
		}
		route, rk := obs.RouteObservedPassable.Value()
		if !rk {
			row.HoldReasons = append(row.HoldReasons, "route_unknown")
		} else if !route {
			row.HoldReasons = append(row.HoldReasons, "route_impassable")
		}
		if len(region.Cells) == 0 {
			row.HoldReasons = append(row.HoldReasons, "extent_empty")
		}
		row.Eligible = len(row.HoldReasons) == 0
		v.Regions = append(v.Regions, row)
	}
	return v
}
