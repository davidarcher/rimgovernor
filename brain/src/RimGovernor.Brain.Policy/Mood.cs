namespace RimGovernor.Brain.Policy;

public enum MoodNeed
{
    [WireName("")]
    None = 0,
    Food,
    Rest,
    Joy,
}

/// <summary>
/// A colonist's mood need (#3897): <see cref="Has"/> its current level and the <see cref="Has.Target"/> its thoughts carry
/// it to now (the forecast's Need_Mood.CurInstantLevel), or <see cref="Lacks"/> a mood need, with neither.
/// </summary>
public abstract record MoodLevel
{
    private protected MoodLevel() { }

    public sealed record Has(double Level, double Target) : MoodLevel;

    public sealed record Lacks : MoodLevel;
}

/// <summary>
/// One colonist's mood evidence, read whole from its pawn row (#3768). A need the pawn does not have is
/// <see cref="NeedLevel.Lacks"/> (<see cref="MoodLevel.Lacks"/> for mood), never an absent number.
/// </summary>
public sealed record MoodPawn
{
    /// <summary>The active native break; null when the pawn is in none.</summary>
    public MentalState? Break { get; init; }
    public bool HighExpectations { get; init; }
    public PawnId Id { get; init; }
    public MoodLevel Mood { get; init; } = new MoodLevel.Lacks();
    public double Threshold { get; init; }
    public NeedLevel Food { get; init; } = new NeedLevel.Lacks();
    public NeedLevel Rest { get; init; } = new NeedLevel.Lacks();
    public NeedLevel Joy { get; init; } = new NeedLevel.Lacks();
    public bool Mental { get; init; }
    public bool Dead { get; init; }
    public bool Downed { get; init; }
    public bool Drafted { get; init; }
    public bool PlayerForced { get; init; }

    /// <summary>The pawn's observed thought rows, negative and positive, most negative first. Consumers must be sign-aware.</summary>
    public EquatableArray<MoodThought> Thoughts { get; init; }

    public void Validate()
    {
        if (!FoodForecastRules.FoodId(Id.Value))
        {
            throw new DomainException("invalid mood pawn");
        }
        if (!double.IsFinite(Threshold) || Mood is MoodLevel.Has { Level: var mood, Target: var target } && (!double.IsFinite(mood) || !double.IsFinite(target)))
        {
            throw new DomainException("nonfinite mood evidence");
        }
        foreach (var level in new[] { Food, Rest, Joy })
        {
            if (level is NeedLevel.Has { Level: var v } && !double.IsFinite(v))
            {
                throw new DomainException("nonfinite mood evidence");
            }
        }
        MoodRules.ValidateMoodThoughts(Thoughts);
    }
}

public sealed record MoodCause
{
    public MoodNeed Need { get; init; }
    public double Level { get; init; }
}

public sealed record MoodState
{
    public MoodPawn Pawn { get; init; } = new();
    public bool Active { get; init; }
    public bool Missing { get; init; }
    public bool MentalRisk { get; init; }
    public IReadOnlyList<MoodCause> Causes { get; init; } = [];

    /// <summary>The Needs whose facilities would remove the pawn's dominant environment thought pressure (moodProvisioning); empty when need relief is the only measured response.</summary>
    public IReadOnlyList<MoodProvision> Provision { get; init; } = [];

    /// <summary>The pawn's slice of the ledger's unowned bucket: negative thoughts with no audited owner (moodUnowned), recorded when owned provisioning does not apply; the method proposal is then the explicit Unowned blocker once no measured relief remains.</summary>
    public IReadOnlyList<MoodThought> Unowned { get; init; } = [];

    public int Priority()
    {
        if (MentalRisk || Pawn.Mental)
        {
            return 1;
        }
        return 2;
    }

    public Finding Need()
    {
        if (Missing)
        {
            return Finding.Unclear;
        }
        if (Pawn.Mental)
        {
            return Finding.Unmet;
        }
        if (Pawn.Dead || Pawn.Mood is not MoodLevel.Has)
        {
            return Finding.Unclear;
        }
        return Active ? Finding.Unmet : Finding.Met;
    }
}

public sealed record MoodHistory
{
    public IReadOnlyList<MoodState> States { get; init; } = [];

    public void Validate()
    {
        var seen = new HashSet<PawnId>();
        foreach (var s in States)
        {
            s.Pawn.Validate();
            if (seen.Contains(s.Pawn.Id) || s.Causes.Count > 3)
            {
                throw new DomainException("invalid mood history");
            }
            MoodRules.ValidateMoodProvision(s.Provision);
            for (var i = 0; i < s.Unowned.Count; i++)
            {
                var t = s.Unowned[i];
                if (MoodRules.ThoughtOwners(t.Def).Count > 0 || t.Offset >= 0 || (i > 0 && s.Unowned[i - 1].Offset > t.Offset))
                {
                    throw new DomainException("invalid mood unowned thought");
                }
            }
            MoodRules.ValidateMoodThoughts(s.Unowned);
            if (s.Provision.Count > 0 && s.Unowned.Count > 0)
            {
                throw new DomainException("mood state both provisioned and unowned");
            }
            seen.Add(s.Pawn.Id);
            var threshold = s.Pawn.Threshold;
            if (!s.Active && (s.Missing || s.MentalRisk || s.Pawn.Mental
                || (s.Pawn.Mood is MoodLevel.Has { Level: var m, Target: var target } && (m <= threshold || (target < m && target <= threshold)))))
            {
                throw new DomainException("observed mood risk marked inactive");
            }
            var causes = new HashSet<MoodNeed>();
            foreach (var c in s.Causes)
            {
                if ((c.Need != MoodNeed.Food && c.Need != MoodNeed.Rest && c.Need != MoodNeed.Joy) || causes.Contains(c.Need) || !double.IsFinite(c.Level))
                {
                    throw new DomainException("invalid mood cause");
                }
                if (c.Level >= .5)
                {
                    throw new DomainException("recovered mood cause retained");
                }
                causes.Add(c.Need);
            }
        }
    }
}

public enum MoodMethodReason
{
    [WireName("")]
    None = 0,
    Recovered,

    [WireName("unknown_mood_or_pawn")]
    Unavailable,

    [WireName("native_mental_break")]
    MentalBreak,

    [WireName("pawn_unavailable_or_player_work")]
    PlayerWork,

    [WireName("no_measured_correctable_need")]
    NoCause,

    [WireName("bounded_methods_exhausted")]
    Exhausted,

    [WireName("native_need_relief")]
    Relief,

    /// <summary>Defers to the Need named in the proposal: the pawn's pressure is dominated by environment thoughts that Need's facility removes, so a native relief job would not clear it.</summary>
    [WireName("facility_provision")]
    Provisioned,

    /// <summary>The explicit blocker for pressure dominated by a thought no Need owns (SleptInBarracks, social memories): no measured need relief remains and no facility Need can be raised, so the proposal names the thought and waits on native recovery.</summary>
    [WireName("unowned_thought_pressure")]
    Unowned,
}

public sealed record MoodProposal
{
    public PawnId Pawn { get; init; }
    public MoodNeed Need { get; init; }
    public MoodMethodReason Reason { get; init; }
    /// <summary>The Need a <see cref="MoodMethodReason.Provisioned"/> proposal defers to; empty otherwise.</summary>
    public NeedId Provider { get; init; }
    public string Thought { get; init; } = "";
    public double Target { get; init; }

    /// <summary>The need level a <see cref="MoodMethodReason.Relief"/> proposal restores (its target less the cause's level); 0 for any other, which relieves no need.</summary>
    public double NeedBenefit { get; init; }
}

public static partial class MoodRules
{
    /// <summary>
    /// The native situational thought a psychic drone puts on every pawn of the gender it targets; the census keeps
    /// it only while it pulls the mood down, so its presence is the drone's own word that this pawn is affected for
    /// the condition's remaining ticks.
    /// </summary>
    public const string PsychicDroneThought = "PsychicDrone";

    /// <summary>Bounds the early entry a drone thought opens: the drone's own offset (12 to 30 mood points across its levels) as a mood fraction, at most this far above the break threshold.</summary>
    private const double PsychicDroneEntryMargin = .15;

    /// <summary>The margin a pawn under a psychic drone gets: relief starts while the drone's offset would still leave the pawn above the minor-break threshold. Zero without the thought.</summary>
    private static double PsychicDroneMargin(EquatableArray<MoodThought> thoughts)
    {
        foreach (var t in thoughts)
        {
            if (t.Def == PsychicDroneThought && t.Offset < 0)
            {
                return Math.Min(PsychicDroneEntryMargin, -t.Offset / 100);
            }
        }
        return 0;
    }

    /// <summary>
    /// How far above the minor-break threshold a pawn's mood is owed relief: one bar, the old recovery bar, with no
    /// separate entry bar (#3231 decision 26, #3265: a Need is a plain threshold).
    /// </summary>
    internal const double MoodReliefMargin = .15;

    /// <summary>
    /// Reviews each pawn against the native threshold plus <see cref="MoodReliefMargin"/> and its needs against one
    /// cause bar, with no hysteresis band (decision 16): the prior state is read only where a fact is unknown. Thought
    /// pressure is a present observation, never a prediction of a break or future mood benefit; a psychic drone thought
    /// widens the margin and the cause bar for its bearer alone.
    /// </summary>
    public static MoodHistory ReviewMood(Fact<EquatableArray<MoodPawn>> observed, MoodHistory previous)
    {
        previous.Validate();
        var old = new Dictionary<PawnId, MoodState>();
        foreach (var s in previous.States)
        {
            old[s.Pawn.Id] = s;
        }
        var known = observed.TryGetValue(out var rows);
        var states = new List<MoodState>();
        var seen = new HashSet<PawnId>();
        if (known)
        {
            foreach (var p in rows!)
            {
                p.Validate();
                if (seen.Contains(p.Id))
                {
                    throw new DomainException("duplicate mood pawn");
                }
                seen.Add(p.Id);
                var prior = old.GetValueOrDefault(p.Id) ?? new MoodState();
                var threshold = p.Threshold;
                var mentalRisk = p.Mental;
                var active = prior.Active;
                var drone = PsychicDroneMargin(p.Thoughts);
                // A pawn without a mood need keeps its prior activity: nothing measures it recovered.
                if (p.Mood is MoodLevel.Has { Level: var m, Target: var target })
                {
                    active = p.Mental || m <= threshold + Math.Max(MoodReliefMargin, drone);
                    if (target < m && target <= threshold)
                    {
                        active = true;
                    }
                }
                active = active || mentalRisk;
                var causes = new List<MoodCause>();
                foreach (var (name, need) in new[] { (MoodNeed.Food, p.Food), (MoodNeed.Rest, p.Rest), (MoodNeed.Joy, p.Joy) })
                {
                    // Under a drone every need short of the relief target is a cause, so the early entry has a method to act on.
                    var limit = drone > 0 ? .5 : .3;
                    if (need is NeedLevel.Has { Level: var level } && level < limit)
                    {
                        causes.Add(new MoodCause { Need = name, Level = level });
                    }
                }
                var sortedCauses = causes.OrderBy(c => c.Level).ThenBy(c => c.Need).ToList();
                IReadOnlyList<MoodProvision> provision = MealMoodProvision(p, MoodProvisioning(p.Thoughts));
                IReadOnlyList<MoodThought> unowned = [];
                if (provision.Count == 0)
                {
                    unowned = MoodUnowned(p.Thoughts);
                }
                // Death cannot certify an active need recovered.
                if (prior.Active && p.Dead)
                {
                    active = true;
                }
                if (active || prior.Pawn.Id.Value != "")
                {
                    states.Add(new MoodState { Pawn = p, Active = active, MentalRisk = mentalRisk, Causes = sortedCauses, Provision = provision, Unowned = unowned });
                }
            }
        }
        foreach (var prior in previous.States)
        {
            if (!seen.Contains(prior.Pawn.Id) && prior.Active)
            {
                states.Add(prior with { Missing = true });
            }
        }
        var result = new MoodHistory { States = [.. states.OrderBy(s => s.Pawn.Id)] };
        result.Validate();
        return result;
    }

    /// <summary>
    /// Proposes one bounded native need method, or defers to the Need that owns the pawn's dominant
    /// environment pressure. The action family must still obtain current job/schedule admission and observe actual
    /// recovery.
    /// </summary>
    public static MoodProposal SelectMoodMethod(MoodState s, IReadOnlyList<MoodNeed> used)
    {
        var r = new MoodProposal { Pawn = s.Pawn.Id };
        new MoodHistory { States = [s] }.Validate();
        var seen = new HashSet<MoodNeed>();
        foreach (var n in used)
        {
            if ((n != MoodNeed.Food && n != MoodNeed.Rest && n != MoodNeed.Joy) || seen.Contains(n))
            {
                throw new DomainException("invalid used mood method");
            }
            seen.Add(n);
        }
        if (s.Missing || s.Need() == Finding.Unclear)
        {
            return r with { Reason = MoodMethodReason.Unavailable };
        }
        if (s.Pawn.Mental)
        {
            return r with { Reason = MoodMethodReason.MentalBreak };
        }
        if (!s.Active)
        {
            return r with { Reason = MoodMethodReason.Recovered };
        }
        if (s.Pawn.Dead || s.Pawn.Downed || s.Pawn.Drafted)
        {
            return r with { Reason = MoodMethodReason.PlayerWork };
        }
        if (s.Provision.Count > 0)
        {
            return r with { Reason = MoodMethodReason.Provisioned, Provider = s.Provision[0].Need };
        }
        r = r with { Reason = s.Causes.Count > 0 ? MoodMethodReason.Exhausted : MoodMethodReason.NoCause };
        if (s.Unowned.Count > 0)
        {
            r = r with { Reason = MoodMethodReason.Unowned, Thought = s.Unowned[0].Def };
        }
        foreach (var cause in s.Causes)
        {
            if (seen.Contains(cause.Need))
            {
                continue;
            }
            return r with { Need = cause.Need, Reason = MoodMethodReason.Relief, Target = .5, NeedBenefit = .5 - cause.Level };
        }
        return r;
    }
}
