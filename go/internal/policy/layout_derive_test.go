package policy

import (
	"strings"
	"testing"
)

func TestDeriveAndReplanLayoutPlan(t *testing.T) {
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	s := zoningSurvey(200, open)
	plan, ok := DeriveLayoutPlan(s, 3).Value()
	if !ok || !plan.Valid() || plan.LayoutOutgrown(3) {
		t.Fatal(ok, plan.Valid())
	}
	for _, z := range plan.Zones {
		if z.Kind == ZoneCore {
			t.Fatal("core candidates saved")
		}
	}
	if sum := plan.Summary(); !strings.Contains(sum, "bedroom:3") || !strings.Contains(sum, "routes=ok") || !strings.Contains(sum, "perimeter_wall:") {
		t.Fatal(sum)
	}
	if _, changed := ReplanLayout(plan, s, 3); changed {
		t.Fatal("a sound plan replanned")
	}
	grown, changed := ReplanLayout(plan, s, 5)
	if !changed || grown.LayoutOutgrown(5) {
		t.Fatal(changed)
	}
	for i, r := range plan.Rooms {
		if grown.Rooms[i] != r {
			t.Fatal("a grown plan moved a room", i)
		}
	}
}
