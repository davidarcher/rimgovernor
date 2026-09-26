package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// surveyMap builds a w x h survey from a cell classifier.
func surveyMap(w, h int32, at func(x, z int32) SurveyCell) MapSurvey {
	s := MapSurvey{Bounds: Bounds{Width: w, Height: h}}
	for z := int32(0); z < h; z++ {
		for x := int32(0); x < w; x++ {
			c := at(x, z)
			c.Cell = domain.Cell{X: x, Z: z}
			s.Cells = append(s.Cells, c)
		}
	}
	return s
}

func planRoles(p MasterPlan) map[ModuleRole]int {
	out := map[ModuleRole]int{}
	for _, m := range p.Modules {
		out[m.Role]++
	}
	return out
}

func TestMasterPlanAvoidsMarshAndEdgeAndReservesEveryRole(t *testing.T) {
	// Marsh covers the west half; the plan must sit east of it.
	s := surveyMap(200, 200, func(x, z int32) SurveyCell {
		return SurveyCell{Walkable: true, Marsh: x < 90, Fertility: 1}
	})
	plan, ok := DeriveMasterPlan(s, 10).Value()
	if !ok {
		t.Fatal("no plan")
	}
	plaza := plan.Plaza()
	if plaza.X < 90 || plaza.X+plaza.Width > 200-MasterPlanEdgeMargin || plaza.Z < MasterPlanEdgeMargin {
		t.Fatalf("plaza %+v on marsh or edge", plaza)
	}
	roles := planRoles(plan)
	for _, r := range []ModuleRole{ModulePlaza, ModuleStorage, ModuleFreezer, ModuleKitchen, ModuleWorkshop, ModuleFields, ModuleHospital, ModuleHousing, ModulePrison} {
		if roles[r] == 0 {
			t.Fatalf("no %s module: %v", r, roles)
		}
	}
	if again, _ := DeriveMasterPlan(s, 10).Value(); again.Grid != plan.Grid {
		t.Fatal("not deterministic")
	}
}

func TestMasterPlanFreezerSitsBesideStorageAndKitchen(t *testing.T) {
	s := surveyMap(160, 160, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true} })
	plan, _ := DeriveMasterPlan(s, 6).Value()
	at := map[[2]int32]ModuleRole{}
	for _, m := range plan.Modules {
		at[[2]int32{m.U, m.V}] = m.Role
	}
	for _, m := range plan.Modules {
		if m.Role != ModuleFreezer {
			continue
		}
		near := map[ModuleRole]bool{}
		for _, d := range [4][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
			near[at[[2]int32{m.U + d[0], m.V + d[1]}]] = true
		}
		if !near[ModuleStorage] || !near[ModuleKitchen] {
			t.Fatalf("freezer at %d,%d beside %v", m.U, m.V, near)
		}
	}
}

func TestMasterPlanPrefersAMountainBackedSite(t *testing.T) {
	// A mountain fills the north; a site against it seals the north ring.
	s := surveyMap(200, 200, func(x, z int32) SurveyCell {
		if z >= 120 {
			return SurveyCell{Rock: true, ThickRoof: true}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})
	plan, _ := DeriveMasterPlan(s, 10).Value()
	if planRoles(plan)[ModuleWall] == 0 {
		t.Fatalf("no sealed ring module: %v at %+v", planRoles(plan), plan.Grid)
	}
}

func TestMasterPlanAnchorAndReplan(t *testing.T) {
	s := surveyMap(160, 160, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, _ := DeriveMasterPlan(s, 6).Value()
	if _, ok := plan.Anchor(ModuleFor(RoomRoleKitchen), nil); !ok {
		t.Fatal("no kitchen slot")
	}
	if plan.ReplanNeeded(s, 6) {
		t.Fatal("a sound plan asked to replan")
	}
	if !plan.ReplanNeeded(s, 60) {
		t.Fatal("outgrown housing did not trigger")
	}
	grown := plan.Replan(s, 60)
	if grown.Grid != plan.Grid || grown.Radius != plan.Radius+1 {
		t.Fatalf("replan moved the grid or did not widen: %+v", grown.Grid)
	}
	kept := map[[2]int32]ModuleRole{}
	for _, m := range grown.Modules {
		kept[[2]int32{m.U, m.V}] = m.Role
	}
	for _, m := range plan.Modules {
		if m.Role != ModuleReserve && max(m.U, -m.U, m.V, -m.V) < plan.Radius && kept[[2]int32{m.U, m.V}] != m.Role {
			t.Fatalf("replan moved %d,%d from %s to %s", m.U, m.V, m.Role, kept[[2]int32{m.U, m.V}])
		}
	}
}
