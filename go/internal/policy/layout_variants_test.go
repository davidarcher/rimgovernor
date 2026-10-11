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
					if _, ok := PlanInterior(room, InteriorPieceDef{}); !ok {
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
		if _, ok := PlanInterior(room, InteriorPieceDef{}); !ok {
			return false
		}
	}
	return true
}

// roomSize is the default until the search picks a variant.
func TestRoomSizeDefaultsAndPicksVariants(t *testing.T) {
	g := coreGrid{}
	if got := g.roomSize(PlannedKitchen); got != coreRoomSize[PlannedKitchen] {
		t.Fatalf("default kitchen %v", got)
	}
	g.sizes = map[PlannedRole]int{PlannedKitchen: 1, PlannedFreezer: 3}
	if got := g.roomSize(PlannedKitchen); got != coreRoomVariants[PlannedKitchen][0] {
		t.Fatalf("variant kitchen %v", got)
	}
	if got := g.roomSize(PlannedFreezer); got != coreRoomSize[PlannedFreezer] {
		t.Fatalf("freezer has no variants, got %v", got)
	}
}

// The local search moves a cluster back in other proportions: over many
// moves the kitchen comes back in both of its shapes, and every plan stays
// routable with all its base rooms.
func TestMoveClusterReproportionsRooms(t *testing.T) {
	g := newCoreGrid(coreTestZones(), nil)
	seed, ok := g.seed()
	if !ok {
		t.Fatal("no seed")
	}
	base := g.generateBase(LayoutPlan{Zones: coreTestZones()}, seed, 3, 0, TechTierCamp)
	shapes := map[[2]int32]bool{}
	for i := range 300 {
		rng := searchRand(i + 1)
		cand, ok := g.moveCluster(base, &rng)
		if !ok {
			continue
		}
		for _, r := range cand.Rooms {
			if r.Role == PlannedKitchen {
				shapes[[2]int32{r.Region.Bounds().Width, r.Region.Bounds().Height}] = true
			}
		}
		if _, err := CheckRoutes(finishForTest(g, cand)); err != nil {
			t.Fatalf("move %d: routes: %v", i, err)
		}
	}
	for _, want := range [][2]int32{coreRoomSize[PlannedKitchen], coreRoomVariants[PlannedKitchen][0]} {
		if !shapes[want] {
			t.Errorf("kitchen never came back %v; saw %v", want, shapes)
		}
	}
}

func finishForTest(g coreGrid, p LayoutPlan) LayoutPlan { return g.finish(routedEntrances(p)) }

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
