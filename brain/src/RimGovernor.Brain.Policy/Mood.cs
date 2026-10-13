namespace RimGovernor.Brain.Policy;

public enum MoodNeed
{
    [WireName("")]
    None = 0,
    Food,
    Rest,
    Joy,
}

public sealed record MoodPawn
{
    public Fact<MentalState> Break { get; init; }
    public Fact<bool> HighExpectations { get; init; }
    public PawnId Id { get; init; }
    public Fact<double> Mood { get; init; }
    public Fact<double> Threshold { get; init; }
    public Fact<double> Target { get; init; }
    public Fact<double> Food { get; init; }
    public Fact<double> Rest { get; init; }
    public Fact<double> Joy { get; init; }
    public Fact<bool> Mental { get; init; }
    public Fact<bool> Dead { get; init; }
    public Fact<bool> Downed { get; init; }
    public Fact<bool> Drafted { get; init; }
    public Fact<bool> PlayerForced { get; init; }

    /// <summary>The pawn's observed thought rows, negative and positive, most negative first; unknown when the native social block was unreadable. Consumers must be sign-aware.</summary>
    public Fact<IReadOnlyList<MoodThought>> Thoughts { get; init; }

    public void Validate()
    {
        if (Break.TryGetValue(out var state) && (!PolicyFunctions.FoodId(state.DefName) || state.TicksInState < 0))
        {
            throw new DomainException("invalid mental-state evidence");
        }
        if (!PolicyFunctions.FoodId(Id.Value))
        {
            throw new DomainException("invalid mood pawn");
        }
        foreach (var f in new[] { Mood, Threshold, Target, Food, Rest, Joy })
        {
            if (!PolicyFunctions.MoodNumber(f))
            {
                throw new DomainException("nonfinite mood evidence");
            }
        }
        PolicyFunctions.ValidateMoodThoughts(Thoughts);
    }
}

public sealed record MoodCause
{
    public MoodNeed Need { get; init; }
    public Fact<double> Level { get; init; }
}

public sealed record MoodState
{
    public MoodPawn Pawn { get; init; } = new();
    public bool Active { get; init; }
    public bool Missing { get; init; }
    public bool MentalRisk { get; init; }
    public IReadOnlyList<MoodCause> Causes { get; init; } = [];

    /// <summary>The upkeep goals whose facilities would remove the pawn's dominant environment thought pressure (moodProvisioning); empty when need relief is the only measured response.</summary>
    public IReadOnlyList<MoodProvision> Provision { get; init; } = [];

    /// <summary>The pawn's slice of the ledger's unowned bucket: negative thoughts with no audited owner (moodUnowned), recorded when owned provisioning does not apply; the method proposal is then the explicit MoodUnowned blocker once no measured relief remains.</summary>
    public IReadOnlyList<MoodThought> Unowned { get; init; } = [];

    public int Priority()
    {
        if (MentalRisk)
        {
            return 1;
        }
        if (Pawn.Mental.TryGetValue(out var mental) && mental)
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
        var dk = Pawn.Dead.TryGetValue(out var dead);
        var mk = Pawn.Mood.IsKnown;
        var tk = Pawn.Threshold.IsKnown;
        var mentalKnown = Pawn.Mental.TryGetValue(out var mental);
        if (mentalKnown && mental)
        {
            return Finding.Unmet;
        }
        if (!dk || dead || !mk || !tk || !mentalKnown)
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
            PolicyFunctions.ValidateMoodProvision(s.Provision);
            for (var i = 0; i < s.Unowned.Count; i++)
            {
                var t = s.Unowned[i];
                if (PolicyFunctions.ThoughtOwners(t.Def).Count > 0 || t.Offset >= 0 || (i > 0 && s.Unowned[i - 1].Offset > t.Offset))
                {
                    throw new DomainException("invalid mood unowned thought");
                }
            }
            PolicyFunctions.ValidateMoodThoughts(Known(s.Unowned));
            if (s.Provision.Count > 0 && s.Unowned.Count > 0)
            {
                throw new DomainException("mood state both provisioned and unowned");
            }
            seen.Add(s.Pawn.Id);
            var mk = s.Pawn.Mood.TryGetValue(out var m);
            var tk = s.Pawn.Threshold.TryGetValue(out var threshold);
            var nk = s.Pawn.Mental.TryGetValue(out var mental);
            var pk = s.Pawn.Target.TryGetValue(out var target);
            if (!s.Active && (s.Missing || s.MentalRisk || (nk && mental) || (mk && tk && (m <= threshold || (pk && target < m && target <= threshold)))))
            {
                throw new DomainException("observed mood risk marked inactive");
            }
            var causes = new HashSet<MoodNeed>();
            foreach (var c in s.Causes)
            {
                if ((c.Need != MoodNeed.Food && c.Need != MoodNeed.Rest && c.Need != MoodNeed.Joy) || causes.Contains(c.Need) || !PolicyFunctions.MoodNumber(c.Level))
                {
                    throw new DomainException("invalid mood cause");
                }
                if (c.Level.TryGetValue(out var level) && level >= .5)
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

    /// <summary>Defers to the upkeep goal named in the proposal: the pawn's pressure is dominated by environment thoughts that goal's facility removes, so a native relief job would not clear it.</summary>
    [WireName("facility_provision")]
    Provisioned,

    /// <summary>The explicit blocker for pressure dominated by a thought no goal owns (SleptInBarracks, social memories): no measured need relief remains and no facility goal can be raised, so the goal names the thought and waits on native recovery.</summary>
    [WireName("unowned_thought_pressure")]
    Unowned,
}

public sealed record MoodProposal
{
    public PawnId Pawn { get; init; }
    public MoodNeed Need { get; init; }
    public MoodMethodReason Reason { get; init; }
    public ConcernId Concern { get; init; }
    public string Thought { get; init; } = "";
    public double Target { get; init; }
    public Fact<double> NeedBenefit { get; init; }
    public Fact<double> MoodBenefit { get; init; }
}

/// <summary>Ports <c>mood.go</c>.</summary>
public static partial class PolicyFunctions
{
    /// <summary>The per-pawn mood Response: one incident per pawn, keyed by the pawn as its subject.</summary>
    public static readonly ConcernId EnsureMood = new("EnsureMood");

    internal static bool MoodNumber(Fact<double> f) => !f.TryGetValue(out var v) || (!double.IsNaN(v) && !double.IsInfinity(v));

    /// <summary>
    /// The native situational thought a psychic drone puts on every pawn of the gender it targets; the census keeps
    /// it only while it pulls the mood down, so its presence is the drone's own word that this pawn is affected for
    /// the condition's remaining ticks.
    /// </summary>
    public const string PsychicDroneThought = "PsychicDrone";

    /// <summary>Bounds the early entry a drone thought opens: the drone's own offset (12 to 30 mood points across its levels) as a mood fraction, at most this far above the break threshold.</summary>
    private const double PsychicDroneEntryMargin = .15;

    /// <summary>The entry margin a pawn under a psychic drone gets: relief starts while the drone's offset would still leave the pawn above the minor-break threshold. Zero without the thought.</summary>
    private static double PsychicDroneMargin(Fact<IReadOnlyList<MoodThought>> thoughts)
    {
        if (!thoughts.TryGetValue(out var rows))
        {
            return 0;
        }
        foreach (var t in rows)
        {
            if (t.Def == PsychicDroneThought && t.Offset < 0)
            {
                return Math.Min(PsychicDroneEntryMargin, -t.Offset / 100);
            }
        }
        return 0;
    }

    /// <summary>How far above the minor-break threshold a pawn enters mood relief.</summary>
    internal const double MoodEntryMargin = .10;

    /// <summary>How far above it an active pawn must climb before relief ends.</summary>
    private const double MoodRecoveryMargin = .15;

    /// <summary>
    /// Ports native-threshold and need hysteresis. Thought pressure is a present observation, never a prediction of
    /// a break or future mood benefit; a psychic drone thought widens the entry margin for its bearer alone.
    /// </summary>
    public static MoodHistory ReviewMood(Fact<IReadOnlyList<MoodPawn>> observed, MoodHistory previous)
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
                var mk = p.Mood.TryGetValue(out var m);
                var tk = p.Threshold.TryGetValue(out var threshold);
                var mentalKnown = p.Mental.TryGetValue(out var mental);
                var mentalRisk = mentalKnown ? mental : prior.MentalRisk;
                var active = prior.Active;
                var drone = PsychicDroneMargin(p.Thoughts);
                if (mk && tk)
                {
                    var margin = prior.Active ? MoodRecoveryMargin : MoodEntryMargin;
                    margin = Math.Max(margin, drone);
                    if (mentalKnown)
                    {
                        active = mental || m <= threshold + margin;
                    }
                    else if (m <= threshold + margin)
                    {
                        active = true;
                    }
                }
                if (mentalKnown && mental)
                {
                    active = true;
                }
                active = active || mentalRisk;
                if (p.Target.TryGetValue(out var target) && mk && tk && target < m && target <= threshold)
                {
                    active = true;
                }
                var causes = new List<MoodCause>();
                foreach (var (name, levelFact) in new[] { (MoodNeed.Food, p.Food), (MoodNeed.Rest, p.Rest), (MoodNeed.Joy, p.Joy) })
                {
                    var retained = false;
                    foreach (var c in prior.Causes)
                    {
                        retained = retained || c.Need == name;
                    }
                    // Under a drone every need short of the relief target is a cause, so the early entry has a method to act on.
                    var limit = .3;
                    if (retained || drone > 0)
                    {
                        limit = .5;
                    }
                    var k = levelFact.TryGetValue(out var level);
                    if ((k && level < limit) || (!k && retained))
                    {
                        causes.Add(new MoodCause { Need = name, Level = levelFact });
                    }
                }
                var sortedCauses = causes
                    .OrderByDescending(c => c.Level.IsKnown)
                    .ThenBy(c => c.Level.IsKnown ? c.Level.Value : 0)
                    .ThenBy(c => c.Need)
                    .ToList();
                active = active || (prior.Active && sortedCauses.Count > 0);
                IReadOnlyList<MoodProvision> provision = MealMoodProvision(p, MoodProvisioning(p.Thoughts));
                IReadOnlyList<MoodThought> unowned = [];
                if (provision.Count == 0)
                {
                    unowned = MoodUnowned(p.Thoughts);
                }
                if (!p.Thoughts.IsKnown)
                {
                    provision = prior.Provision;
                    unowned = prior.Unowned;
                }
                // Death or unreadable availability cannot certify an active need recovered.
                if (prior.Active && (!p.Dead.TryGetValue(out var dead) || dead))
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
    /// Proposes one bounded native need method, or defers to the upkeep goal that owns the pawn's dominant
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
        if (s.Pawn.Mental.TryGetValue(out var mental) && mental)
        {
            return r with { Reason = MoodMethodReason.MentalBreak };
        }
        if (!s.Active)
        {
            return r with { Reason = MoodMethodReason.Recovered };
        }
        foreach (var f in new[] { s.Pawn.Dead, s.Pawn.Downed, s.Pawn.Drafted })
        {
            if (!f.TryGetValue(out var value) || value)
            {
                return r with { Reason = MoodMethodReason.PlayerWork };
            }
        }
        if (s.Provision.Count > 0)
        {
            return r with { Reason = MoodMethodReason.Provisioned, Concern = s.Provision[0].Concern };
        }
        r = r with { Reason = s.Causes.Count > 0 ? MoodMethodReason.Exhausted : MoodMethodReason.NoCause };
        if (s.Unowned.Count > 0)
        {
            r = r with { Reason = MoodMethodReason.Unowned, Thought = s.Unowned[0].Def };
        }
        foreach (var cause in s.Causes)
        {
            if (!cause.Level.TryGetValue(out var level) || seen.Contains(cause.Need))
            {
                continue;
            }
            return r with { Need = cause.Need, Reason = MoodMethodReason.Relief, Target = .5, NeedBenefit = Known(.5 - level) };
        }
        return r;
    }
}
