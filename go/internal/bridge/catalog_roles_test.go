package bridge

import (
	"slices"
	"testing"
)

// TestDefinitionCatalogValidatesRoomRoles: a def's game-named
// room-role furniture roles are known, unique and sorted.
func TestDefinitionCatalogValidatesRoomRoles(t *testing.T) {
	ok := factsReply()
	ok.ThingFacts[0].RoomRoles = []string{"Decoration", "Toy"}
	catalog, err := DecodeDefinitionCatalog(ok, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if names, err := catalog.RoomRoleRows(); err != nil || !slices.Equal(names, []string{ok.ThingFacts[0].DefName}) {
		t.Fatalf("role definitions %v %v", names, err)
	}
	for name, roles := range map[string][]string{"unknown": {"Throne"}, "duplicate": {"Toy", "Toy"}, "unsorted": {"Toy", "Decoration"}} {
		v := factsReply()
		v.ThingFacts[0].RoomRoles = roles
		if _, err := DecodeDefinitionCatalog(v, pbIdentity()); err == nil {
			t.Errorf("%s roles accepted", name)
		}
	}
}
