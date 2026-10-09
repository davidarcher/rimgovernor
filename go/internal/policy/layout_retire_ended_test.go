package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

func endedRoles(pawns domain.Fact[[]WorkPawn], ideology domain.Fact[Ideoligion], demand domain.Fact[ContainmentDemand], owed ...ChildRoomNeed) []PlannedRole {
	return EndedRoomRoles(pawns, ideology, ContainmentPlanning{Demand: demand}, IsolationPlanning{Pawns: domain.Known([]PawnID(nil))}, owed)
}

// The isolation room ends only when the isolated census is read and empty.
func TestEndedRoomRolesIsolation(t *testing.T) {
	pawns, ideo, none := domain.Known([]WorkPawn(nil)), domain.Known(Ideoligion{}), domain.Known(ContainmentDemand{})
	ended := func(isolation IsolationPlanning, owed ...ChildRoomNeed) bool {
		return slices.Contains(EndedRoomRoles(pawns, ideo, ContainmentPlanning{Demand: none}, isolation, owed), PlannedIsolationRoom)
	}
	if !ended(IsolationPlanning{Pawns: domain.Known([]PawnID(nil))}) {
		t.Error("isolation room not ended with no isolated creepjoiner")
	}
	if ended(IsolationPlanning{Pawns: domain.Unknown[[]PawnID]()}) {
		t.Error("isolation room ended on an unread census")
	}
	if ended(IsolationPlanning{Pawns: domain.Known([]PawnID{"7"})}, ChildRoomNeed{Module: PlannedIsolationRoom}) {
		t.Error("isolation room ended while a creepjoiner is isolated")
	}
}

// An ended need is only ever read from known facts (#1824).
func TestEndedRoomRolesNeedKnownFacts(t *testing.T) {
	adult := WorkPawn{Biotech: domain.Known(PawnBiotech{DevelopmentalStage: domain.Known("Adult"), Deathrest: domain.Known[*PawnDeathrest](nil)})}
	noStage := WorkPawn{Biotech: domain.Known(PawnBiotech{Deathrest: domain.Known[*PawnDeathrest](nil)})}
	noDeathrest := WorkPawn{Biotech: domain.Known(PawnBiotech{DevelopmentalStage: domain.Known("Adult")})}
	ideo, none := domain.Known(Ideoligion{}), domain.Known(ContainmentDemand{})
	known := domain.Known([]WorkPawn{adult})
	childRoles := []PlannedRole{PlannedNursery, PlannedPlayroom, PlannedClassroom}

	got := endedRoles(known, ideo, none)
	for _, role := range []PlannedRole{PlannedNursery, PlannedPlayroom, PlannedClassroom, PlannedDeathrestChamber, PlannedWorship, PlannedContainmentCell, PlannedIsolationRoom} {
		if !slices.Contains(got, role) {
			t.Errorf("%s not ended with every need gone: %v", role, got)
		}
	}
	for name, c := range map[string]struct {
		got  []PlannedRole
		kept []PlannedRole
	}{
		"unread stage":     {endedRoles(domain.Known([]WorkPawn{adult, noStage}), ideo, none), childRoles},
		"unread biotech":   {endedRoles(domain.Known([]WorkPawn{{}}), ideo, none), append([]PlannedRole{PlannedDeathrestChamber}, childRoles...)},
		"unread pawns":     {endedRoles(domain.Unknown[[]WorkPawn](), ideo, none), append([]PlannedRole{PlannedDeathrestChamber}, childRoles...)},
		"no pawns":         {endedRoles(domain.Known([]WorkPawn(nil)), ideo, none), append([]PlannedRole{PlannedDeathrestChamber}, childRoles...)},
		"unread deathrest": {endedRoles(domain.Known([]WorkPawn{noDeathrest}), ideo, none), []PlannedRole{PlannedDeathrestChamber}},
		"unread ideology":  {endedRoles(known, domain.Unknown[Ideoligion](), none), []PlannedRole{PlannedWorship}},
		"unread demand":    {endedRoles(known, ideo, domain.Unknown[ContainmentDemand]()), []PlannedRole{PlannedContainmentCell}},
		"entities held":    {endedRoles(known, ideo, domain.Known(ContainmentDemand{Entities: 1})), []PlannedRole{PlannedContainmentCell}},
		"nursery owed":     {endedRoles(known, ideo, none, ChildRoomNeed{Module: PlannedNursery}), []PlannedRole{PlannedNursery}},
	} {
		for _, role := range c.kept {
			if slices.Contains(c.got, role) {
				t.Errorf("%s: %s ended", name, role)
			}
		}
	}
}

// Each role's unbuilt room leaves the plan once its need is gone; a standing
// or furnished room and a role with no ended need keep theirs (#1824).
func TestReplanRetiresUnbuiltEndedRooms(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, ok := DeriveLayoutPlan(s, 3, TechTierCamp, nil, 30, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	roles := []PlannedRole{PlannedNursery, PlannedPlayroom, PlannedClassroom, PlannedDeathrestChamber, PlannedWorship, PlannedContainmentCell, PlannedIsolationRoom}
	var added []PlannedRoom
	for i, role := range roles {
		in := Rectangle{X: 20 + 12*int32(i%3), Z: 20 + 12*int32(i/3), Width: 4, Height: 4}
		r := PlannedRoom{Role: role, Interior: in, Door: domain.Cell{X: in.X + 1, Z: in.Z - 1}, DoorRot: domain.South}
		added = append(added, r)
		plan.Rooms = append(plan.Rooms, r)
	}
	count := func(p LayoutPlan, role PlannedRole) int { return len(p.roomsOf(role)) }
	replan := func(g RoomGrowth) LayoutPlan {
		next, _, _ := ReplanLayoutWithRooms(plan, s, g, 0, 3, 1, TechTierCamp, nil, nil)
		return next
	}
	for i, role := range roles {
		next := replan(RoomGrowth{Ended: []PlannedRole{role}, InUse: map[Rectangle]bool{}})
		if count(next, role) != 0 {
			t.Errorf("%s: unbuilt ended room kept", role)
		}
		for _, other := range roles {
			if other != role && count(next, other) != 1 {
				t.Errorf("%s ended dropped %s", role, other)
			}
		}
		if count(replan(RoomGrowth{Ended: []PlannedRole{role}, InUse: map[Rectangle]bool{added[i].Interior: true}}), role) != 1 {
			t.Errorf("%s: in-use room dropped", role)
		}
		if count(replan(RoomGrowth{}), role) != 1 {
			t.Errorf("%s: room dropped with no ended need", role)
		}
	}
}

// A planned room is in use when a census room stands on it or a building of
// the furniture stands inside it.
func TestRoomsInUseCountsFurnitureInside(t *testing.T) {
	in := Rectangle{X: 20, Z: 20, Width: 4, Height: 4}
	plan := LayoutPlan{Rooms: []PlannedRoom{{Role: PlannedNursery, Interior: in, Door: domain.Cell{X: 21, Z: 19}, DoorRot: domain.South}}}
	defs := furnitureDefs(map[string]Bounds{"Crib": {Width: 1, Height: 1}})
	b, err := domain.NewBuilding("Crib", domain.Cell{X: 21, Z: 21}, domain.South, "")
	if err != nil {
		t.Fatal(err)
	}
	crib := CurrentBuilding{ID: "1", Building: b, Cells: []domain.Cell{{X: 21, Z: 21}}}
	if RoomsInUse(plan, nil, defs)[in] {
		t.Fatal("an empty planned room is in use")
	}
	if !RoomsInUse(plan, []CurrentBuilding{crib}, defs)[in] {
		t.Fatal("a furnished room is not in use")
	}
}
