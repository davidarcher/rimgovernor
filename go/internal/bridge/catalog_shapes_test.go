package bridge

import (
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A def's family is the room role the game scores its building into: a work
// table's role, a bedroom bed, a medical bed; a work table with no role and an
// ordinary building have none.
func TestPieceShapesFamilyIsTheRoomRoleTheRowsGive(t *testing.T) {
	shapes, err := FixtureCatalog("load", slices.Concat(CoreFurnitureFixtures(), []FixtureDef{{Name: "Pot", Width: 1, Height: 1}})...).PieceShapes()
	if err != nil {
		t.Fatal(err)
	}
	for def, want := range map[string]policy.RoomRole{
		"FueledStove": policy.RoomRoleKitchen, "TableStonecutter": policy.RoomRoleWorkshop, "SimpleResearchBench": policy.RoomRoleLaboratory,
		"Bed": policy.RoomRoleBedroom, "RoyalBed": policy.RoomRoleBedroom, "HospitalBed": policy.RoomRoleHospital,
		"TableButcher": "", "Pot": "",
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
	without := func(names ...string) []FixtureDef {
		var out []FixtureDef
		for _, def := range CoreFurnitureFixtures() {
			if !slices.Contains(names, def.Name) {
				out = append(out, def)
			}
		}
		return out
	}
	for name, test := range map[string]struct {
		defs []FixtureDef
		want string
	}{
		"no end table":    {without("EndTable"), "end table"},
		"no dresser":      {without("Dresser"), "dresser"},
		"no cabinet":      {without("ToolCabinet"), "tool cabinet"},
		"no monitor":      {without("VitalsMonitor"), "vitals monitor"},
		"no sarcophagus":  {without("Sarcophagus"), "Sarcophagus"},
		"no stove":        {without("FueledStove", "ElectricStove"), "Kitchen"},
		"no animal bed":   {without("AnimalBed"), "animal"},
		"no animal flap":  {without("AnimalFlap"), "animal flap"},
		"no double bed":   {without("DoubleBed", "RoyalBed", "BedrollDouble"), "double bed"},
		"stoves not role": {append(without("FueledStove", "ElectricStove"), FixtureDef{Name: "FueledStove", Width: 3, Height: 1, Bench: true}), "Kitchen"},
	} {
		_, err := FixtureCatalog("load", test.defs...).PieceShapes()
		if err == nil || !strings.Contains(err.Error(), test.want) {
			t.Errorf("%s: %v, want an error naming %q", name, err, test.want)
		}
	}
}
