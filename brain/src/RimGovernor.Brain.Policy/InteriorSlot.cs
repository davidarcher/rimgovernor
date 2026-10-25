namespace RimGovernor.Brain.Policy;

/// <summary>
/// The shape of a definition a template can plan: its size and interaction cell offset at rotation North,
/// and the slot family it shares slots with. A family is the room role the game scores the building into:
/// kitchen stoves, workshop benches, laboratory benches, the beds of a bedroom. The catalog rows put a
/// building in its family (PieceShapes); a building with no interchangeable slot family has none.
/// </summary>
/// <param name="Def">The definition name.</param>
/// <param name="Size">The North footprint size.</param>
/// <param name="Family">The slot family.</param>
/// <param name="Interaction">The interaction cell offset from the anchor at North; InteractionCell.None for a definition without one.</param>
public sealed record InteriorPieceDef(string Def, Cell Size, PlanRole Family, InteractionCell Interaction)
{
    public InteriorPieceDef() : this("", default, default, new InteractionCell.None())
    {
    }
}

/// <summary>The cell a building's user stands on: At its offset from the anchor at rotation North, or None for a building without one.</summary>
public abstract record InteractionCell
{
    private InteractionCell() { }

    public sealed record At(Cell Offset) : InteractionCell;

    public sealed record None : InteractionCell;
}
