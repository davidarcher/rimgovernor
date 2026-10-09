package bridge

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// A def's family is the room role the game scores its building into: a work
// table's role, a bedroom bed, a medical bed; a work table with no role and an
// ordinary building have none.
func TestPieceShapesFamilyIsTheRoomRoleTheRowsGive(t *testing.T) {
	shapes, err := sharedRecordedCatalog(t).PieceShapes()
	if err != nil {
		t.Fatal(err)
	}
	for def, want := range map[string]policy.RoomRole{
		"FueledStove": policy.RoomRoleKitchen, "TableStonecutter": policy.RoomRoleWorkshop, "SimpleResearchBench": policy.RoomRoleLaboratory,
		"Bed": policy.RoomRoleBedroom, "RoyalBed": policy.RoomRoleBedroom, "HospitalBed": policy.RoomRoleHospital,
		"TableButcher": "", "PlantPot": "",
	} {
		if got := shapes.Defs[def].Family; got != want {
			t.Errorf("%s family %q, want %q", def, got, want)
		}
	}
	if s := shapes.Defs["FabricationBench"]; s.Size.X != 5 || s.Size.Z != 2 || s.Interaction == nil || s.Interaction.Z != -1 {
		t.Errorf("fabrication bench shape %+v", s)
	}
	if s := shapes.Defs["Bed"]; s.Interaction != nil {
		t.Errorf("a bed has no interaction cell: %+v", s)
	}
}

func TestPieceShapesRefuseACatalogTheTemplatesCannotUse(t *testing.T) {
	isDoubleBed := func(row *d.ThingDef) bool {
		return Buildable(row) && row.GetThingClass() == ClassBed && row.GetBuilding().GetBedHumanlike() && row.GetSize().GetX() == 2
	}
	for name, test := range map[string]struct {
		strip func(*recordedSlice)
		want  string
	}{
		"no end table":   {func(s *recordedSlice) { s.drop("EndTable") }, "end table"},
		"no dresser":     {func(s *recordedSlice) { s.drop("Dresser") }, "dresser"},
		"no cabinet":     {func(s *recordedSlice) { s.drop("ToolCabinet") }, "tool cabinet"},
		"no monitor":     {func(s *recordedSlice) { s.drop("VitalsMonitor") }, "vitals monitor"},
		"no sarcophagus": {func(s *recordedSlice) { s.drop("Sarcophagus") }, "Sarcophagus"},
		"no stove":       {func(s *recordedSlice) { s.drop("FueledStove", "ElectricStove") }, "Kitchen"},
		"no animal bed":  {func(s *recordedSlice) { s.drop("AnimalBed") }, "animal"},
		"no animal flap": {func(s *recordedSlice) { s.drop("AnimalFlap") }, "animal flap"},
		"no double bed":  {func(s *recordedSlice) { s.dropWhere(isDoubleBed) }, "double bed"},
		"stoves not role": {func(s *recordedSlice) {
			s.drop("ElectricStove")
			s.thing("FueledStove").Building.WorkTableRoomRole = ""
		}, "Kitchen"},
	} {
		slice := buildingsSlice(t)
		test.strip(slice)
		_, err := slice.catalog().PieceShapes()
		if err == nil || !strings.Contains(err.Error(), test.want) {
			t.Errorf("%s: %v, want an error naming %q", name, err, test.want)
		}
	}
}
