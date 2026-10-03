package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The piece shapes are rules over the recorded game's rows: the room role a
// work table or bed is scored into is its family, and sizes and interaction
// cells are the rows'.
func TestRecordedCatalogPieceShapes(t *testing.T) {
	shapes, err := recordedCatalog(t).PieceShapes()
	if err != nil {
		t.Fatal(err)
	}
	for def, want := range map[string]struct {
		family policy.RoomRole
		w, h   int32
		front  bool
	}{
		"FueledStove": {policy.RoomRoleKitchen, 3, 1, true}, "ElectricStove": {policy.RoomRoleKitchen, 3, 1, true},
		"TableStonecutter": {policy.RoomRoleWorkshop, 3, 1, true}, "FabricationBench": {policy.RoomRoleWorkshop, 5, 2, true},
		"SimpleResearchBench": {policy.RoomRoleLaboratory, 3, 2, true}, "HiTechResearchBench": {policy.RoomRoleLaboratory, 5, 2, true},
		"TableButcher": {"", 3, 1, true}, "ButcherSpot": {"", 1, 1, true}, "ElectricCrematorium": {"", 3, 2, true},
		"Bed": {policy.RoomRoleBedroom, 1, 2, false}, "DoubleBed": {policy.RoomRoleBedroom, 2, 2, false}, "SleepingSpot": {policy.RoomRoleBedroom, 1, 2, false},
		"HospitalBed": {policy.RoomRoleHospital, 1, 2, false}, "AnimalSleepingSpot": {"", 1, 1, false}, "DeathrestCasket": {"", 1, 2, false},
		"Dresser": {"", 2, 1, false}, "ToolCabinet": {"", 2, 1, false}, "Sarcophagus": {"", 1, 2, false},
	} {
		got, ok := shapes[def]
		if !ok || got.Family != want.family || got.Size.X != want.w || got.Size.Z != want.h || (got.Interaction != nil) != want.front {
			t.Errorf("%s shape %+v, want family %q %dx%d front %v", def, got, want.family, want.w, want.h, want.front)
		}
	}
}
