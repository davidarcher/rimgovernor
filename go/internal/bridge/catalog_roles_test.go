package bridge

import (
	"slices"
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// TestGameRoomRolesByName: the furniture roles the game scores by ThingDefOf
// name belong to the named defs alone, and a buildable one is a room-role row.
func TestGameRoomRolesByName(t *testing.T) {
	for name, want := range map[string][]string{"ToyBox": {"Toy"}, "BabyDecoration": {"Decoration"}, "Blackboard": {"Board"}, "SchoolDesk": {"Desk"}, "Wall": nil} {
		if got := GameRoomRoles(name); !slices.Equal(got, want) {
			t.Errorf("%s roles %v, want %v", name, got, want)
		}
	}
	reply := factsReply()
	reply.ThingDefs = append(reply.ThingDefs, &d.ThingDef{DefName: "ToyBox", DesignationCategory: "Furniture"})
	catalog, err := DecodeDefinitionCatalog(reply, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if names, err := catalog.RoomRoleRows(); err != nil || !slices.Equal(names, []string{"ToyBox"}) {
		t.Fatalf("role definitions %v %v", names, err)
	}
}
