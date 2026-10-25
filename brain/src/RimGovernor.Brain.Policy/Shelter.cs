using SleepingTemperature = RimGovernor.Contracts.Wire.Observations.SleepingTemperature;
using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the shelter family reads (#3257): the retained bed-use review, the sleeping rooms' temperatures and the
/// temperature work the room census owes, and whether the bot-owned areas differ from what they should be. In the
/// immediate pass only <see cref="SafeAreaOwed"/> is read; the rest stays unknown.
/// </summary>
public sealed record ShelterFacts
{
    /// <summary>Whether every colonist owns, and was seen using, a suitable bed and no bedroom step is due.</summary>
    public bool SleepingRecovered { get; init; }

    /// <summary>The coldest and warmest sleeping room, each read as the outdoor temperature when no sleeping room is measured.</summary>
    public double SleepingMin { get; init; }

    public double SleepingMax { get; init; }

    /// <summary>A heat campfire owes its refuel switch, or a sleeping room sits below its sleepers' band.</summary>
    public bool TemperatureOwed { get; init; }

    /// <summary>A door held open to vent a hot room, or to close again, is owed.</summary>
    public Fact<bool> HeatDoorOwed { get; init; }

    /// <summary>The bot-owned allowed areas differ from the enclosed roofed rooms and the plan (SafeAreaCells).</summary>
    public Fact<bool> SafeAreaOwed { get; init; }

    /// <summary>The season's comfort band the sleeping rooms are held inside (<see cref="RoundsPolicy.ColdExit"/>, <see cref="RoundsPolicy.HotExit"/>).</summary>
    public double ColdExit { get; init; }

    public double HotExit { get; init; }

    /// <summary>The facts a review already holds, with <paramref name="policy"/> taken for the season.</summary>
    public static ShelterFacts From(RoundsFacts f, RoundsPolicy policy)
    {
        var p = policy.Seasonal(f.Calendar, f.DisasterConditions);
        return new()
        {
            ColdExit = p.ColdExit,
            HotExit = p.HotExit,
            SleepingRecovered = f.SleepingRecovered,
            SleepingMin = f.SleepingTemperature is SleepingTemperature.Indoor indoor ? indoor.MinC : f.OutdoorTemperature,
            SleepingMax = f.SleepingTemperature is SleepingTemperature.Indoor room ? room.MaxC : f.OutdoorTemperature,
            TemperatureOwed = f.TemperatureOwed,
            HeatDoorOwed = f.Upkeep.Rooms.IsKnown ? Known(DoorControlRules.DoorChanges(f.Upkeep.Rooms, f.Upkeep.Routes ?? throw new RequiredFactException("routes census"), p).Any(d => d.Heat)) : Unknown<bool>(),
            SafeAreaOwed = f.SafeAreaOwed,
        };
    }
}

/// <summary>
/// The shelter family (#3257): the colony's housing, its sleeping rooms' temperature and its safe area, each Need
/// derived only when its service is composed. <c>shelter/sleeping</c> is the day-one floor (stand-in sleeping spots
/// until a sleeping place stands for everyone, #3659), bed ownership, bedrooms and their upgrades; the first rooms are
/// the plan's, selected as any planned room. <c>shelter/temperature</c> keeps the sleeping rooms
/// inside the comfort band and <c>shelter/safe-area</c> (SeriousHarm, so also the immediate pass) keeps the bot-owned
/// allowed areas current. Every Method is building or area work the runtime's work step commits under the Need.
/// </summary>
public sealed class ShelterFamily(bool sleeping, bool temperature, bool safeArea) : IFamily<ShelterFacts>
{
    public const string FamilyId = "shelter";

    /// <summary>The stand-in sleeping spots, bed ownership, bedrooms and their upgrades.</summary>
    public static readonly NeedId Sleeping = NeedId.Of(FamilyId, "sleeping");

    /// <summary>The sleeping rooms' temperature.</summary>
    public static readonly NeedId Temperature = NeedId.Of(FamilyId, "temperature");

    /// <summary>The bot-owned allowed areas (the Safe area edit).</summary>
    public static readonly NeedId SafeArea = NeedId.Of(FamilyId, "safe-area");

    public string Id => FamilyId;

    /// <summary>The colony and pawn counts, the room census and the outdoor temperature.</summary>
    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Rooms, FactFamily.Pawns, FactFamily.World];

    /// <summary>The safe area reads the room census, the world and the emergency.</summary>
    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = safeArea ? [FactFamily.Rooms, FactFamily.World, FactFamily.Emergency] : [];

    public IReadOnlyList<Need> Derive(ShelterFacts facts)
    {
        var needs = new List<Need>(DeriveSeriousHarm(facts));
        if (sleeping)
        {
            needs.Add(ShelterRules.SleepingNeedOf(facts));
        }
        if (temperature)
        {
            needs.Add(ShelterRules.TemperatureNeedOf(facts));
        }
        return needs;
    }

    /// <summary>The safe area, the SeriousHarm Need.</summary>
    public IReadOnlyList<Need> DeriveSeriousHarm(ShelterFacts facts) => safeArea ? [ShelterRules.SafeAreaNeedOf(facts)] : [];

    /// <summary>Nothing pure: every Method is building, assignment or area work the runtime's work step commits.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, ShelterFacts facts) => [];
}

public static partial class ShelterRules
{
    /// <summary>
    /// Bed ownership and bedrooms: met once every colonist was seen using a suitable bed they own and no bedroom step
    /// is due, unmet while one is not. No hysteresis: an open bedroom plan does
    /// not hold it unmet. Its deficit is whole. Its work places the stand-in sleeping spots first (#3659).
    /// </summary>
    public static Need SleepingNeedOf(ShelterFacts f) => FoodRules.WholeNeed(ShelterFamily.Sleeping, NeedTier.Wellbeing, f.SleepingRecovered);

    /// <summary>
    /// The sleeping rooms' temperature, a plain threshold at the exit bars (decision 16): unmet while the coldest
    /// sleeping room (outdoors when none is measured) is below <see cref="RoundsPolicy.ColdExit"/> or the warmest above
    /// <see cref="RoundsPolicy.HotExit"/>, or while a refuel switch, an unheated sleeping room or a venting door is
    /// owed; met otherwise. Its deficit is whole.
    /// </summary>
    public static Need TemperatureNeedOf(ShelterFacts f)
    {
        var band = f.SleepingMin >= f.ColdExit && f.SleepingMax <= f.HotExit;
        var met = !f.TemperatureOwed && !RoundsRules.Positive(f.HeatDoorOwed) && band;
        return FoodRules.WholeNeed(ShelterFamily.Temperature, NeedTier.Wellbeing, met);
    }

    /// <summary>
    /// The safe area (SeriousHarm, the immediate pass): unmet while a bot-owned allowed area edit is owed, met while
    /// none is; an unread room census raises <see cref="RequiredFactException"/>. Its deficit is whole.
    /// </summary>
    public static Need SafeAreaNeedOf(ShelterFacts f) => Whole(ShelterFamily.SafeArea, NeedTier.SeriousHarm, RoundsRules.NotFact(f.SafeAreaOwed), "safe area edits");

    /// <summary>A Need met by <paramref name="met"/>, with a whole deficit; an unread <paramref name="met"/> raises for <paramref name="fact"/>.</summary>
    private static Need Whole(NeedId id, NeedTier tier, Fact<bool> met, string fact) =>
        FoodRules.WholeNeed(id, tier, met.TryGetValue(out var known) ? known : throw new RequiredFactException(fact));

}
