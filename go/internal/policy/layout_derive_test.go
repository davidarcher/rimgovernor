package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

func TestDeriveAndReplanLayoutPlan(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	s := zoningSurvey(200, open)
	plan, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 30).Value()
	if !ok || !plan.Valid() || plan.LayoutOutgrown(3) {
		t.Fatal(ok, plan.Valid())
	}
	for _, z := range plan.Zones {
		if z.Kind == ZoneCore {
			t.Fatal("core candidates saved")
		}
	}
	if sum := plan.Summary(); !strings.Contains(sum, "bedroom:10") || !strings.Contains(sum, "routes=ok") || !strings.Contains(sum, "perimeter_wall:") {
		t.Fatal(sum)
	}
	if _, changed := replanTest(plan, s, 3, 1, BuildTierCamp, nil, nil); changed {
		t.Fatal("a sound plan replanned")
	}
	tombs, changed := replanTest(plan, s, 3, 2, BuildTierCamp, nil, nil)
	if !changed || tombs.TombRooms() != 2 || plan.TombRooms() != 1 {
		t.Fatal("a full tomb grew no second one", changed, tombs.TombRooms())
	}
	grown, changed := replanTest(plan, s, 11, 1, BuildTierCamp, nil, nil)
	if !changed || grown.LayoutOutgrown(11) {
		t.Fatal(changed)
	}
	for i, r := range plan.Rooms {
		if !grown.Rooms[i].Same(r) {
			t.Fatal("a grown plan moved a room", i)
		}
	}
}

// The perimeter replans when the ground under the ring dries; its own walls
// standing on the ring change nothing; the opening and rooms stay (#954).
func TestReplanPerimeterOnDriedGround(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	survey := func(dried bool, walls map[domain.Cell]bool) MapSurvey {
		return zoningSurvey(200, func(x, z int32) SurveyCell {
			c := SurveyCell{Walkable: true, Fertility: 1}
			// Marsh bands closer than the ring is tall: it must cross one.
			if z%40 < 4 && !dried {
				c = marsh
			}
			if walls[domain.Cell{X: x, Z: z}] {
				c.Walkable, c.Built = false, true
			}
			return c
		})
	}
	plan, ok := DeriveLayoutPlan(survey(false, nil), 3, BuildTierCamp, nil, 30).Value()
	if !ok || len(reserved(plan, ReservePerimeterLight)) == 0 || len(reserved(plan, ReserveMoisturePump)) == 0 {
		t.Fatal("no wooden stretch", ok)
	}
	walls := reservedCells(plan, ReservePerimeter)
	if _, changed := replanTest(plan, survey(false, walls), 3, 1, BuildTierCamp, nil, nil); changed {
		t.Fatal("standing walls replanned the perimeter")
	}
	next, changed := replanTest(plan, survey(true, walls), 3, 1, BuildTierCamp, nil, nil)
	if !changed || len(reserved(next, ReservePerimeterLight)) != 0 || len(reserved(next, ReserveMoisturePump)) != 0 {
		t.Fatal("dried ground kept its wooden wall", changed)
	}
	if kb, was := reserved(next, ReserveKillbox), reserved(plan, ReserveKillbox); len(kb) != 1 || kb[0] != was[0] {
		t.Fatal("the opening moved", kb, was)
	}
	for i, r := range plan.Rooms {
		if !next.Rooms[i].Same(r) {
			t.Fatal("a perimeter replan moved a room", i)
		}
	}
}
