using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the shelter family reads (#3257): the foothold counts (a sleeping place and an indoor place for everyone),
/// the retained bed-use review, the population against the indoor capacity, the sleeping rooms' temperatures and the
/// temperature work the room census owes, and whether the bot-owned areas differ from what they should be. In the
/// immediate pass only <see cref="SafeAreaOwed"/> is read; the rest stays unknown.
/// </summary>
public sealed record ShelterFacts
{
    /// <summary>Whether an indoor place stands for everyone (<see cref="PolicyFunctions.FootholdShelter"/>).</summary>
    public Fact<bool> Indoor { get; init; }

    /// <summary>Whether a sleeping place stands for everyone (<see cref="PolicyFunctions.FootholdSleeping"/>).</summary>
    public Fact<bool> Beds { get; init; }

    /// <summary>Whether every colonist owns, and was seen using, a suitable bed and no bedroom step is due.</summary>
    public Fact<bool> SleepingRecovered { get; init; }

    public Fact<long> Colonists { get; init; }

    public Fact<long> IndoorCapacity { get; init; }

    /// <summary>The coldest and warmest sleeping room, each read as the outdoor temperature when no sleeping room is measured.</summary>
    public Fact<double> SleepingMin { get; init; }

    public Fact<double> SleepingMax { get; init; }

    /// <summary>A heat campfire owes its refuel switch, or a sleeping room sits below its sleepers' band.</summary>
    public Fact<bool> TemperatureOwed { get; init; }

    /// <summary>A door held open to vent a hot room, or to close again, is owed.</summary>
    public Fact<bool> HeatDoorOwed { get; init; }

    /// <summary>The bot-owned allowed areas differ from the enclosed roofed rooms and the plan (PlanSafeArea).</summary>
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
            Indoor = PolicyFunctions.FootholdShelter(f),
            Beds = PolicyFunctions.FootholdSleeping(f),
            SleepingRecovered = f.SleepingRecovered,
            Colonists = f.Colonists,
            IndoorCapacity = f.IndoorCapacity,
            SleepingMin = PolicyFunctions.Fallback(f.SleepingMin, f.OutdoorTemperature),
            SleepingMax = PolicyFunctions.Fallback(f.SleepingMax, f.OutdoorTemperature),
            TemperatureOwed = f.TemperatureOwed,
            HeatDoorOwed = f.Upkeep.Rooms.IsKnown ? Known(PolicyFunctions.DoorChanges(f.Upkeep.Rooms, f.Upkeep.Routes, p).Any(d => d.Heat)) : Unknown<bool>(),
            SafeAreaOwed = f.SafeAreaOwed,
        };
    }
}

/// <summary>
/// The shelter family (#3257): the colony's housing, its sleeping rooms' temperature and its safe area, each Need
/// derived only when its service is composed. <c>shelter/starter</c> is the starter shelter with its bunks (a
/// sleeping and an indoor place for everyone); <c>shelter/sleeping</c> is bed ownership, bedrooms and their upgrades,
/// waiting on the starter; <c>shelter/expansion</c> keeps one spare indoor place beyond the population, waiting on
/// both. Those preconditions replace the old housing phase latch. <c>shelter/temperature</c> keeps the sleeping rooms
/// inside the comfort band and <c>shelter/safe-area</c> (SeriousHarm, so also the immediate pass) keeps the bot-owned
/// allowed areas current. Every Method is building or area work the runtime's work step commits under the Need.
/// </summary>
public sealed class ShelterFamily(bool starter, bool sleeping, bool expansion, bool temperature, bool safeArea) : IFamily<ShelterFacts>
{
    public const string FamilyId = "shelter";

    /// <summary>The starter shelter and its bunk rungs.</summary>
    public static readonly NeedId Starter = NeedId.Of(FamilyId, "starter");

    /// <summary>Bed ownership, bedrooms and their upgrades.</summary>
    public static readonly NeedId Sleeping = NeedId.Of(FamilyId, "sleeping");

    /// <summary>One spare indoor place beyond the population.</summary>
    public static readonly NeedId Expansion = NeedId.Of(FamilyId, "expansion");

    /// <summary>The sleeping rooms' temperature.</summary>
    public static readonly NeedId Temperature = NeedId.Of(FamilyId, "temperature");

    /// <summary>The bot-owned allowed areas (the Safe area edit).</summary>
    public static readonly NeedId SafeArea = NeedId.Of(FamilyId, "safe-area");

    /// <summary>Whether <paramref name="key"/> files one of the three housing Needs (a building planner's concern).</summary>
    public static bool Housing(ConcernId key) => key == Starter.FilingKey || key == Sleeping.FilingKey || key == Expansion.FilingKey;

    public string Id => FamilyId;

    /// <summary>The colony and pawn counts, the room census and the outdoor temperature.</summary>
    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Rooms, FactFamily.Pawns, FactFamily.World];

    /// <summary>The safe area reads the room census, the world and the emergency.</summary>
    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = safeArea ? [FactFamily.Rooms, FactFamily.World, FactFamily.Emergency] : [];

    public IReadOnlyList<Need> Derive(ShelterFacts facts)
    {
        var needs = new List<Need>(5);
        if (safeArea)
        {
            needs.Add(PolicyFunctions.SafeAreaNeedOf(facts));
        }
        if (starter)
        {
            needs.Add(PolicyFunctions.StarterShelterNeedOf(facts));
        }
        if (sleeping)
        {
            needs.Add(PolicyFunctions.SleepingNeedOf(facts, starter));
        }
        if (expansion)
        {
            needs.Add(PolicyFunctions.ExpansionNeedOf(facts, starter, sleeping));
        }
        if (temperature)
        {
            needs.Add(PolicyFunctions.TemperatureNeedOf(facts));
        }
        return needs;
    }

    /// <summary>Nothing pure: every Method is building, assignment or area work the runtime's work step commits.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, ShelterFacts facts) => [];
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The starter shelter, a plain threshold: met while a sleeping and an indoor place stand for every colonist
    /// (the housing target when larger), unmet while either is known short, otherwise unknown. Its deficit is whole.
    /// </summary>
    public static Need StarterShelterNeedOf(ShelterFacts f)
    {
        var met = AllFacts(f.Indoor, f.Beds);
        return new Need
        {
            Id = ShelterFamily.Starter,
            Tier = NeedTier.Wellbeing,
            Satisfaction = Satisfy(met),
            Deficit = met.TryGetValue(out var v) ? Known(v ? 0.0 : 1.0) : Unknown<double>(),
            Evidence =
            [
                new("indoor_place", Standing(f.Indoor)),
                new("sleeping_place", Standing(f.Beds)),
            ],
        };
    }

    /// <summary>
    /// Bed ownership and bedrooms, waiting on the starter shelter when it is composed: met once every colonist was seen
    /// using a suitable bed they own and no bedroom step is due, unmet while one is not, unknown while the sleeping
    /// census is. No hysteresis: an open bedroom plan does not hold it unmet. Its deficit is whole.
    /// </summary>
    public static Need SleepingNeedOf(ShelterFacts f, bool starter) => new()
    {
        Id = ShelterFamily.Sleeping,
        Tier = NeedTier.Wellbeing,
        Satisfaction = Satisfy(f.SleepingRecovered),
        Deficit = f.SleepingRecovered.TryGetValue(out var v) ? Known(v ? 0.0 : 1.0) : Unknown<double>(),
        Evidence = [new("sleeping_recovered", Standing(f.SleepingRecovered))],
        Preconditions = starter ? [ShelterFamily.Starter] : [],
    };

    /// <summary>
    /// One spare indoor place beyond the population, waiting on the starter shelter and on sleeping when composed: met
    /// while the indoor capacity reaches the colonists plus <see cref="PolicyConstants.HousingSparePlaces"/>, unmet
    /// below, unknown without a colonist or a capacity. Its deficit is the short share of that target.
    /// </summary>
    public static Need ExpansionNeedOf(ShelterFacts f, bool starter, bool sleeping)
    {
        var met = Unknown<bool>();
        var deficit = Unknown<double>();
        if (f.Colonists.TryGetValue(out var n) && n > 0 && n < long.MaxValue - HousingSparePlaces && f.IndoorCapacity.TryGetValue(out var capacity) && capacity >= 0)
        {
            var target = n + HousingSparePlaces;
            met = Known(capacity >= target);
            deficit = Known(Math.Min(1.0, Math.Max(0.0, ((double)target - capacity) / target)));
        }
        return new Need
        {
            Id = ShelterFamily.Expansion,
            Tier = NeedTier.Development,
            Satisfaction = Satisfy(met),
            Deficit = deficit,
            Evidence =
            [
                new("colonists", Count(f.Colonists)),
                new("indoor_capacity", Count(f.IndoorCapacity)),
            ],
            Preconditions = [.. starter ? [ShelterFamily.Starter] : Array.Empty<NeedId>(), .. sleeping ? [ShelterFamily.Sleeping] : Array.Empty<NeedId>()],
        };
    }

    /// <summary>
    /// The sleeping rooms' temperature, a plain threshold at the exit bars (decision 16): unmet while the coldest
    /// sleeping room (outdoors when none is measured) is below <see cref="RoundsPolicy.ColdExit"/> or the warmest above
    /// <see cref="RoundsPolicy.HotExit"/>, or while a refuel switch, an unheated sleeping room or a venting door is
    /// owed; otherwise unknown while a temperature is, and met. Its deficit is whole.
    /// </summary>
    public static Need TemperatureNeedOf(ShelterFacts f)
    {
        var band = AllFacts(Measured(f.SleepingMin, v => v >= f.ColdExit), Measured(f.SleepingMax, v => v <= f.HotExit));
        var met = Positive(f.TemperatureOwed) || Positive(f.HeatDoorOwed) ? Known(false) : band;
        return new Need
        {
            Id = ShelterFamily.Temperature,
            Tier = NeedTier.Wellbeing,
            Satisfaction = Satisfy(met),
            Deficit = met.TryGetValue(out var v) ? Known(v ? 0.0 : 1.0) : Unknown<double>(),
            Evidence =
            [
                new("sleeping_min_c", Celsius(f.SleepingMin)),
                new("sleeping_max_c", Celsius(f.SleepingMax)),
                new("temperature_owed", Owed(f.TemperatureOwed)),
                new("heat_door_owed", Owed(f.HeatDoorOwed)),
            ],
        };
    }

    /// <summary>
    /// The safe area (SeriousHarm, the immediate pass): unmet while a bot-owned allowed area edit is owed, met while
    /// none is, unknown while the room census is unread. Its deficit is whole.
    /// </summary>
    public static Need SafeAreaNeedOf(ShelterFacts f) => new()
    {
        Id = ShelterFamily.SafeArea,
        Tier = NeedTier.SeriousHarm,
        Satisfaction = Satisfy(NotFact(f.SafeAreaOwed)),
        Deficit = f.SafeAreaOwed.TryGetValue(out var owed) ? Known(owed ? 1.0 : 0.0) : Unknown<double>(),
        Evidence = [new("area_edits", Owed(f.SafeAreaOwed))],
    };

    /// <summary>
    /// Whether the starter shelter holds the colony's Construction labor this review (<see cref="ConstructionLaborHeld"/>):
    /// the foothold sleeping and indoor places are not both known standing. A plain per-review value, not a latch.
    /// </summary>
    internal static bool StarterShelterOwed(RoundsFacts f) => !Positive(AllFacts(FootholdShelter(f), FootholdSleeping(f)));

    private static Satisfaction Satisfy(Fact<bool> met) =>
        met.TryGetValue(out var v) ? v ? Satisfaction.Met : Satisfaction.Unmet : Satisfaction.Unknown;

    private static Fact<string> Standing(Fact<bool> v) =>
        v.TryGetValue(out var b) ? Known(b ? "standing" : "short") : Unknown<string>();

    private static Fact<string> Owed(Fact<bool> v) =>
        v.TryGetValue(out var b) ? Known(b ? "owed" : "none") : Unknown<string>();

    private static Fact<string> Count(Fact<long> v) =>
        v.TryGetValue(out var n) ? Known(n.ToString(CultureInfo.InvariantCulture)) : Unknown<string>();

    private static Fact<string> Celsius(Fact<double> v) =>
        v.TryGetValue(out var c) ? Known(c.ToString("0.#", CultureInfo.InvariantCulture)) : Unknown<string>();
}
