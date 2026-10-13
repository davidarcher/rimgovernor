using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The plannable shape of every buildable definition, as the catalog rows give it
/// (DefinitionCatalog.PieceShapes): its size and interaction cell offset at rotation North, and the family it
/// shares slots with, which is the room role the game scores the building into (the definition's
/// workTableRoomRole, or the bedroom or hospital role of a humanlike bed); and the furniture the room
/// planners place, chosen by rules over the same rows (RoomFurniture). It is a threaded value on the room
/// observation and the colony projection, carried to InteriorRoom, never a global.
/// </summary>
public sealed record PieceShapes
{
    public IReadOnlyDictionary<string, InteriorPieceDef> Defs { get; init; } = new Dictionary<string, InteriorPieceDef>();

    public RoomFurniture Furniture { get; init; } = new();

    /// <summary>A definition's shape; Ok is false when the catalog has no buildable row for it.</summary>
    public (InteriorPieceDef Shape, bool Ok) Get(string def) =>
        Defs.TryGetValue(def, out var shape) ? (shape, true) : (new InteriorPieceDef(), false);

    /// <summary>
    /// The shape of a building standing in a room: a definition no buildable row holds (a ruin, a natural
    /// edifice) is no plannable piece and comes back with only its name, in no family.
    /// </summary>
    internal InteriorPieceDef Standing(string def) =>
        Defs.TryGetValue(def, out var shape) ? shape : new InteriorPieceDef(def, default, RoomRole.None, null);

    /// <summary>
    /// Checks every shape is a footprint a template can lay out (a positive size), that the furniture rules
    /// found a set the templates can lay out and that every definition they plan is present, in its family
    /// and, for a bench, worked from the floor in front of it. Throws DomainException naming the first
    /// violation.
    /// </summary>
    public void Validate()
    {
        foreach (var (def, shape) in Defs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (shape.Def != def || shape.Size.X < 1 || shape.Size.Z < 1)
            {
                throw new DomainException($"piece shape of {def} is \"{shape.Def}\" {shape.Size.X}x{shape.Size.Z}");
            }
        }
        Furniture.Validate();
        var wants = new List<TemplatePiece>
        {
            new(Furniture.PrimaryBed(), RoomRole.Bedroom, false),
            new(Furniture.CoupleBed(), RoomRole.Bedroom, false),
            new(Furniture.Sarcophagus, RoomRole.None, false),
        };
        foreach (var (role, def) in Furniture.Bench.OrderBy(kv => Wire.Name(kv.Key), StringComparer.Ordinal))
        {
            wants.Add(new TemplatePiece(def, role, true));
        }
        foreach (var link in Furniture.Facilities())
        {
            wants.Add(new TemplatePiece(link.Def, RoomRole.None, false));
        }
        if (Furniture.AdvancedLab.Length != 0)
        {
            wants.Add(new TemplatePiece(Furniture.AdvancedLab, RoomRole.Laboratory, true));
            wants.Add(new TemplatePiece(Furniture.Analyzer.Def, RoomRole.None, false));
        }
        wants.AddRange(TemplatePieces);
        foreach (var w in wants)
        {
            if (!Defs.TryGetValue(w.Def, out var shape))
            {
                throw new DomainException($"interior templates plan {w.Def}, which the catalog has no buildable row for");
            }
            if (w.Family != RoomRole.None && shape.Family != w.Family)
            {
                throw new DomainException(
                    $"interior templates plan {w.Def} as a {Wire.Name(w.Family)} piece, its row puts it in family \"{Wire.Name(shape.Family)}\"");
            }
            if (w.Bench && !FrontInteraction(shape))
            {
                throw new DomainException($"interior templates plan {w.Def} as a bench, its row has no interaction cell in front of it");
            }
        }
    }

    /// <summary>
    /// A definition the interior templates plan: the family it must be in (None: any) and whether it is a
    /// bench a worker uses from the open floor in front of it.
    /// </summary>
    private sealed record TemplatePiece(string Def, RoomRole Family, bool Bench);

    // The furniture the interior templates plan that no rule of RoomFurniture names. A catalog missing one
    // cannot lay a template out, which Validate names.
    private static readonly TemplatePiece[] TemplatePieces =
    [
        new(PolicyConstants.StandingLampDef, RoomRole.None, false),
        new(PolicyConstants.WorkshopShelfDef, RoomRole.None, false),
        new(PolicyConstants.ShelterHeatDef, RoomRole.None, false),
        new(PolicyConstants.ShelterCraftingDef, RoomRole.None, false),
    ];
}

public static class InteriorPieceShapeExtensions
{
    /// <summary>
    /// Reports whether a definition may take this slot: the slot's own definition, or a member of its family
    /// with the same footprint (size and interaction offset), which stays regular and role-correct in the
    /// slot.
    /// </summary>
    public static bool Accepts(this InteriorPiece p, PieceShapes shapes, string def)
    {
        if (def == p.Def)
        {
            return true;
        }
        var (a, aok) = shapes.Get(p.Def);
        var (b, bok) = shapes.Get(def);
        return aok && bok && a!.Family != RoomRole.None && a.Family == b!.Family && a.Size == b.Size
            && a.Interaction == b.Interaction;
    }

    /// <summary>Reports whether a worker uses the piece from the open floor in front of it at rotation North, as a bench row needs.</summary>
    public static bool WorkedFromFront(this InteriorPieceDef d) => FrontInteraction(d);
}
