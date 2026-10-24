using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's pawn cycle (#3526 of #3470): one pawn's day in one twelfth of the year, as the labor hours it has and
// what it can do with them. Pure, derived from a ColonyModelInput. A rate the review did not observe leaves the quantities that
// depend on it unknown and is named in Findings, never defaulted. Pawn timetables are not modelled: autopilot owns schedules, so
// the day is its overheads (sleep, eating, recreation) and what remains. Each overhead is the cycle the need runs at its observed
// rates, the level falling while the pawn works and rising while it is served, not a wake/sleep timetable.

/// <summary>One colonist's day in one twelfth of the year.</summary>
public sealed record PawnCycle
{
    /// <summary>The skill sowing is gated by. The brain holds no game reference, so the def name is spelled here as TreePlantation spells it.</summary>
    public const string PlantsSkillName = "Plants";

    private const double HoursPerDay = 24;

    public string Pawn { get; init; } = "";

    public int Twelfth { get; init; }

    /// <summary>Hours a day asleep: the rest need falls while awake and rises in bed, so the day splits fall : fall + gain.</summary>
    public Fact<double> SleepHours { get; init; }

    /// <summary>Hours a day eating: meals a day (food fall over the nutrition of a meal) times the meal's service plus the mean approach.</summary>
    public Fact<double> EatingHours { get; init; }

    /// <summary>Hours a day on recreation: the joy need splits the day as sleep does, over the available recreation's gain.</summary>
    public Fact<double> RecreationHours { get; init; }

    /// <summary>Hours of daylight in this twelfth, from the climate read.</summary>
    public Fact<double> DaylightHours { get; init; }

    /// <summary>24 minus sleep, eating and recreation.</summary>
    public Fact<double> FreeHours { get; init; }

    /// <summary>The free hours that fall in daylight. Outdoor work at night is possible and runs at the glow factor (<c>LayoutLight.LightSpeed</c>).</summary>
    public Fact<double> OutdoorHours { get; init; }

    /// <summary>The free hours not in daylight, spent indoors.</summary>
    public Fact<double> IndoorHours { get; init; }

    /// <summary>
    /// The pawn's outdoor hours as one stationary thermal span (start 0, the twelfth's outdoor temperature held) against its comfort
    /// range, the input of the hypothermia and heatstroke integration of the capability-days ledger (#3521). The game has no speed
    /// factor for temperature, so this is the only thermal term.
    /// </summary>
    public Fact<ForecastThermalSpan> OutdoorThermal { get; init; }

    /// <summary>Each rate or read this cycle lacks, one finding per gap.</summary>
    public EquatableArray<string> Findings { get; init; }

    /// <summary>The twelve cycles of one pawn, indexed by twelfth.</summary>
    public static EquatableArray<PawnCycle> Year(ColonyModelInput input, ModelPawn pawn) => EquatableArray.From(Enumerable.Range(0, 12).Select(t => Of(input, pawn, t)));

    /// <summary>The pawn's day in the given twelfth (0 to 11).</summary>
    public static PawnCycle Of(ColonyModelInput input, ModelPawn pawn, int twelfth)
    {
        DomainException.Require(twelfth >= 0 && twelfth < 12, "invalid twelfth");
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var sleep = Split(pawn, "rest", twelfth, findings);
        var recreation = Split(pawn, "joy", twelfth, findings);
        var eating = Eating(pawn, findings);
        var daylight = Daylight(input, twelfth, findings);
        var left = Left(pawn, twelfth, findings);
        if (left < 0)
        {
            findings.Add($"overheads_exceed_day:{pawn.Pawn}");
        }

        var free = left is double known && known >= 0 ? Known(known) : Unknown<double>();
        var outdoor = Unknown<double>();
        var indoor = Unknown<double>();
        if (left is double hours && hours >= 0 && daylight.TryGetValue(out var sun))
        {
            var outside = Math.Min(hours, sun);
            outdoor = Known(outside);
            indoor = Known(hours - outside);
        }

        return new PawnCycle
        {
            Pawn = pawn.Pawn,
            Twelfth = twelfth,
            SleepHours = sleep,
            EatingHours = eating,
            RecreationHours = recreation,
            DaylightHours = daylight,
            FreeHours = free,
            OutdoorHours = outdoor,
            IndoorHours = indoor,
            OutdoorThermal = Thermal(input, pawn, twelfth, outdoor, findings),
            Findings = EquatableArray.From(findings),
        };
    }

    /// <summary>The cycle's rates are all read and the day is a day: every overhead, the daylight and the thermal span known, and the hours adding up to 24.</summary>
    public bool Valid()
    {
        if (!SleepHours.TryGetValue(out var asleep) || !EatingHours.TryGetValue(out var eats) || !RecreationHours.TryGetValue(out var plays)
            || !DaylightHours.TryGetValue(out var sun) || !FreeHours.TryGetValue(out var free) || !OutdoorHours.TryGetValue(out var outdoor)
            || !IndoorHours.TryGetValue(out var indoor) || !OutdoorThermal.TryGetValue(out var thermal))
        {
            return false;
        }

        return new[] { asleep, eats, plays, sun, free, outdoor, indoor }.All(h => double.IsFinite(h) && h >= 0 && h <= HoursPerDay)
            && Math.Abs(asleep + eats + plays + free - HoursPerDay) < 1e-9 && Math.Abs(outdoor + indoor - free) < 1e-9 && sun >= outdoor
            && thermal.Start == 0 && Math.Abs(thermal.End - (outdoor * GameTime.TicksPerHour)) < 1e-6;
    }

    /// <summary>
    /// Whether the pawn's Plants skill reaches the crop's sowing minimum. A skill or requirement that is unknown makes the task
    /// unavailable and says so, so a gap is never read as permission.
    /// </summary>
    public static (bool Available, string Finding) SowGate(ModelPawn pawn, CropChoice crop)
    {
        if (!crop.SowMinSkill.TryGetValue(out var required))
        {
            return (false, $"sow_skill_requirement_unknown:{crop.Name}");
        }

        if (!pawn.Skills.TryGetValue(out var skills) || skills.FirstOrDefault(s => s.Name == PlantsSkillName) is not { } plants)
        {
            return (false, $"plants_skill_unknown:{pawn.Pawn}");
        }

        return plants.Disabled ? (false, $"plants_disabled:{pawn.Pawn}") : (plants.Level >= required, "");
    }

    // Sleep and recreation: while the pawn is served the level rises at Gain, otherwise it falls at Fall; the day is spent
    // Fall : Fall + Gain on the service once the level cycles between its thresholds. A gain that needs outdoor weather (joy's walk,
    // #3825 D7) serves only the twelfth's OutdoorShares; the rest of the twelfth the pawn takes its best building's ShelteredGain
    // instead, so the hours are the share's mix of the two days. A day with no hours on the need is one nothing serves (no
    // recreation building and no weather for a walk): it is named.
    private static Fact<double> Split(ModelPawn pawn, string kind, int twelfth, SortedSet<string> findings)
    {
        var need = Need(pawn, kind);
        if (need is null || !need.Fall.TryGetValue(out var fall) || fall < 0 || double.IsNaN(fall))
        {
            findings.Add($"{kind}_fall_unknown:{pawn.Pawn}");
            return Unknown<double>();
        }

        if (!need.Gain.TryGetValue(out var gain) || !(gain >= 0))
        {
            findings.Add($"{kind}_gain_unknown:{pawn.Pawn}");
            return Unknown<double>();
        }

        var hours = Served(fall, gain);
        if (need.OutdoorShares.Count == 12 && gain > need.ShelteredGain)
        {
            var share = need.OutdoorShares[twelfth];
            hours = share * hours + (1 - share) * Served(fall, need.ShelteredGain);
        }

        if (hours == 0)
        {
            findings.Add($"{kind}_unserved:{pawn.Pawn}");
        }

        return Known(hours);
    }

    private static double Served(double fall, double gain) => gain == 0 ? 0 : HoursPerDay * fall / (fall + gain);

    private static Fact<double> Eating(ModelPawn pawn, SortedSet<string> findings)
    {
        var need = Need(pawn, "food");
        if (need is null || !need.Fall.TryGetValue(out var fall) || fall < 0)
        {
            findings.Add($"food_fall_unknown:{pawn.Pawn}");
            return Unknown<double>();
        }

        if (!need.GainPerUse.TryGetValue(out var meal) || !(meal > 0) || !need.ServiceTicks.TryGetValue(out var service) || !need.ApproachTicks.TryGetValue(out var approach))
        {
            findings.Add($"meal_unknown:{pawn.Pawn}");
            return Unknown<double>();
        }

        var meals = fall * GameTime.TicksPerDay / meal;
        return Known(meals * (service + approach) / GameTime.TicksPerHour);
    }

    private static ModelNeed? Need(ModelPawn pawn, string kind) => pawn.Needs.FirstOrDefault(n => n.Kind == kind);

    /// <summary>
    /// Per pawn of the input, the mean hours a day its sleep, meals and recreation leave free over the <paramref name="horizonDays"/>
    /// from the review, the time a pawn's value fills with work (#3825): each twelfth's <see cref="FreeHours"/> (its recreation
    /// depends on the twelfth's weather, #3825 D7) weighted by the horizon's days in it, from the climate read's current twelfth and
    /// the ticks to the next. A pawn whose overheads are unread or exceed the day in a twelfth the horizon reaches has no row (its
    /// cycle's findings name why), and no pawn has one without the climate read.
    /// </summary>
    public static IReadOnlyDictionary<string, double> FreeHoursByPawn(ColonyModelInput input, double horizonDays)
    {
        DomainException.Require(horizonDays > 0 && double.IsFinite(horizonDays), "free hours horizon");
        var output = new Dictionary<string, double>(StringComparer.Ordinal);
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        if (!input.Pawns.TryGetValue(out var pawns) || !input.Climate.TryGetValue(out var climate))
        {
            return output;
        }

        var days = new double[12];
        var twelfth = climate.CurrentTwelfth;
        var span = climate.TicksToNextTwelfth / (double)GameTime.TicksPerDay;
        for (var remaining = horizonDays; remaining > 0; twelfth = (twelfth + 1) % 12, span = ColonyModel.TwelfthDays)
        {
            var spent = Math.Min(remaining, span);
            days[twelfth] += spent;
            remaining -= spent;
        }

        foreach (var pawn in pawns)
        {
            var total = 0d;
            var whole = true;
            for (var t = 0; t < 12 && whole; t++)
            {
                if (days[t] == 0)
                {
                    continue;
                }

                if (Left(pawn, t, findings) is double free && free >= 0)
                {
                    total += days[t] * free;
                }
                else
                {
                    whole = false;
                }
            }

            if (whole)
            {
                output[pawn.Pawn] = total / horizonDays;
            }
        }

        return output;
    }

    // The hours sleep, meals and recreation leave of the pawn's day in the twelfth; null while any is unread (findings name it).
    private static double? Left(ModelPawn pawn, int twelfth, SortedSet<string> findings) =>
        Split(pawn, "rest", twelfth, findings).TryGetValue(out var asleep) && Eating(pawn, findings).TryGetValue(out var eats)
            && Split(pawn, "joy", twelfth, findings).TryGetValue(out var plays) ? HoursPerDay - asleep - eats - plays : null;

    private static Fact<double> Daylight(ColonyModelInput input, int twelfth, SortedSet<string> findings)
    {
        if (!input.Climate.TryGetValue(out var climate))
        {
            findings.Add("climate_unknown");
            return Unknown<double>();
        }

        return climate.DaylightHoursIn(twelfth);
    }

    private static Fact<ForecastThermalSpan> Thermal(ColonyModelInput input, ModelPawn pawn, int twelfth, Fact<double> outdoor, SortedSet<string> findings)
    {
        if (!input.Climate.TryGetValue(out var climate) || climate.Temperatures.Count != 12)
        {
            findings.Add("temperature_unknown");
            return Unknown<ForecastThermalSpan>();
        }

        if (!pawn.ComfortableMin.TryGetValue(out var coldest) || !pawn.ComfortableMax.TryGetValue(out var hottest))
        {
            findings.Add($"comfort_range_unknown:{pawn.Pawn}");
            return Unknown<ForecastThermalSpan>();
        }

        if (!outdoor.TryGetValue(out var hours))
        {
            return Unknown<ForecastThermalSpan>();
        }

        var temperature = climate.Temperatures[twelfth];
        return Known(new ForecastThermalSpan(0, hours * GameTime.TicksPerHour, temperature, temperature, coldest, hottest));
    }
}
