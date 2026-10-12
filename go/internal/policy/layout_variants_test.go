package policy

import "testing"

// Every variant is at least the default's floor area and no deeper than the
// core's cross-section, and its role's interior template fits it.
func TestRoomVariantsKeepAreaDepthAndTemplates(t *testing.T) {
	interiorRole := map[PlannedRole]RoomRole{
		PlannedKitchen: RoomRoleKitchen, PlannedDining: RoomRoleDiningRoom,
		PlannedWorkshop: RoomRoleWorkshop, PlannedStorage: RoomRoleStoreroom,
	}
	for role, variants := range coreRoomVariants {
		def := coreRoomSize[role]
		for _, v := range variants {
			if v[0]*v[1] < def[0]*def[1] || v[1] > coreMaxDepth {
				t.Errorf("%s variant %v: area or depth off the default %v", role, v, def)
			}
			// Where the default plans, the variant must too.
			if ir, ok := interiorRole[role]; ok && interiorPlansAt(ir, def) {
				for _, room := range interiorRoomsAround(ir, v[0], v[1], 1) {
					if _, ok := PlanRoomInterior(room, InteriorPieceDef{}); !ok {
						t.Errorf("%s variant %v: no interior plan with doors %v", role, v, room.Doors)
					}
				}
			}
		}
	}
}

// interiorPlansAt reports that role's interior template plans a size on every door.
func interiorPlansAt(role RoomRole, size [2]int32) bool {
	for _, room := range interiorRoomsAround(role, size[0], size[1], 1) {
		if _, ok := PlanRoomInterior(room, InteriorPieceDef{}); !ok {
			return false
		}
	}
	return true
}

// PlanScore prices the weighted walk: a freezer nearer the kitchen scores
// higher, and the term is the weight times planWalk.
func TestPlanScoreChargesWalk(t *testing.T) {
	s := plainScoreSurvey()
	sc := newPlanScorer(Zone(s), nil, s)
	far, near := sc.core(handPlan(50)), sc.core(handPlan(12))
	if near.Walk <= far.Walk {
		t.Fatalf("walk: near %d, far %d", near.Walk, far.Walk)
	}
	if want := -planWeights.Walk * planWalk(handPlan(12)); near.Walk != want {
		t.Fatalf("walk term %d, want %d", near.Walk, want)
	}
}
