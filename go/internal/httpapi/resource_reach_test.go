package httpapi

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRoutineResourceReachJSON(t *testing.T) {
	for _, tt := range []struct {
		request policy.ResourceReachRequest
		reason  string
		known   bool
	}{
		{policy.ResourceReachRequest{}, "extent_unknown", false},
		{policy.ResourceReachRequest{Threat: domain.Known(true), Extent: domain.Known(policy.ColonyExtent{})}, "threat_present", true},
	} {
		status := routineStatus(RoutineStatus{ResourceReach: tt.request})
		if status.Extent.Known != tt.known {
			t.Fatal(status.Extent)
		}
		data, err := json.Marshal(status)
		if err != nil {
			t.Fatal(err)
		}
		for _, want := range []string{`"resourceReach":{"stage":"base","reason":"` + tt.reason + `"}`, `"extent":{"known":`} {
			if !strings.Contains(string(data), want) {
				t.Fatalf("missing %s: %s", want, data)
			}
		}
	}
}
