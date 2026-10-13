using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_shapes.go: the plannable shape of every buildable def, by name: the size and interaction cell offset the def
// states, and the family the def shares slots with, which is the room role the game itself scores the building into.
//   - a work table is in the family of its workTableRoomRole (the game's RoomRoleWorker_Workshop and _Laboratory count
//     exactly the buildings whose workTableRoomRole is their role, and the kitchen role the stoves): a stove is Kitchen, a
//     bench Workshop, a research bench Laboratory. A butcher table and a crafting spot state no role and so are in no
//     family;
//   - a humanlike bed (a Building_Bed with bed_humanlike) counted for bedrooms and barracks is Bedroom, one that is medical
//     by default is Hospital (RoomRoleWorker_Bedroom and _Hospital); a cot for animals, a deathrest casket (not counted)
//     and the rest are in no family.
// The shapes must be ones the interior templates can lay out and hold the furniture they plan (P.PieceShapes.Validate); a
// catalog that breaks either is an error.

public sealed partial class DefinitionCatalog
{
    /// <summary>The plannable shape of every buildable def, built once.</summary>
    public P.PieceShapes PieceShapes() => _shapes.Value;

    private P.PieceShapes BuildPieceShapes()
    {
        var defs = new Dictionary<string, P.InteriorPieceDef>(StringComparer.Ordinal);
        foreach (var (name, row) in _thingDefs)
        {
            if (!Buildable(row))
            {
                continue;
            }

            Domain.Cell? interaction = row.hasInteractionCell ? new Domain.Cell(row.interactionCellOffset.x, row.interactionCellOffset.z) : null;
            defs[name] = new P.InteriorPieceDef(name, new Domain.Cell(row.size.x, row.size.z), PieceFamily(row), interaction);
        }

        var shapes = new P.PieceShapes { Defs = defs, Furniture = RoomFurniture(defs) };
        try
        {
            shapes.Validate();
        }
        catch (Domain.DomainException e)
        {
            throw new CatalogException(e.Message);
        }

        return shapes;
    }

    // The room role the game scores the def's building into, or none.
    private static P.RoomRole PieceFamily(ThingDef row)
    {
        if (row.building?.workTableRoomRole?.defName is { Length: > 0 } role)
        {
            return Wire.TryParse<P.RoomRole>(role, out var parsed) ? parsed : throw new CatalogException($"{row.defName} names room role {role} the policy does not know");
        }

        if (!IsBed(row) || row.building is not { bed_humanlike: true } building)
        {
            return P.RoomRole.Unset;
        }

        return building.bed_defaultMedical ? P.RoomRole.Hospital : building.bed_countsForBedroomOrBarracks ? P.RoomRole.Bedroom : P.RoomRole.Unset;
    }
}
