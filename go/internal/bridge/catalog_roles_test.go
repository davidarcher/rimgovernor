package bridge

import (
	"testing"
)

// TestDefinitionCatalogValidatesRoomRoles (#1728): a row's room-role
// furniture roles are known, unique and sorted.
func TestDefinitionCatalogValidatesRoomRoles(t *testing.T) {
	context := authorityTestContext(7)
	ok := catalogReply(context).GetObserved()
	ok.Definitions[0].RoomRoles = []string{"BabyBed", "Toy"}
	catalog, err := DecodeDefinitionCatalog(ok, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if names := catalog.RoomRoleDefinitions(); len(names) != 1 || names[0] != ok.Definitions[0].Definition.GetDefName() {
		t.Fatalf("role definitions %v", names)
	}
	for name, roles := range map[string][]string{"unknown": {"Throne"}, "duplicate": {"Toy", "Toy"}, "unsorted": {"Toy", "BabyBed"}} {
		v := catalogReply(context).GetObserved()
		v.Definitions[0].RoomRoles = roles
		if _, err := DecodeDefinitionCatalog(v, pbIdentity()); err == nil {
			t.Errorf("%s roles accepted", name)
		}
	}
}
