namespace RimGovernor.Brain.Policy;

/// <summary>
/// The shape of a definition a template can plan: its size and interaction cell offset at rotation North,
/// and the slot family it shares slots with. A family is the room role the game scores the building into:
/// kitchen stoves, workshop benches, laboratory benches, the beds of a bedroom. The catalog rows put a
/// building in its family (PieceShapes); a building in no family, like a butchery, whose blood filth must
/// never take a kitchen or workshop slot (SeparationProtectedCells), has none.
/// </summary>
/// <param name="Def">The definition name.</param>
/// <param name="Size">The North footprint size.</param>
/// <param name="Family">The slot family.</param>
/// <param name="Interaction">The interaction cell offset from the anchor at North; null for a definition without one.</param>
public sealed record InteriorPieceDef(string Def, Cell Size, RoomRole Family, Cell? Interaction);
