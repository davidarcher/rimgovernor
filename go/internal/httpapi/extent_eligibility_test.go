package httpapi

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRoutineExtentEligibilityJSON(t *testing.T) {
	r := RoutineStatus{ExtentEligibility: policy.ExtentEligibilityRequest{
		Extent:     domain.Known(policy.ColonyExtent{Regions: []policy.ExtentRegion{{Cells: []policy.ExtentCell{{Provenance: []policy.ExtentProvenance{{Origin: policy.ExtentFacility, Facility: "bed"}}}}}}}),
		Facilities: domain.Known([]string{}), Threat: domain.Known(true),
	}}
	b, err := json.Marshal(routineStatus(r))
	if err != nil {
		t.Fatal(err)
	}
	want := `"extentEligibility":{"known":true,"reason":"","regions":[{"region":0,"stage":"established","cells":1,"origins":["facility"],"facilities":["bed"],"activeFacilities":[],"eligible":false,"holdReasons":["threat_present","facility_lost","route_unknown"]}]}`
	if !strings.Contains(string(b), want) {
		t.Fatalf("missing %s in %s", want, b)
	}
}
