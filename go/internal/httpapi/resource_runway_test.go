package httpapi

import (
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"strings"
	"testing"
)

func TestRoutineResourceRunwayJSON(t *testing.T) {
	r := routineStatus(RoutineStatus{ResourceRunways: []policy.ResourceRunway{{Resource: "Steel", Tick: 60000, DaysLeft: domain.Known(2.5), Deficit: domain.Known(true)}}})
	data, err := json.Marshal(r)
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{`"resourceRunways":[`, `"resource":"Steel"`, `"daysLeft":2.5`, `"surfaceOre":null`, `"deficit":true`, `"thresholdDays":5`} {
		if !strings.Contains(string(data), want) {
			t.Fatal(string(data), want)
		}
	}
}
