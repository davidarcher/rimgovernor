namespace RimGovernor.Brain.Simulation.Combat;

/// <summary>
/// The per-part injury model of one pawn kind: the body def's parts with the health, coverage and capacity
/// roles the ported rules read, and the scalars those rules take from the pawn and the damage def. The
/// importer derives it from the catalog; every rule here is a port of the pinned game's code, named on the
/// method, and approximations.md (A1, A33, A34, A39) lists what is left out.
/// </summary>
public sealed class Body
{
    /// <summary>The body tree in pre-order, Parts[0] the core part, every parent before its children.</summary>
    public IReadOnlyList<BodyPart> Parts { get; init; } = [];

    /// <summary>Pawn.HealthScale.</summary>
    public double HealthScale { get; init; }

    /// <summary>The injury hediff's injuryProps.painPerSeverity.</summary>
    public double PainPerSeverity { get; init; }

    /// <summary>The MissingBodyPart hediff's painPerSeverity.</summary>
    public double MissingPainPerSeverity { get; init; }

    /// <summary>The PainShockThreshold stat; Pawn_HealthTracker.InPainShock downs a pawn whose total pain reaches it.</summary>
    public double PainShock { get; init; }

    /// <summary>Pawn_HealthTracker.LethalDamageThreshold, 150 x HealthScale.</summary>
    public double LethalDamage { get; init; }

    /// <summary>The IncomingDamageFactor stat applied to violent damage after armor.</summary>
    public double IncomingDamageFactor { get; init; }

    /// <summary>The damage def's harmAllLayersUntilOutside.</summary>
    public bool HarmAllLayers { get; init; }

    /// <summary>The low end of the damage def's overkillPctToDestroyPart range.</summary>
    public double OverkillLow { get; init; }

    /// <summary>The high end of the damage def's overkillPctToDestroyPart range.</summary>
    public double OverkillHigh { get; init; }

    /// <summary>The Moving capacity's minForCapable.</summary>
    public double MovingMinForCapable { get; init; }

    /// <summary>The injury hediff's injuryProps.bleedRate.</summary>
    public double BleedPerSeverity { get; init; }

    /// <summary>The MissingBodyPart hediff's bleed rate.</summary>
    public double MissingBleedPerSeverity { get; init; }

    /// <summary>The race's bleedRateFactor, 0 for a pawn that cannot bleed (no blood def or not flesh).</summary>
    public double BleedFactor { get; init; }

    /// <summary>The injury hediff's injuryProps.canMerge. The bleed fields feed the serious-injury stop readings (A22); a zero value reads no bleeding.</summary>
    public bool MergeInjuries { get; init; }

    // Tag lists the capacity workers read: the parts carrying each body part tag. Empty leaves the capacity
    // part efficiency at 1 (CalculateTagEfficiency).
    public IReadOnlyList<int> Consciousness { get; init; } = [];

    public IReadOnlyList<int> BloodPumping { get; init; } = [];

    public IReadOnlyList<int> BreathingSource { get; init; } = [];

    public IReadOnlyList<int> BreathingPathway { get; init; } = [];

    public IReadOnlyList<int> BreathingCage { get; init; } = [];

    public IReadOnlyList<int> FiltrationKidney { get; init; } = [];

    public IReadOnlyList<int> FiltrationLiver { get; init; } = [];

    public IReadOnlyList<int> FiltrationSource { get; init; } = [];

    public IReadOnlyList<int> Metabolism { get; init; } = [];

    public IReadOnlyList<int> Pelvis { get; init; } = [];

    public IReadOnlyList<int> Spine { get; init; } = [];

    /// <summary>The parts tagged SightSource (the eyes), read by PawnCapacityWorker_Sight; empty leaves the Sight capacity at 1.</summary>
    public IReadOnlyList<int> Sight { get; init; } = [];

    /// <summary>The limbs the Moving capacity averages.</summary>
    public IReadOnlyList<Limb> Moving { get; init; } = [];

    /// <summary>The limbs the Manipulation capacity averages.</summary>
    public IReadOnlyList<Limb> Manipulation { get; init; } = [];

    // The BloodLoss hediff (Core Hediffs_Global_Misc.xml) and the giver that grows it
    // (Verse.HediffGiver_Bleeding, run by Pawn_HealthTracker.HealthTickInterval when
    // pawn.IsHashIntervalTick(60)): severity rises by bleed rate x 0.001 an interval while the rate is at
    // least 0.1 and falls by 1/3000 otherwise; it is lethal at 1. Its stages (minSeverity 0, 0.15, 0.30,
    // 0.45, 0.60) offset the Consciousness capacity by 0, -0.1, -0.2, -0.4, -0.4 and the last also caps it
    // at 0.1.
    internal const int BloodLossInterval = 60;
    internal const double BloodLossPerBleed = 0.001;
    internal const double BloodLossRecovery = 0.00033333333;
    internal const double BloodLossMinBleed = 0.1;
    internal const double BloodLossLethal = 1.0;
    internal const int BloodLossStageCount = 5;

    /// <summary>PawnCapacityWorker_Sight.PartEfficiencySpecialWeight.</summary>
    private const double SightSpecialWeight = 0.75;

    private static readonly double[] BloodStageMin = [0, 0.15, 0.30, 0.45, 0.60];
    private static readonly double[] BloodStageOffset = [0, -0.1, -0.2, -0.4, -0.4];

    /// <summary>The index of the BloodLoss stage a severity falls in (Hediff.CurStageIndex: the last stage whose minSeverity it reaches).</summary>
    internal static int BloodStage(double sev)
    {
        var i = 0;
        for (var j = 0; j < BloodStageMin.Length; j++)
        {
            if (sev >= BloodStageMin[j])
            {
                i = j;
            }
        }
        return i;
    }

    /// <summary>
    /// Ports HediffGiver_Bleeding.OnIntervalPassed for one interval and reports whether the hediff changed
    /// stage or became lethal, the cases in which Hediff.Severity notifies the health tracker
    /// (CheckForStateChange).
    /// </summary>
    internal bool BloodTick(InjuryState st)
    {
        var before = BloodStage(st.Blood);
        var rate = BleedRate(st);
        if (rate >= BloodLossMinBleed)
        {
            st.Blood += rate * BloodLossPerBleed;
        }
        else
        {
            st.Blood = Math.Max(st.Blood - BloodLossRecovery, 0);
        }
        if (st.Blood >= BloodLossLethal)
        {
            st.Blood = BloodLossLethal;
            return true;
        }
        return BloodStage(st.Blood) != before;
    }

    /// <summary>Reports why the body cannot be simulated, or the empty string.</summary>
    internal string Validate()
    {
        var n = Parts.Count;
        if (n == 0)
        {
            return "has no parts";
        }
        for (var i = 0; i < n; i++)
        {
            var p = Parts[i];
            if ((i == 0 && p.Parent != -1) || (i > 0 && (p.Parent < 0 || p.Parent >= i)))
            {
                return "has a part whose parent does not come before it";
            }
            if (!(p.MaxHealth >= 1) || p.Coverage < 0 || p.HitWeight < 0)
            {
                return "has a part with no health or a negative coverage or hit weight";
            }
        }
        if (!(HealthScale > 0) || !(PainPerSeverity > 0) || MissingPainPerSeverity < 0 || !(PainShock > 0) ||
            !(LethalDamage > 0) || !(IncomingDamageFactor >= 0) || OverkillLow < 0 || OverkillHigh < OverkillLow)
        {
            return "has an invalid scalar (health scale, pain, shock or lethal threshold)";
        }
        var hittable = 0.0;
        foreach (var p in Parts)
        {
            hittable += p.HitWeight;
        }
        if (!(hittable > 0))
        {
            return "has no part a hit can land on";
        }
        bool InRange(IReadOnlyList<int> idx) => idx.All(i => i >= 0 && i < n);
        foreach (var l in Moving.Concat(Manipulation))
        {
            if (l.Core < 0 || l.Core >= n || !InRange(l.Segments) || !InRange(l.Digits))
            {
                return "has a limb naming a part that is not in the body";
            }
        }
        IReadOnlyList<int>[] tags =
        [
            Consciousness, BloodPumping, BreathingSource, BreathingPathway, BreathingCage,
            FiltrationKidney, FiltrationLiver, FiltrationSource, Metabolism, Pelvis, Spine,
        ];
        if (tags.Any(t => !InRange(t)))
        {
            return "has a capacity tag naming a part that is not in the body";
        }
        return "";
    }

    /// <summary>
    /// Ports HediffSet.GetPartHealth for a part with no added parts: its maximum less its injuries, rounded,
    /// never below 1 when the part cannot be destroyed by damage, 0 once missing.
    /// </summary>
    internal double PartHealth(InjuryState st, int i)
    {
        if (st.Missing[i])
        {
            return 0;
        }
        var p = Parts[i];
        var h = Math.Max(p.MaxHealth - st.Sev[i], 0);
        if (!p.Destroyable)
        {
            h = Math.Max(h, 1);
        }
        return Math.Round(h, MidpointRounding.ToEven);
    }

    /// <summary>DamageWorker_AddInjury.ShouldReduceDamageToPreservePart: an outside part that is not the core.</summary>
    internal bool PreservesOutside(int i) => i != 0 && !Parts[i].Inside;

    /// <summary>
    /// Ports PawnCapacityUtility.CalculatePartEfficiency for a part with no added part and no stage that
    /// moves efficiency (the importer refuses an injury hediff with stages).
    /// </summary>
    internal double PartEfficiency(InjuryState st, int i)
    {
        var par = Parts[i].Parent;
        if (par >= 0 && st.Missing[par])
        {
            return 0;
        }
        var frac = PartHealth(st, i) / Parts[i].MaxHealth;
        if (frac == 1)
        {
            return 1;
        }
        if (PreservesOutside(i))
        {
            frac = Clamp01((frac - 0.1) / 0.9);
        }
        return Math.Max(frac, 0);
    }

    internal static double Clamp01(double f) => Math.Min(Math.Max(f, 0), 1);

    /// <summary>
    /// Ports PawnCapacityWorker_Sight: CalculateTagEfficiency over the SightSource parts with
    /// PartEfficiencySpecialWeight 0.75, which weights the best eye 0.75 and the mean of the others 0.25 when
    /// there are two or more.
    /// </summary>
    internal double SightEfficiency(InjuryState st)
    {
        var n = Sight.Count;
        if (n == 0)
        {
            return 1;
        }
        double sum = 0, best = 0;
        foreach (var i in Sight)
        {
            var e = PartEfficiency(st, i);
            sum += e;
            best = Math.Max(best, e);
        }
        if (n < 2)
        {
            return sum / n;
        }
        return (best * SightSpecialWeight) + ((sum - best) / (n - 1) * (1 - SightSpecialWeight));
    }

    /// <summary>
    /// Ports PawnCapacityUtility.CalculateTagEfficiency with no lerp range: the mean efficiency of the parts
    /// with the tag, capped at <paramref name="maximum"/>, 1 for a body with no such part.
    /// </summary>
    internal double TagEfficiency(InjuryState st, IReadOnlyList<int> parts, double maximum)
    {
        if (parts.Count == 0)
        {
            return 1;
        }
        var sum = 0.0;
        foreach (var i in parts)
        {
            sum += PartEfficiency(st, i);
        }
        return Math.Min(sum / parts.Count, maximum);
    }

    /// <summary>
    /// Ports PawnCapacityUtility.CalculateLimbEfficiency: the mean over limb cores of the core's efficiency
    /// times its connected segments', with the digits' mean efficiency lerped in by the appendage weight. It
    /// also returns the share of limbs still working.
    /// </summary>
    internal (double Level, double Functional) LimbEfficiency(InjuryState st, IReadOnlyList<Limb> limbs, double appendageWeight)
    {
        if (limbs.Count == 0)
        {
            return (0, 0);
        }
        var sum = 0.0;
        var working = 0;
        foreach (var l in limbs)
        {
            var e = PartEfficiency(st, l.Core);
            foreach (var s in l.Segments)
            {
                e *= PartEfficiency(st, s);
            }
            if (l.Digits.Count > 0)
            {
                var avg = 0.0;
                foreach (var d in l.Digits)
                {
                    avg += PartEfficiency(st, d);
                }
                avg /= l.Digits.Count;
                e += ((e * avg) - e) * appendageWeight; // Mathf.Lerp(e, e*avg, weight)
            }
            sum += e;
            if (e > 0)
            {
                working++;
            }
        }
        return (sum / limbs.Count, (double)working / limbs.Count);
    }

    /// <summary>
    /// Ports HediffSet.CalculatePain for a pawn with only injuries and lost parts: each injury's severity x
    /// painPerSeverity, a freshly lost outside, non-solid part's maximum health x the missing part's
    /// painPerSeverity (a lost part whose parent is also lost hurts only through the parent), over
    /// HealthScale, clamped to [0, 1].
    /// </summary>
    internal double Pain(InjuryState st)
    {
        var total = st.Total * PainPerSeverity;
        for (var i = 0; i < Parts.Count; i++)
        {
            var p = Parts[i];
            if (st.Fresh[i] && !p.Inside && !p.Solid && (p.Parent < 0 || !st.Missing[p.Parent]))
            {
                total += p.MaxHealth * MissingPainPerSeverity;
            }
        }
        return Clamp01(total / HealthScale);
    }

    /// <summary>GenMath.RoundedHundredth.</summary>
    internal static double RoundedHundredth(double f) => Math.Round(f * 100, MidpointRounding.ToEven) / 100;

    /// <summary>
    /// Ports PawnCapacityUtility.CalculateCapacityLevel for the capacities Pawn_HealthTracker reads (the four
    /// vital ones, Metabolism, Consciousness, Moving and Manipulation) through their workers, with the
    /// capacity defs' zeroIfCannotBeAwake. Hediffs carry no capacity modifiers here (A39).
    /// </summary>
    internal Capacities Levels(InjuryState st)
    {
        var pumping = RoundedHundredth(TagEfficiency(st, BloodPumping, double.MaxValue));
        var breathing = RoundedHundredth(TagEfficiency(st, BreathingSource, double.MaxValue) *
            TagEfficiency(st, BreathingPathway, 1) * TagEfficiency(st, BreathingCage, 1));
        double filtration;
        if (FiltrationKidney.Count > 0)
        {
            filtration = TagEfficiency(st, FiltrationKidney, double.MaxValue) * TagEfficiency(st, FiltrationLiver, double.MaxValue);
        }
        else
        {
            filtration = TagEfficiency(st, FiltrationSource, double.MaxValue);
        }
        filtration = RoundedHundredth(filtration);
        var metabolism = RoundedHundredth(TagEfficiency(st, Metabolism, double.MaxValue));

        // PawnCapacityWorker_Consciousness.
        var c = TagEfficiency(st, Consciousness, double.MaxValue);
        var off = Math.Min(Math.Max(0 + ((Pain(st) - 0.1) / 0.9 * 0.4), 0), 0.4);
        if (off >= 0.01)
        {
            c -= off;
        }

        static double Lerp(double a, double z, double t) => a + ((z - a) * Clamp01(t));
        c = Lerp(c, c * Math.Min(pumping, 1), 0.2);
        c = Lerp(c, c * Math.Min(breathing, 1), 0.2);
        c = Lerp(c, c * Math.Min(filtration, 1), 0.1);
        if (c > 0)
        {
            // PawnCapacityUtility.CalculateCapacityLevel: the hediff capMods, BloodLoss's only.
            var stage = BloodStage(st.Blood);
            c += BloodStageOffset[stage];
            if (stage == BloodLossStageCount - 1)
            {
                c = Math.Min(c, 0.1);
            }
        }
        c = RoundedHundredth(Math.Max(c, 0));

        var consciousness = c;
        var sight = RoundedHundredth(Math.Max(SightEfficiency(st), 0)); // no hediff here moves it
        var lethalLost = !(c > 0) || !(pumping > 0) || !(breathing > 0) || !(filtration > 0) || !(metabolism > 0);
        double moving = 0, manipulation = 0;
        if (!(c >= 0.3))
        {
            // PawnCapacitiesHandler.CanBeAwake: both limb capacities are zeroIfCannotBeAwake.
            return new Capacities(consciousness, moving, manipulation, sight, lethalLost);
        }

        // PawnCapacityWorker_Moving.
        var (lvl, functional) = LimbEfficiency(st, Moving, 0.4);
        if (functional >= 0.4999)
        {
            var m = lvl * TagEfficiency(st, Pelvis, double.MaxValue) * TagEfficiency(st, Spine, double.MaxValue);
            m = Lerp(m, m * breathing, 0.2);
            m = Lerp(m, m * pumping, 0.2);
            moving = RoundedHundredth(Math.Max(m * Math.Min(c, 1), 0));
        }
        // PawnCapacityWorker_Manipulation.
        var (mlvl, _) = LimbEfficiency(st, Manipulation, 0.8);
        manipulation = RoundedHundredth(Math.Max(mlvl * c, 0));
        return new Capacities(consciousness, moving, manipulation, sight, lethalLost);
    }

    /// <summary>
    /// Ports Pawn_HealthTracker.ShouldBeDead for the hediffs held: a lethal capacity lost, the core part's
    /// efficiency gone, or injuries past the lethal damage threshold.
    /// </summary>
    internal bool ShouldBeDead(InjuryState st, Capacities caps) =>
        caps.LethalLost || PartEfficiency(st, 0) <= 0.0001 || st.Total >= LethalDamage || st.Blood >= BloodLossLethal;

    /// <summary>
    /// Ports Pawn_HealthTracker.ShouldBeDowned for a pawn that is not always downed: pain shock, no
    /// consciousness to stay awake, or a Moving capacity at or below its minForCapable.
    /// </summary>
    internal bool ShouldBeDowned(InjuryState st, Capacities caps) =>
        Pain(st) >= PainShock || !(caps.Consciousness >= 0.3) || !(caps.Moving > MovingMinForCapable);

    /// <summary>
    /// Ports HediffSet.GetRandomNotMissingPart for a hit with no chosen height or depth: a part drawn with
    /// weight HitWeight among the parts that are not missing, <paramref name="u"/> in [0, 1). -1 when none
    /// can be hit.
    /// </summary>
    internal int ChooseHitPart(InjuryState st, double u)
    {
        var total = 0.0;
        for (var i = 0; i < Parts.Count; i++)
        {
            if (!st.Missing[i])
            {
                total += Parts[i].HitWeight;
            }
        }
        if (!(total > 0))
        {
            return -1;
        }
        var x = u * total;
        var last = -1;
        for (var i = 0; i < Parts.Count; i++)
        {
            var w = Parts[i].HitWeight;
            if (st.Missing[i] || !(w > 0))
            {
                continue;
            }
            if (x < w)
            {
                return i;
            }
            x -= w;
            last = i;
        }
        return last;
    }

    /// <summary>
    /// Adds one injury of the given severity to a part that is not missing (HediffSet.AddDirect) and returns
    /// the damage it dealt, the severity capped at the part's health before it
    /// (DamageWorker.FinalizeAndAddInjury's totalDamageDealt). A part past its health that is not the core is
    /// destroyed: it and the parts under it go missing and lose their injuries (Hediff_MissingPart.PostAdd,
    /// Pawn_HealthTracker.RestorePart).
    /// </summary>
    internal double AddInjury(InjuryState st, int part, double severity)
    {
        var dealt = Math.Min(severity, PartHealth(st, part));
        st.Sev[part] += severity;
        st.Total += severity;
        st.Keep[part] *= 1 - Math.Min(severity / (75 * HealthScale), 0.95);
        if (part != 0 && PartHealth(st, part) == 0)
        {
            st.Fresh[part] = true;
            Lose(st, part);
        }
        return dealt;
    }

    /// <summary>Marks the part and every part beneath it missing and drops their injuries. Parts are in pre-order, so a part's subtree follows it.</summary>
    private void Lose(InjuryState st, int part)
    {
        st.Missing[part] = true;
        st.Total -= st.Sev[part];
        st.Sev[part] = 0;
        st.Keep[part] = 1;
        for (var i = part + 1; i < Parts.Count; i++)
        {
            if (!InSubtree(i, part))
            {
                continue;
            }
            st.Total -= st.Sev[i];
            st.Sev[i] = 0;
            st.Keep[i] = 1;
            st.Missing[i] = true;
            st.Fresh[i] = false;
        }
    }

    private bool InSubtree(int i, int root)
    {
        for (var p = Parts[i].Parent; p >= 0; p = Parts[p].Parent)
        {
            if (p == root)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// FloatRange.InverseLerpThroughRange over the damage def's overkillPctToDestroyPart: the chance an
    /// outside part is destroyed rather than left at 1 health when a hit exceeds the health it has.
    /// </summary>
    internal double OverkillChance(double f)
    {
        if (OverkillHigh == OverkillLow)
        {
            return 0; // Mathf.InverseLerp(a, a, f)
        }
        return Clamp01((f - OverkillLow) / (OverkillHigh - OverkillLow));
    }

    /// <summary>
    /// Ports SummaryHealthHandler.SummaryHealthPercent: the product, over the injury hediffs, of 1 - min(severity
    /// / (75 x HealthScale), 0.95), and the same for each missing part that is a fresh non-solid outside
    /// extremity (Hediff_MissingPart.SummaryHealthPercentImpact: its def hit points over the same scale, when
    /// it has tags, child parts or bleeds), clamped to [0.05, 1]. The injuries of a part whose hediff can
    /// merge are one hediff of the summed severity (Hediff_Injury.TryMergeWith), otherwise each hit is its
    /// own.
    /// </summary>
    internal double SummaryHealth(InjuryState st)
    {
        var scale = 75 * HealthScale;
        var result = 1.0;
        for (var i = 0; i < Parts.Count; i++)
        {
            var p = Parts[i];
            if (st.Missing[i])
            {
                if (st.Fresh[i] && !p.Inside && !p.Solid && (p.Tagged || p.HasChildren || PartBleed(i) > 0))
                {
                    result *= 1 - Math.Min(p.HitPoints / scale, 0.95);
                }
            }
            else if (MergeInjuries)
            {
                result *= 1 - Math.Min(st.Sev[i] / scale, 0.95);
            }
            else
            {
                result *= st.Keep[i];
            }
        }
        return Math.Min(Math.Max(result, 0.05), 1);
    }

    /// <summary>
    /// The bleed rate (before the division by HealthScale) of a destroyed part's Hediff_MissingPart, when it
    /// counts: the part's maximum health x the missing-part hediff's bleedRate x the part's bleedRate x the
    /// race factor.
    /// </summary>
    internal double PartBleed(int i)
    {
        var p = Parts[i];
        return p.MaxHealth * MissingBleedPerSeverity * p.BleedRate * BleedFactor;
    }

    /// <summary>
    /// Ports HediffSet.CalculateBleedRate for the hediffs held: each injury on a non-solid part bleeds
    /// severity x the injury hediff's bleedRate x the part's bleedRate x the race factor
    /// (Hediff_Injury.BleedRate; nothing is tended or permanent here), a fresh missing outside non-solid part
    /// whose parent is whole bleeds <see cref="PartBleed"/>, all over HealthScale. 0 for a race that cannot
    /// bleed (Pawn_HealthTracker.CanBleed).
    /// </summary>
    internal double BleedRate(InjuryState st)
    {
        if (!(BleedFactor > 0))
        {
            return 0;
        }
        var total = 0.0;
        for (var i = 0; i < Parts.Count; i++)
        {
            var p = Parts[i];
            if (st.Missing[i])
            {
                if (st.Fresh[i] && !p.Inside && !p.Solid && (p.Parent < 0 || !st.Missing[p.Parent]))
                {
                    total += PartBleed(i);
                }
            }
            else if (!p.Solid)
            {
                total += st.Sev[i] * BleedPerSeverity * p.BleedRate * BleedFactor;
            }
        }
        return total / HealthScale;
    }
}

/// <summary>One BodyPartRecord with its BodyPartDef values.</summary>
public sealed record BodyPart
{
    /// <summary>The parent part's index, -1 for the core part.</summary>
    public int Parent { get; init; }

    /// <summary>Whether depth == Inside (otherwise Outside).</summary>
    public bool Inside { get; init; }

    /// <summary>BodyPartRecord.coverageAbs.</summary>
    public double Coverage { get; init; }

    /// <summary>Coverage x BodyPartDef.GetHitChanceFactorFor(the damage def): the weight HediffSet.GetRandomNotMissingPart gives the part.</summary>
    public double HitWeight { get; init; }

    /// <summary>BodyPartDef.GetMaxHealth: ceil(hitPoints x HealthScale).</summary>
    public double MaxHealth { get; init; }

    /// <summary>BodyPartDef.IsSolid.</summary>
    public bool Solid { get; init; }

    /// <summary>BodyPartDef.destroyableByDamage.</summary>
    public bool Destroyable { get; init; }

    /// <summary>The def's hitPoints before the HealthScale.</summary>
    public double HitPoints { get; init; }

    /// <summary>The def's bleedRate.</summary>
    public double BleedRate { get; init; }

    /// <summary>Whether it has any body part tag (the missing-part summary-health rule reads this).</summary>
    public bool Tagged { get; init; }

    /// <summary>Whether it has any child part (the missing-part summary-health rule reads this).</summary>
    public bool HasChildren { get; init; }
}

/// <summary>
/// One limb core of a limb capacity with the parts the capacity multiplies in:
/// BodyPartRecord.GetConnectedParts(segment tag) and GetChildParts(digit tag).
/// </summary>
public sealed record Limb(int Core, IReadOnlyList<int> Segments, IReadOnlyList<int> Digits);

/// <summary>The hediffs of one pawn that the rules read: the summed severity of the injuries on each part and which parts are missing.</summary>
internal sealed class InjuryState(int n)
{
    /// <summary>The summed severity of the injuries on a part.</summary>
    public double[] Sev { get; } = new double[n];

    public bool[] Missing { get; } = new bool[n];

    /// <summary>Marks a part destroyed by the damage itself (Hediff_MissingPart.IsFresh), whose loss hurts while its parent is whole.</summary>
    public bool[] Fresh { get; } = new bool[n];

    /// <summary>The severity of every injury (Hediff_Injury).</summary>
    public double Total { get; set; }

    /// <summary>
    /// The product of 1 - min(severity / (75 x HealthScale), 0.95) over the hits on a part that stay separate
    /// hediffs (summaryHealth).
    /// </summary>
    public double[] Keep { get; } = Enumerable.Repeat(1.0, n).ToArray();

    /// <summary>The BloodLoss hediff's severity (0 while the pawn has none).</summary>
    public double Blood { get; set; }
}

/// <summary>The levels the downing and firing rules read.</summary>
internal readonly record struct Capacities(double Consciousness, double Moving, double Manipulation, double Sight, bool LethalLost);
