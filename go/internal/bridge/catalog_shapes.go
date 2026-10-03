package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// ClassBed is the thing class of a bed, Building_Bed or a subclass.
const ClassBed = "RimWorld.Building_Bed"

// PieceShapes is the plannable shape of every buildable def, by name, built
// once from the catalog rows: the size and interaction cell offset the row
// states, and the family the def shares slots with, which is the room role
// the game itself scores the building into:
//   - a work table is in the family of its workTableRoomRole (the game's
//     RoomRoleWorker_Workshop and _Laboratory count exactly the buildings
//     whose workTableRoomRole is their role, and the kitchen role the
//     stoves): a stove is Kitchen, a bench Workshop, a research bench
//     Laboratory. A butcher table, a crafting spot and a crematorium state
//     no role and so are in no family;
//   - a humanlike bed (a Building_Bed with bed_humanlike) counted for
//     bedrooms and barracks is Bedroom, one that is medical by default is
//     Hospital (RoomRoleWorker_Bedroom and _Hospital); a cot for animals, a
//     deathrest casket (not counted) and the rest are in no family.
//
// The shapes must be ones the interior templates can lay out and hold the
// furniture they plan (policy.PieceShapes.Validate); a catalog that breaks
// either is an error.
func (catalog *DefinitionCatalog) PieceShapes() (policy.PieceShapes, error) {
	if catalog == nil {
		return nil, contract("no definition catalog")
	}
	catalog.shapesOnce.Do(func() { catalog.shapes, catalog.shapesErr = catalog.buildPieceShapes() })
	return catalog.shapes, catalog.shapesErr
}

func (catalog *DefinitionCatalog) buildPieceShapes() (policy.PieceShapes, error) {
	out := policy.PieceShapes{}
	for name, row := range catalog.ThingDefs {
		if !Buildable(row) || row.GetSize() == nil {
			continue
		}
		shape := policy.InteriorPieceDef{Def: name, Size: domain.Cell{X: row.GetSize().GetX(), Z: row.GetSize().GetZ()}}
		if row.GetHasInteractionCell() {
			offset := row.GetInteractionCellOffset()
			shape.Interaction = &domain.Cell{X: offset.GetX(), Z: offset.GetZ()}
		}
		family, err := catalog.pieceFamily(row)
		if err != nil {
			return nil, err
		}
		shape.Family = family
		out[name] = shape
	}
	if err := out.Validate(); err != nil {
		return nil, contract("%v", err)
	}
	return out, nil
}

// pieceFamily is the room role the game scores the def's building into, or
// none.
func (catalog *DefinitionCatalog) pieceFamily(row *d.ThingDef) (policy.RoomRole, error) {
	if role := row.GetBuilding().GetWorkTableRoomRole(); role != "" {
		return policy.RoomRole(role), nil
	}
	bed, err := catalog.ClassIsA(row.GetThingClass(), ClassBed)
	if err != nil || !bed || !row.GetBuilding().GetBedHumanlike() {
		return "", err
	}
	switch building := row.GetBuilding(); {
	case building.GetBedDefaultMedical():
		return policy.RoomRoleHospital, nil
	case building.GetBedCountsForBedroomOrBarracks():
		return policy.RoomRoleBedroom, nil
	}
	return "", nil
}
