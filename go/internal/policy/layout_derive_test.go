package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

func TestDeriveLayoutPlan(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	s := zoningSurvey(200, open)
	plan, ok := oldDerive(s, 3, TechTierCamp, nil, 30).Value()
	if !ok || !plan.WithRoomIDs().Valid() || (bedroomCount(plan) < 3) {
		t.Fatal(ok, plan.WithRoomIDs().Valid())
	}
	for _, z := range plan.Zones {
		if z.Kind == ZoneCore {
			t.Fatal("core candidates saved")
		}
	}
	if sum := plan.Summary(); !strings.Contains(sum, "bedroom:10") || !strings.Contains(sum, "routes=ok") || !strings.Contains(sum, "perimeter_wall:") {
		t.Fatal(sum)
	}
}
