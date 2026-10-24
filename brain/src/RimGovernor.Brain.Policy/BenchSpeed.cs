namespace RimGovernor.Brain.Policy;

/// <summary>
/// Where a bench stands, as the room-reading parts of its speed stat see it (#3800): the cell's room role (None for no
/// proper room: Room.ProperRoom fails at the map edge or over 60 regions, and every room stat then answers its
/// roomlessScore), the room's Cleanliness, whether StatPart_Outdoors considers the bench outdoors (Room.OutdoorsForWork,
/// or its cell unroofed) and whether the room is PsychologicallyOutdoors.
/// </summary>
public readonly record struct BenchRoom(RoomRole Role, double Cleanliness, bool Outdoors, bool PsychologicallyOutdoors)
{
    /// <summary>A bench placed outdoors: no proper room, its cell unroofed, psychologically outdoors.</summary>
    public static BenchRoom Outside => new(RoomRole.None, 0, true, true);

    /// <summary>A bench in a closed planned room that serves: roofed (its reconcile owes the roof, epic #3650 P2), its predicted role and cleanliness.</summary>
    public static BenchRoom Closed(RoomRole role, double cleanliness) => new(role, cleanliness, false, false);
}

/// <summary>One StatPart of a bench speed stat, as the def data reads it (#3800). A part multiplies the value it is handed.</summary>
public abstract record BenchStatPart
{
    /// <summary>The factor the part applies to a bench standing in <paramref name="room"/>.</summary>
    public abstract double Factor(BenchRoom room);
}

/// <summary>StatPart_Outdoors: factorOutdoors where the bench is considered outdoors, factorIndoors elsewhere.</summary>
public sealed record BenchOutdoorsPart(double Indoors, double Outdoors) : BenchStatPart
{
    public override double Factor(BenchRoom room) => room.Outdoors ? Outdoors : Indoors;
}

/// <summary>
/// StatPart_WorkTableRoomRole: the def's workTableNotInRoomRoleFactor in a room that is not psychologically outdoors and
/// whose role is not the def's workTableRoomRole (StatPart_WorkTableRoomRole.Applies).
/// </summary>
public sealed record BenchRoomRolePart(RoomRole Role, double NotInRole) : BenchStatPart
{
    public override double Factor(BenchRoom room) => !room.PsychologicallyOutdoors && room.Role != Role ? NotInRole : 1;
}

/// <summary>
/// StatPart_RoomStat over a RoomStatWorker_FromStatByCurve of Cleanliness: the room stat's curve at the room's Cleanliness,
/// its roomlessScore where there is no proper room (Room.GetStat).
/// </summary>
public sealed record BenchCleanlinessPart(EquatableArray<CurvePoint> Curve, double Roomless) : BenchStatPart
{
    public override double Factor(BenchRoom room) => room.Role == RoomRole.None ? Roomless : LayoutFilthRules.EvaluateCurve(Curve, room.Cleanliness);
}

/// <summary>
/// A part the model holds at 1: StatPart_WorkTableTemperature (the bench's temperature is Thermal's and
/// held the same in either room), and a StatPart_RoomStat of RoomStatWorker_ReadingBonus whose roomlessScore is 1 (a planned
/// room holds no bookcase, which scores 1 too).
/// </summary>
public sealed record BenchHeldPart : BenchStatPart
{
    public override double Factor(BenchRoom room) => 1;
}

/// <summary>A part of a class the catalog does not model: evaluating it throws a RequiredFactException naming bench_speed_part and the class.</summary>
public sealed record BenchUnreadPart(string Part) : BenchStatPart
{
    public override double Factor(BenchRoom room) => throw new RequiredFactException("bench_speed_part:" + Part);
}

/// <summary>
/// A bench def's speed stat (#3800: ResearchSpeedFactor), from the def data: the def's statBases value (else the stat's
/// defaultBaseValue), the stat's parts in order and its minValue, the clamp StatWorker.FinalizeValue applies last.
/// Facility offsets (CompAffectedByFacilities) are not modelled.
/// </summary>
public sealed record BenchSpeedModel(double Base, double Min, EquatableArray<BenchStatPart> Parts)
{
    /// <summary>The stat of the bench standing in <paramref name="room"/>. Throws a RequiredFactException for an unread part.</summary>
    public double At(BenchRoom room)
    {
        var value = Base;
        foreach (var part in Parts) value *= part.Factor(room);
        return Math.Max(Min, value);
    }
}
