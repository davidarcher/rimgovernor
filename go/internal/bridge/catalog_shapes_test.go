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
		"TableButcher": "", "ElectricCrematorium": "", "Pot": "",
	} {
		if got := shapes[def].Family; got != want {
			t.Errorf("%s family %q, want %q", def, got, want)
		}
	}
	if s := shapes["FabricationBench"]; s.Size.X != 5 || s.Size.Z != 2 || s.Interaction == nil || s.Interaction.Z != -1 {
		t.Errorf("fabrication bench shape %+v", s)
	}
	if s := shapes["Bed"]; s.Interaction != nil {
		t.Errorf("a bed has no interaction cell: %+v", s)
	}
}

func TestPieceShapesRefuseACatalogTheTemplatesCannotUse(t *testing.T) {
	without := func(name string) []FixtureDef {
		var out []FixtureDef
		for _, def := range CoreFurnitureFixtures() {
			if def.Name != name {
				out = append(out, def)
			}
		}
		return out
	}
	for name, test := range map[string]struct {
		defs []FixtureDef
		want string
	}{
		"no end table":     {without("EndTable"), "EndTable"},
		"no stove":         {without("FueledStove"), "FueledStove"},
		"stove not a role": {append(without("FueledStove"), FixtureDef{Name: "FueledStove", Width: 3, Height: 1, Bench: true}), "family"},
		"bench no cell":    {append(without("ElectricCrematorium"), FixtureDef{Name: "ElectricCrematorium", Width: 3, Height: 2}), "interaction cell"},
	} {
		_, err := FixtureCatalog("load", test.defs...).PieceShapes()
		if err == nil || !strings.Contains(err.Error(), test.want) {
			t.Errorf("%s: %v, want an error naming %q", name, err, test.want)
		}
	}
}
