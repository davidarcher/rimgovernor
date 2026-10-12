package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The old generator's core: a three-cell-wide main hallway along X, crossed by
// north-south hallways as it grows. Rooms hang off both sides and share side
// walls; every room's door sits in its hallway wall, so no room is a
// thoroughfare. Only the generator (SiteCore, test scaffolding, #2932) still
// runs on it; the ground it reads is buildGround (layout_build_ground.go).

// coreRoomSize is a role's interior: width along the spine, depth away
// from it. Bedrooms live in the wing (layout_wing.go).
var coreRoomSize = map[PlannedRole][2]int32{
	PlannedKitchen:  {6, 5},
	PlannedFreezer:  {5, 5},
	PlannedButchery: {4, 4},
	PlannedDining:   {9, 7},
	PlannedRec:      {9, 7},
	PlannedHospital: {7, 5},
	PlannedPrison:   {5, 5},
	PlannedWorkshop: {7, 5},
	PlannedStorage:  {15, 7},
	PlannedLab:      {11, 6},
	PlannedTomb:     {5, 5},
	PlannedMorgue:   {5, 4},
	// The rooms below are added on demand (layout_demand_rooms.go,
	// layout_gear.go), not in coreBaseRooms. The tomb and morgue above are
	// sized here and sited in the outskirts cluster.
	PlannedArmory:   {7, 5},
	PlannedWardrobe: {7, 5},
	PlannedBattery:  batteryRoomSize,
}

// coreBaseRooms is the set the generator places first, in placement order:
// pairs that trade goods sit side by side. Each room takes the nearest free
// slot, so order is centrality: dining lands near the centre. The bedroom
// wings are sited with them.
var coreBaseRooms = defaultOwedRooms

// coreGrid is the generator's ground: the build ground plus the state its
// search and packer keep.
type coreGrid struct {
	buildGround
	// fixed are the interiors of the rooms a replan must not move or change:
	// the search operators and the second-door pass leave them be.
	fixed map[int32]bool
	// skip are the room interiors the packer refuses: the slots SiteRoom has scored.
	skip map[Rectangle]bool
	// sizes picks a variant (coreRoomVariants) per role; a role absent keeps
	// its coreRoomSize. Only the local search sets it.
	sizes map[PlannedRole]int
	// noShelter keeps the generator from siting a shelter: a replan never
	// regrows one a retirement dropped.
	noShelter bool
	// linkBehind lets a linked room take its neighbour's back wall when its
	// sides are taken (packer.against): set by a successor that keeps a Link.
	linkBehind bool
}

// newCoreGrid takes reserved sites out of the core candidates.
func newCoreGrid(zones []LayoutZone, reserved []LayoutReservation) coreGrid {
	return coreGrid{buildGround: newBuildGround(zones, reserved)}
}

// withSoil gives g a per-cell build cost from the survey's fertility.
func (g coreGrid) withSoil(s MapSurvey) coreGrid {
	g.buildGround = g.buildGround.withSoil(s)
	return g
}

// withObstacles is a copy of g whose core lacks obs, so slot search, the
// hallway and the wings all keep off them.
func (g coreGrid) withObstacles(obs map[domain.Cell]bool) coreGrid {
	g.buildGround = g.buildGround.without(obs)
	return g
}

// coreRoomVariants are the other proportions a base-cluster room may take:
// the same floor area or a little more, never deeper than coreMaxDepth. A
// role listed here is sized by roomSize; coreRoomSize stays variant 0, and
// the sizes of rooms grown on demand.
var coreRoomVariants = map[PlannedRole][][2]int32{
	PlannedKitchen:  {{5, 6}},
	PlannedDining:   {{11, 6}},
	PlannedWorkshop: {{5, 7}, {6, 6}},
	PlannedStorage:  {{18, 6}},
}

// roomSize is role's interior on g: coreRoomSize, or the variant g.sizes
// picks for it (1 is the first of coreRoomVariants).
func (g coreGrid) roomSize(role PlannedRole) [2]int32 {
	if i := g.sizes[role]; i > 0 && i <= len(coreRoomVariants[role]) {
		return coreRoomVariants[role][i-1]
	}
	return coreRoomSize[role]
}
