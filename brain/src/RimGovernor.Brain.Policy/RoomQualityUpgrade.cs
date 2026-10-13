using System.Collections.Immutable;

namespace RimGovernor.Brain.Policy;

// Room quality gap closer: an owned bedroom (or a dining or rec room, against CommonRoomTargets) below its
// RoomTarget.Min gets one furniture piece at a time from the bedroom template's optional slots. The weakest
// of wealth, beauty, space and cleanliness decides whether a piece helps at all:
//   - space weakest: nothing is added, since every furniture tile costs the room 0.9 space and the weakest
//     stat carries about half the score; the owner is given a suite instead (SuiteClaims);
//   - otherwise the slots go in lever order (end table, dresser, then the lamp), each adding wealth.
// Flooring is not a lever here: MaintainFlooring already floors every bedroom at the living tier. A room
// marked NeverUpgrade, or at or above its Max, is never touched.

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

    public RoomStat Weakest { get; init; }

    /// <summary>When set, places Def at each cell (a floor).</summary>
    public EquatableArray<Cell> Cells { get; init; }

    /// <summary>When set, overrides the definition's default stuff.</summary>
    public string Stuff { get; init; } = "";
}

public sealed partial record RoomFurniture
{
    /// <summary>The definitions the closer's slots place in a bedroom: end table, dresser and the standing lamp (a suite's furnishing by them).</summary>
    public IReadOnlyList<string> BedroomUpgradePieces() => [EndTable.Def, Dresser.Def, PolicyFunctions.StandingLampDef];
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Divides each stat by the native normaliser RoomStatWorker_Impressiveness uses before combining (wealth
    /// 1500, beauty 3, space 125, cleanliness 2.5 after the +2.5 shift), so the weakest stat is compared on
    /// the game's own footing.
    /// </summary>
    public static RoomStat WeakestRoomStat(RoomQuality q)
    {
        (RoomStat Stat, double V)[] scores =
        [
            (RoomStat.Wealth, q.Wealth / 1500),
            (RoomStat.Beauty, q.Beauty / 3),
            (RoomStat.Space, q.Space / 125),
            (RoomStat.Cleanliness, (q.Cleanliness + 2.5) / 2.5),
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
    /// every catalog): the standing lamp, the plant pot and the floors, the beauty levers. The planning census
    /// must read them for availability and stuff.
    /// </summary>
    public static IReadOnlyList<string> RoomBeautyDefinitions()
    {
        var output = new List<string> { StandingLampDef, PlantPotDefinition };
        output.AddRange(DefaultFlooringPolicy().Floors);
        return output;
    }

    /// <summary>
    /// The first (by room id) upgrade due, false when none. rooms are the furniture rooms (FurnitureRooms);
    /// available reports a definition the colony can build now.
    /// </summary>
    public static (RoomUpgrade Upgrade, bool Ok) NextRoomUpgrade(
        SleepingObservation obs,
        IReadOnlyDictionary<string, RoomTarget> targets,
        IReadOnlyList<FurnitureRoom> rooms,
        Func<string, bool> available,
        RoomGate gate)
    {
        if (!obs.Rooms.TryGetValue(out var census))
        {
            return (new RoomUpgrade(), false);
        }
        var quality = new Dictionary<string, RoomQuality>(StringComparer.Ordinal);
        foreach (var r in census)
        {
            if (r.Quality.TryGetValue(out var q))
            {
                quality[r.Id] = q;
            }
        }
        var furniture = new Dictionary<string, FurnitureRoom>(StringComparer.Ordinal);
        foreach (var r in rooms)
        {
            furniture[r.Id] = r;
        }
        foreach (var id in targets.Keys.Order(StringComparer.Ordinal))
        {
            var t = targets[id];
            var qk = quality.TryGetValue(id, out var q);
            var rk = furniture.TryGetValue(id, out var room);
            if (!qk || !rk || t.NeverUpgrade || t.Min <= 0 || q!.Impressiveness >= t.Min || (t.Max > 0 && q.Impressiveness >= t.Max))
            {
                continue;
            }
            var weakest = WeakestRoomStat(q);
            if (weakest == RoomStat.Space)
            {
                continue;
            }
            var (plan, ok) = PlanRoomInterior(room!.Planning(), new InteriorPieceDef());
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
                    if (piece.Slot != slot || have.Contains(piece.Def) || !available(piece.Def) || RoomPieceOverlaps(room.Pieces, piece.Rect) || !gate.PieceAllowed(t.Owners, piece.Def))
                    {
                        continue;
                    }
                    return (new RoomUpgrade { Room = id, Slot = slot, Def = piece.Def, Anchor = piece.Anchor(), Rot = piece.Rot, Weakest = weakest }, true);
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
