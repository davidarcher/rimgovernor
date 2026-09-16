package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// installedRoomRoles is every RoomRoleDef defName the installed game and
// expansions define (Data/*/Defs/Rooms/RoomRoles.xml), excluding the two
// structural pseudo-roles None and Room. The catalog is the per-role
// implementation matrix: adding a game role without a catalog row, or a row
// the game does not define, fails here rather than surfacing as a silent gap.
var installedRoomRoles = map[RoomRole]string{
	RoomRoleBedroom: "", RoomRolePrisonCell: "", RoomRoleDiningRoom: "", RoomRoleRecRoom: "", RoomRoleHospital: "",
	RoomRoleLaboratory: "", RoomRoleWorkshop: "", RoomRoleStoreroom: "", RoomRoleBarracks: "", RoomRolePrisonBarracks: "",
	RoomRoleKitchen: "", RoomRoleTomb: "", RoomRoleBarn: "",
	RoomRoleThroneRoom: "Royalty", RoomRoleWorshipRoom: "Ideology",
	RoomRoleNursery: "Biotech", RoomRolePlayroom: "Biotech", RoomRoleClassroom: "Biotech", RoomRoleDeathrestChamber: "Biotech",
	RoomRoleContainmentCell: "Anomaly", RoomRoleCeremonialChamber: "Anomaly",
}

func TestFacilityCatalogIsTheCompleteRoomRoleMatrix(t *testing.T) {
	seen := map[RoomRole]bool{}
	for _, f := range FacilityCatalog() {
		content, installed := installedRoomRoles[f.Role]
		if !installed || seen[f.Role] || f.Content != content {
			t.Fatal("catalog row is not one installed role", f)
		}
		seen[f.Role] = true
		switch f.Status {
		case FacilityImplemented:
			if len(f.Furniture) == 0 {
				t.Fatal("implemented role without furniture", f)
			}
		case FacilityPending:
			if len(f.Furniture) != 0 {
				t.Fatal("pending role with furniture", f)
			}
		default:
			t.Fatal("unknown status", f)
		}
		if !f.Hosts(f.Role) || f.Hosts(RoomRoleNone) {
			t.Fatal("hosting must include the role itself and never a non-room", f)
		}
		for _, r := range f.Compatible {
			if _, ok := installedRoomRoles[r]; !ok && r != RoomRoleRoom {
				t.Fatal("compatible role is not installed", f, r)
			}
		}
	}
	for role := range installedRoomRoles {
		if !seen[role] {
			t.Fatal("installed role missing from the catalog", role)
		}
	}
	if _, err := Facility(RoomRoleNone); err == nil {
		t.Fatal("non-room lookup accepted")
	}
}

func TestDiningAndRecreationShareHostingRooms(t *testing.T) {
	dining, _ := Facility(RoomRoleDiningRoom)
	recreation, _ := Facility(RoomRoleRecRoom)
	for _, role := range []RoomRole{RoomRoleDiningRoom, RoomRoleRecRoom, RoomRoleRoom} {
		if !dining.Hosts(role) || !recreation.Hosts(role) {
			t.Fatal("shared dining/recreation room refused", role)
		}
	}
	for _, role := range []RoomRole{RoomRoleBedroom, RoomRoleBarracks, RoomRoleKitchen, RoomRoleHospital} {
		if dining.Hosts(role) || recreation.Hosts(role) {
			t.Fatal("incompatible room hosted", role)
		}
	}
}

func TestHostedComfortDropsFacilitiesOutsideHostingRooms(t *testing.T) {
	people := []PawnID{"a"}
	v := ComfortObservation{People: people,
		Surfaces:   []DiningSurface{{ID: "hosted-table", RoomID: "dining", Adjacent: []domain.Cell{{X: 1, Z: 1}}}, {ID: "barracks-table", RoomID: "barracks"}, {ID: "unknown-table", RoomID: "gone"}, {ID: "outdoor-table"}},
		Dining:     []ComfortFacility{{ID: "hosted-chair", RoomID: "rec", AccessibleTo: people}, {ID: "barracks-chair", RoomID: "barracks", AccessibleTo: people}},
		Recreation: []ComfortFacility{{ID: "hosted-pin", RoomID: "dining", AccessibleTo: people}, {ID: "unroled-pin", RoomID: "unroled", AccessibleTo: people}, {ID: "outdoor-pin", AccessibleTo: people}}}
	rooms := RoomObservation{Rooms: []Room{
		{ID: "dining", Role: domain.Known(RoomRoleDiningRoom)}, {ID: "rec", Role: domain.Known(RoomRoleRecRoom)},
		{ID: "barracks", Role: domain.Known(RoomRoleBarracks)}, {ID: "unroled"}}}
	hosted, err := HostedComfort(v, rooms)
	if err != nil {
		t.Fatal(err)
	}
	if len(hosted.People) != 1 || len(hosted.Surfaces) != 1 || hosted.Surfaces[0].ID != "hosted-table" || len(hosted.Dining) != 1 || hosted.Dining[0].ID != "hosted-chair" || len(hosted.Recreation) != 1 || hosted.Recreation[0].ID != "hosted-pin" {
		t.Fatal(hosted)
	}
	cells := HostingCells(FacilityRequirement{Role: RoomRoleDiningRoom, Compatible: []RoomRole{RoomRoleRecRoom}}, RoomObservation{Rooms: []Room{
		{ID: "dining", Role: domain.Known(RoomRoleDiningRoom), Cells: []domain.Cell{{X: 1, Z: 1}}},
		{ID: "rec", Role: domain.Known(RoomRoleRecRoom), Cells: []domain.Cell{{X: 2, Z: 1}}},
		{ID: "bed", Role: domain.Known(RoomRoleBedroom), Cells: []domain.Cell{{X: 3, Z: 1}}},
		{ID: "unroled", Cells: []domain.Cell{{X: 4, Z: 1}}}}})
	if len(cells) != 2 || cells[0] != (domain.Cell{X: 1, Z: 1}) || cells[1] != (domain.Cell{X: 2, Z: 1}) {
		t.Fatal(cells)
	}
}
