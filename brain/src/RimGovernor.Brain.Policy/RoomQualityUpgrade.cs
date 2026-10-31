using System.Collections.Immutable;

namespace RimGovernor.Brain.Policy;

// Room furnishing improvement (RG-HABITAT-001): a room gets one furniture piece at a time from the bedroom
// template's optional slots (end table, dresser, then the lamp) when the piece is worth building: its owners'
// gain sleeping at the Impressiveness stage it reaches, over the horizon, beats its building cost
// (RoomImprovementValue). No tier floor asks for it: a room already at the stage the piece would leave it gains
// nothing. A title's bedroom requirement (reason "title") is a requirement, not a comfort, and asks for a piece
// while the room is below it. A room marked NeverUpgrade, or at or above its Max, is never touched.

/// <summary>Names one input of the native Impressiveness stat.</summary>
public enum RoomStat
{
    [WireName("")]
    None = 0,
    Wealth,
    Beauty,
    Space,
    Cleanliness,
}

/// <summary>One PlaceBuilding the closer wants.</summary>
public sealed record RoomUpgrade
{
    public string Room { get; init; } = "";

    public string Slot { get; init; } = "";

    public string Def { get; init; } = "";

    public Cell Anchor { get; init; }

    public Rotation Rot { get; init; }

    /// <summary>A furniture piece's North footprint, so the room's furnishing lists it (Slotted); unset for a floor and a bed replacement.</summary>
    public Cell Size { get; init; }

    /// <summary>The piece as a furnishing slot: where the lever wants it in its room.</summary>
    internal InteriorPiece Slotted() => new() { Slot = Slot, Def = Def, Size = Size, Rot = Rot, Rect = InteriorRules.OccupiedRect(Anchor, Size, Rot) };

    /// <summary>When set, places Def at each cell (a floor).</summary>
    public EquatableArray<Cell> Cells { get; init; }

    /// <summary>When set, overrides the definition's default stuff.</summary>
    public string Stuff { get; init; } = "";
}

public static partial class RoomQualityRules
{
    /// <summary>
    /// Divides each stat by the native normaliser RoomStatWorker_Impressiveness uses before combining (RoomSpaceRules:
    /// wealth, beauty, space, and cleanliness after the shift by its scale), so the weakest stat is compared on
    /// the game's own footing.
    /// </summary>
    public static RoomStat WeakestRoomStat(RoomQuality q)
    {
        (RoomStat Stat, double V)[] scores =
        [
            (RoomStat.Wealth, q.Wealth / RoomSpaceRules.WealthScale),
            (RoomStat.Beauty, q.Beauty / RoomSpaceRules.BeautyScale),
            (RoomStat.Space, q.Space / RoomSpaceRules.SpaceScale),
            (RoomStat.Cleanliness, (q.Cleanliness + RoomSpaceRules.CleanlinessScale) / RoomSpaceRules.CleanlinessScale),
        ];
        var best = scores[0];
        foreach (var s in scores.Skip(1))
        {
            if (s.V < best.V)
            {
                best = s;
            }
        }
        return best.Stat;
    }

    /// <summary>The bedroom template slots the closer fills, cheapest lever first.</summary>
    internal static readonly ImmutableArray<string> RoomUpgradeSlots = ["end_table", "dresser", "lamp"];

    /// <summary>
    /// The definitions the closer may place beside the furniture rules' (RoomFurniture.Definitions, read with
    /// every catalog): the standing lamp and the plant pot. The planning census reads them for availability
    /// and stuff; all buildable floors already join the ordinary catalog composition.
    /// </summary>
    public static IReadOnlyList<string> RoomBeautyDefinitions()
    {
        var output = new List<string> { InteriorHelpersRules.StandingLampDef, PlantPotDefinition };
        return output;
    }

    /// <summary>
    /// The first (by room id) upgrade due, false when none. rooms are the furniture rooms (FurnitureRooms);
    /// available reports a definition the colony can build now; value prices a piece in its room
    /// (RoomImprovementValue). A room below a title's bedroom requirement takes the next piece unless space is its
    /// weakest stat; any other room takes only a piece whose value is due.
    /// </summary>
    public static (RoomUpgrade Upgrade, bool Ok) NextRoomUpgrade(
        SleepingObservation obs,
        IReadOnlyDictionary<string, RoomTarget> targets,
        IReadOnlyList<FurnitureRoom> rooms,
        Func<string, bool> available,
        Func<UpkeepRoom, InteriorPiece, RoomImprovementValue> value)
    {
        var census = new Dictionary<string, UpkeepRoom>(StringComparer.Ordinal);
        foreach (var r in obs.Rooms)
        {
            census[r.Id] = r;
        }
        var furniture = new Dictionary<string, FurnitureRoom>(StringComparer.Ordinal);
        foreach (var r in rooms)
        {
            furniture[r.Id] = r;
        }
        foreach (var id in targets.Keys.Order(StringComparer.Ordinal))
        {
            var t = targets[id];
            var qk = census.TryGetValue(id, out var upkeep);
            var rk = furniture.TryGetValue(id, out var room);
            if (!qk || !rk || t.NeverUpgrade || (t.Max > 0 && upkeep!.Quality.Impressiveness >= t.Max))
            {
                continue;
            }
            var q = upkeep!.Quality;
            var required = t.Reasons.Contains("title") && q.Impressiveness < t.Min;
            if (required && WeakestRoomStat(q) == RoomStat.Space)
            {
                continue;
            }
            var (plan, ok) = InteriorRegionRules.PlanRoomInterior(room!.Planning(), new InteriorPieceDef());
            if (!ok)
            {
                continue;
            }
            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in room.Pieces)
            {
                have.Add(p.Def);
            }
            foreach (var slot in RoomUpgradeSlots)
            {
                foreach (var piece in plan.Pieces)
                {
                    if (piece.Slot != slot || have.Contains(piece.Def) || !available(piece.Def) || RoomPieceOverlaps(room.Pieces, piece.Rect)
                        || (!required && !value(upkeep, piece).Due))
                    {
                        continue;
                    }
                    return (new RoomUpgrade { Room = id, Slot = slot, Def = piece.Def, Size = piece.Size, Anchor = piece.Anchor(), Rot = piece.Rot }, true);
                }
            }
        }
        return (new RoomUpgrade(), false);
    }

    internal static bool RoomPieceOverlaps(IReadOnlyList<FurniturePiece> pieces, Rectangle r)
    {
        foreach (var p in pieces)
        {
            var a = p.Rect;
            if (a.X < r.X + r.Width && r.X < a.X + a.Width && a.Z < r.Z + r.Height && r.Z < a.Z + a.Height)
            {
                return true;
            }
        }
        return false;
    }
}
