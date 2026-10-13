namespace RimGovernor.Brain.Policy;

// Containment strength follows the decompiled Assembly-CSharp StatWorker_ContainmentStrength calculation:
//
//   strength = holder stat base + facility statOffsets
//            + (lighting + wallHp + doorHp + floor, each *= 0.9 per other holder in or beside the room; + roof)
//              * holder containmentFactor
//
// and zero when the room is missing, touches the map edge or has no map. The inputs are the game's defs (wall
// and door MaxHitPoints, the holder's containmentFactor, the terrain stat, facility offsets and their limits);
// the constants below are code constants of that worker, which no def or native fact carries, so they are
// named here once and nowhere else.

/// <summary>
/// One facility def that offsets the holder's containment strength (CompProperties_Facility statOffsets on
/// ContainmentStrength, linkable to the holder through its CompProperties_AffectedByFacilities). The game counts
/// at most MaxSimultaneous of one def and only those within MaxDistance; both are the def's fields as they are,
/// with no sentinel read into them.
/// </summary>
public sealed record ContainmentFacility
{
    public string Def { get; init; } = "";

    public double Offset { get; init; }

    public double MaxDistance { get; init; }

    public int MaxSimultaneous { get; init; }
}

/// <summary>
/// The static inputs of the prediction, read from the definition catalog (bridge.DefinitionCatalog.ContainmentDefs):
/// the holder the planner places, the wall and door the shell builds, the terrain stat of a plain floor and the
/// facilities that link to the holder.
/// </summary>
public sealed record ContainmentDefs
{
    public string Holder { get; init; } = "";

    /// <summary>The holder's CompProperties_EntityHolder containmentFactor.</summary>
    public double HolderFactor { get; init; }

    /// <summary>The holder's ContainmentStrength stat base (the def's own base, else the stat def's default).</summary>
    public double HolderBase { get; init; }

    /// <summary>The MaxHitPoints stat of the shell's wall as the planner builds it (def and stuff).</summary>
    public double WallHP { get; init; }

    /// <summary>The MaxHitPoints stat of the shell's door as the planner builds it (def and stuff).</summary>
    public double DoorHP { get; init; }

    /// <summary>The terrain ContainmentStrength stat of the floor the room stands on: the stat's default base for plain terrain.</summary>
    public double FloorStrength { get; init; }

    /// <summary>
    /// The terrain def with the greatest ContainmentStrength stat above the plain floor's, the strength a
    /// floored room's floor term takes; its Def is empty when the catalog has none.
    /// </summary>
    public ContainmentFloor Floor { get; init; } = new();

    public EquatableArray<ContainmentFacility> Facilities { get; init; }

    /// <summary>
    /// The holder's containment strength in the room. A definition the formula cannot be evaluated from throws,
    /// never a default.
    /// </summary>
    public double Predict(ContainmentRoom room)
    {
        DomainException.Require(Holder.Length != 0, "no holder def is set");
        DomainException.Require(
            WallHP > 0 && DoorHP > 0,
            FormattableString.Invariant($"the wall ({WallHP} hit points) or door ({DoorHP}) has no hit points"));
        DomainException.Require(room.MeanGlow >= 0 && room.OtherHolders >= 0, "a room has no negative glow or holder count");
        var floor = FloorStrength;
        if (room.Floored)
        {
            DomainException.Require(Floor.Def.Length != 0, "the catalog has no floor that adds containment strength");
            floor = Floor.Strength;
        }
        var terms = (room.MeanGlow * ContainmentLightingFactor) + ContainmentWallTerm(WallHP) + (DoorHP / ContainmentDoorHPDivisor) + floor;
        terms *= Math.Pow(ContainmentOtherHolderFactor, room.OtherHolders);
        if (room.OpenRoof)
        {
            terms += ContainmentOpenRoofPenalty;
        }
        var strength = HolderBase + (terms * HolderFactor);
        foreach (var f in Facilities)
        {
            var n = room.Facilities.GetValueOrDefault(f.Def);
            n = Math.Min(n, f.MaxSimultaneous);
            strength += f.Offset * n;
        }
        return strength;
    }
}

/// <summary>
/// A terrain that raises containment strength: the def and its ContainmentStrength stat value as the catalog's
/// stat table shows it. Its cost and availability are the planning definition's (FloorDefinition).
/// </summary>
public sealed record ContainmentFloor
{
    public string Def { get; init; } = "";

    public double Strength { get; init; }
}

/// <summary>The room-dependent input of a prediction.</summary>
public sealed record ContainmentRoom
{
    /// <summary>The room's mean ground glow, never negative; the planner has no light plan, so it passes 0, the least a room can have.</summary>
    public double MeanGlow { get; init; }

    /// <summary>An indoor room with an unroofed cell.</summary>
    public bool OpenRoof { get; init; }

    /// <summary>How many other holders are in or beside the room.</summary>
    public int OtherHolders { get; init; }

    /// <summary>A room whose every tile is laid with the defs' Floor.</summary>
    public bool Floored { get; init; }

    /// <summary>How many of each facility def (by name) stand within the def's MaxDistance of the holder.</summary>
    public IReadOnlyDictionary<string, int> Facilities { get; init; } = new Dictionary<string, int>();
}

public static partial class PolicyFunctions
{
    /// <summary>Scales the room's mean ground glow.</summary>
    internal const double ContainmentLightingFactor = 10.0;

    /// <summary>Divides the doors' mean hit points.</summary>
    internal const double ContainmentDoorHPDivisor = 5.0;

    /// <summary>Multiplies the lighting, wall, door and floor terms once per other holder in or beside the room.</summary>
    internal const double ContainmentOtherHolderFactor = 0.9;

    /// <summary>The roof term of an indoor room with any open roof cell; a fully roofed room has a zero roof term.</summary>
    internal const double ContainmentOpenRoofPenalty = -30.0;

    /// <summary>
    /// The worker's SimpleCurve from the mean hit points of the room's wall edifices to the wall term:
    /// (0,0), (1000,100), (10000,150), clamped at both ends.
    /// </summary>
    private static readonly double[][] ContainmentWallCurve = [[0, 0], [1000, 100], [10000, 150]];

    internal static double ContainmentWallTerm(double hitPoints)
    {
        var c = ContainmentWallCurve;
        if (hitPoints <= c[0][0])
        {
            return c[0][1];
        }
        for (var i = 1; i < c.Length; i++)
        {
            if (hitPoints <= c[i][0])
            {
                var t = (hitPoints - c[i - 1][0]) / (c[i][0] - c[i - 1][0]);
                return c[i - 1][1] + (t * (c[i][1] - c[i - 1][1]));
            }
        }
        return c[^1][1];
    }
}
