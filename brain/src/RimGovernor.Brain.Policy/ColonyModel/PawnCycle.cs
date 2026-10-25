namespace RimGovernor.Brain.Policy;

/// <summary>One pawn's day. Missing mechanics fail the read; days that cannot serve overheads have named states.</summary>
public abstract record PawnCycle
{
    private protected PawnCycle() { }
    // Policy has no game reference; the native skill census names this skill by defName.
    public const string PlantsSkillName = "Plants";
    private const double HoursPerDay = 24;
    public string Pawn { get; init; } = "";
    public int Twelfth { get; init; }
    public EquatableArray<string> Findings { get; init; }

    public sealed record Scheduled : PawnCycle
    {
        public required double SleepHours { get; init; }
        public required double EatingHours { get; init; }
        public required double RecreationHours { get; init; }
        public required double DaylightHours { get; init; }
        public required double FreeHours { get; init; }
        public required double OutdoorHours { get; init; }
        public required double IndoorHours { get; init; }
        public required ForecastThermalSpan OutdoorThermal { get; init; }
    }

    /// <summary>No meal service is observed for a pawn whose food need falls.</summary>
    public sealed record NoMealService : PawnCycle;
    /// <summary>The measured overheads exceed the available day.</summary>
    public sealed record Overbooked : PawnCycle;

    public static EquatableArray<PawnCycle> Year(ColonyModelInput input, ModelPawn pawn) =>
        EquatableArray.From(Enumerable.Range(0, 12).Select(t => Of(input, pawn, t)));

    public static PawnCycle Of(ColonyModelInput input, ModelPawn pawn, int twelfth)
    {
        DomainException.Require(twelfth >= 0 && twelfth < 12, "invalid twelfth");
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var sleep = Split(pawn, "rest", twelfth, findings);
        var recreation = Split(pawn, "joy", twelfth, findings);
        var food = Need(pawn, "food");
        RequireRate(food.Fall, "food fall", pawn);
        if (food.Fall > 0 && food.Service is ModelService.NoneObserved)
            return new NoMealService { Pawn = pawn.Pawn, Twelfth = twelfth, Findings = [$"meal_service_unavailable:{pawn.Pawn}"] };
        var eating = Eating(food, pawn);
        var free = HoursPerDay - sleep - eating - recreation;
        if (free < 0)
        {
            findings.Add($"overheads_exceed_day:{pawn.Pawn}");
            return new Overbooked { Pawn = pawn.Pawn, Twelfth = twelfth, Findings = EquatableArray.From(findings) };
        }
        var climate = input.Climate;
        if (climate.DaylightHours.Count != 12 || climate.Temperatures.Count != 12)
            throw new RequiredFactException("pawn cycle seasonal climate");
        var daylight = climate.DaylightHours[twelfth];
        var outside = Math.Min(free, daylight);
        return new Scheduled
        {
            Pawn = pawn.Pawn, Twelfth = twelfth, SleepHours = sleep, EatingHours = eating, RecreationHours = recreation,
            DaylightHours = daylight, FreeHours = free, OutdoorHours = outside, IndoorHours = free - outside,
            OutdoorThermal = new ForecastThermalSpan(0, outside * GameTime.TicksPerHour, climate.Temperatures[twelfth],
                climate.Temperatures[twelfth], pawn.ComfortableMin, pawn.ComfortableMax),
            Findings = EquatableArray.From(findings),
        };
    }

    public bool Valid()
    {
        switch (this)
        {
        case Scheduled s: return new[] { s.SleepHours, s.EatingHours, s.RecreationHours, s.DaylightHours, s.FreeHours, s.OutdoorHours, s.IndoorHours }
            .All(h => double.IsFinite(h) && h >= 0 && h <= HoursPerDay)
            && Math.Abs(s.SleepHours + s.EatingHours + s.RecreationHours + s.FreeHours - HoursPerDay) < 1e-9
            && Math.Abs(s.OutdoorHours + s.IndoorHours - s.FreeHours) < 1e-9 && s.DaylightHours >= s.OutdoorHours
            && s.OutdoorThermal.Start == 0 && Math.Abs(s.OutdoorThermal.End - s.OutdoorHours * GameTime.TicksPerHour) < 1e-6;
        case NoMealService: return false;
        case Overbooked: return false;
        }
        throw new InvalidOperationException("undeclared pawn cycle");
    }

    public static (bool Available, string Finding) SowGate(ModelPawn pawn, CropChoice crop)
    {
        // CropChoice is hardened by #3820; its missing read already fails this computation.
        if (!crop.SowMinSkill.TryGetValue(out var required))
            throw new RequiredFactException($"sow skill requirement:{crop.Name}");
        var plants = pawn.Skills.FirstOrDefault(s => s.Name == PlantsSkillName)
            ?? throw new RequiredFactException($"plants skill:{pawn.Pawn}");
        return plants.Disabled ? (false, $"plants_disabled:{pawn.Pawn}") : (plants.Level >= required, "");
    }

    private static double Split(ModelPawn pawn, string kind, int twelfth, SortedSet<string> findings)
    {
        var need = Need(pawn, kind);
        RequireRate(need.Fall, kind + " fall", pawn);
        RequireRate(need.Gain, kind + " gain", pawn);
        var hours = Served(need.Fall, need.Gain);
        if (need.OutdoorShares.Count == 12 && need.Gain > need.ShelteredGain)
        {
            var share = need.OutdoorShares[twelfth];
            hours = share * hours + (1 - share) * Served(need.Fall, need.ShelteredGain);
        }
        if (hours == 0) findings.Add($"{kind}_unserved:{pawn.Pawn}");
        return hours;
    }

    private static double Served(double fall, double gain) => gain == 0 ? 0 : HoursPerDay * fall / (fall + gain);
    private static void RequireRate(double rate, string name, ModelPawn pawn)
    {
        if (!double.IsFinite(rate) || rate < 0) throw new RequiredFactException($"{name}:{pawn.Pawn}");
    }
    private static double Eating(double fall, ModelService.Observed service, ModelPawn pawn)
    {
        if (!(service.GainPerUse > 0) || !double.IsFinite(service.GainPerUse))
            throw new RequiredFactException($"meal gain:{pawn.Pawn}");
        RequireRate(service.ServiceTicks, "meal service", pawn);
        RequireRate(service.ApproachTicks, "meal approach", pawn);
        return fall * GameTime.TicksPerDay / service.GainPerUse * (service.ServiceTicks + service.ApproachTicks) / GameTime.TicksPerHour;
    }
    private static double Eating(ModelNeed food, ModelPawn pawn)
    {
        switch (food.Service)
        {
            case ModelService.Observed service: return Eating(food.Fall, service, pawn);
            case ModelService.NoneObserved: return 0; // A zero fall needs no meals; positive falls were refused above.
        }
        throw new InvalidOperationException("undeclared meal service");
    }
    private static ModelNeed Need(ModelPawn pawn, string kind) => pawn.Needs.FirstOrDefault(n => n.Kind == kind)
        ?? throw new RequiredFactException($"{kind} need:{pawn.Pawn}");

    public static IReadOnlyDictionary<string, double> FreeHoursByPawn(ColonyModelInput input, double horizonDays) =>
        FreeHoursByPawn(input, horizonDays, pawns => pawns);

    /// <summary>Mean free hours over the horizon; a pawn without a serviceable day is omitted.</summary>
    public static IReadOnlyDictionary<string, double> FreeHoursByPawn(ColonyModelInput input, double horizonDays,
        Func<IReadOnlyList<ModelPawn>, IEnumerable<ModelPawn>> pawnsOf)
    {
        DomainException.Require(horizonDays > 0 && double.IsFinite(horizonDays), "free hours horizon");
        var output = new Dictionary<string, double>(StringComparer.Ordinal);
        var climate = input.Climate;
        var days = new double[12];
        var twelfth = climate.CurrentTwelfth;
        var span = climate.TicksToNextTwelfth / (double)GameTime.TicksPerDay;
        for (var remaining = horizonDays; remaining > 0; twelfth = (twelfth + 1) % 12, span = ColonyModel.TwelfthDays)
        {
            var spent = Math.Min(remaining, span);
            days[twelfth] += spent;
            remaining -= spent;
        }
        foreach (var pawn in pawnsOf(input.Pawns))
        {
            var total = 0d;
            var whole = true;
            for (var t = 0; t < 12 && whole; t++)
            {
                if (days[t] == 0) continue;
                switch (Of(input, pawn, t))
                {
                    case Scheduled scheduled: total += days[t] * scheduled.FreeHours; break;
                    case NoMealService: whole = false; break;
                    case Overbooked: whole = false; break;
                }
            }
            if (whole) output[pawn.Pawn] = total / horizonDays;
        }
        return output;
    }
}
