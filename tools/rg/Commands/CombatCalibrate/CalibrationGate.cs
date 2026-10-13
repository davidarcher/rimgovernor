using System.Globalization;
using RimGovernor.Brain.Simulation.Combat;
using RimGovernor.Brain.Simulation.CombatMetrics;

namespace RimGovernor.Tools.Commands.CombatCalibrate;

/// <summary>A fixture's place in the calibration.</summary>
internal static class CalibrationRole
{
    /// <summary>Used while building the sim: checked against the baseline but proves nothing about ranking.</summary>
    public const string Calibration = "calibration";

    /// <summary>Not used while building the sim: the sim's variant ranking is judged on these.</summary>
    public const string HeldOut = "held-out";
}

/// <summary>One tactic variant on one fixture. Real is null until the variant has been swept in the real game; SimError is set when the sim could not run it.</summary>
internal sealed record VariantInput(string Name, Sweep? Real, Sweep? Sim, string? SimError);

/// <summary>One fixture's measurements. Unsupported is the sim's refusal when it cannot represent the fixture. Variants lists the default variant first.</summary>
internal sealed record FixtureInput(string Name, string Role, string? Unsupported, IReadOnlyList<VariantInput> Variants);

/// <summary>
/// The sim-vs-real honesty gate for combatsim (Go: <c>internal/combatsim/calibrate</c>, #2844). It decides whether the
/// simulator's results may be trusted for a slice by comparing its sweeps with real-game sweeps of the same fixtures and
/// seeds.
/// </summary>
/// <remarks>
/// The thresholds are constants here, versioned with the pinned assembly hash and the combat ruleset version in the stamp;
/// a pass recorded under another stamp is stale (<see cref="Trusted"/>). The gate fails closed: a fixture the sim cannot
/// import, a variant without a real sweep, a sweep with a defect or the wrong seeds, and a role with nothing measured are
/// all failures, never skips.
/// </remarks>
internal static class CalibrationGate
{
    /// <summary>Thresholds are fixed before any measurement (epic #2830). Changing one is a version bump and invalidates every recorded pass.</summary>
    public const int ThresholdsVersion = 1;

    /// <summary>The pooled pairwise rank agreement of the sim's ordering of variants with the real sweep's, over held-out fixtures.</summary>
    public const double MinRankAgreement = 0.80;

    /// <summary>The survival-critical misrankings allowed: zero.</summary>
    public const int MaxMisrankings = 0;

    /// <summary>A sim mean of colonist deaths or downs must be within this fraction of the real mean...</summary>
    public const double RelTolerance = 0.25;

    /// <summary>...or within this many pawns when that is wider (a small mean).</summary>
    public const double SmallMeanPawns = 1.0;

    /// <summary>The seeds every sweep must have.</summary>
    public const int MinSeeds = 20;

    /// <summary>The variants a held-out fixture must be measured under for a ranking to mean anything.</summary>
    public const int MinVariants = 3;

    /// <summary>The shipped tuning, whose real sweep is the committed baseline.</summary>
    public const string DefaultVariant = "default";

    /// <summary>The stamp of this build.</summary>
    public static CalibrationStamp CurrentStamp() => new()
    {
        AssemblyHash = RulesVersion.AssemblyCSharpSha256,
        RulesVersion = RulesVersion.Current,
        ThresholdsVersion = ThresholdsVersion,
        SweepVersion = Sweep.SweepVersion,
    };

    /// <summary>
    /// Whether a recorded report licenses using the sim: it must have passed, and its stamp must equal the current build's.
    /// Throws <see cref="CalibrationStaleException"/> otherwise.
    /// </summary>
    public static void Trusted(CalibrationReport recorded, CalibrationStamp now)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        if (recorded.Stamp != now)
        {
            throw new CalibrationStaleException($"calibration report does not apply to this build: recorded under {recorded.Stamp}, build is {now}");
        }
        if (!recorded.Pass)
        {
            throw new CalibrationStaleException("calibration report did not pass");
        }
    }

    /// <summary>Runs the gate over the fixtures.</summary>
    public static CalibrationReport Evaluate(CalibrationStamp stamp, IReadOnlyList<FixtureInput> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var rep = new CalibrationReport { Stamp = stamp };
        var failures = new List<string>();
        void Fail(FormattableString text) => failures.Add(text.ToString(CultureInfo.InvariantCulture));
        var fixtures = new List<FixtureReport>();
        var gaps = new List<Gap>();
        var misrankings = new List<Misranking>();
        var regrets = new List<Regret>();
        var falseSafe = new List<string>();
        int pairs = 0, agreed = 0, heldMeasured = 0;
        foreach (var fx in input)
        {
            var fr = new FixtureReport { Name = fx.Name, Role = fx.Role };
            if (fx.Role is not (CalibrationRole.Calibration or CalibrationRole.HeldOut))
            {
                Fail($"{fx.Name}: unknown role \"{fx.Role}\"");
            }
            if (fx.Unsupported is not null)
            {
                fr.Status = "unsupported: " + fx.Unsupported;
                Fail($"{fx.Name} ({fx.Role}): the sim does not support this fixture: {fx.Unsupported}");
                fixtures.Add(fr);
                continue;
            }
            var ok = new List<VariantInput>();
            foreach (var v in fx.Variants)
            {
                if (Check(fx.Name, v) is { } problem)
                {
                    Fail($"{fx.Name}/{v.Name}: {problem}");
                    continue;
                }
                ok.Add(v);
            }
            if (fx.Variants.Count == 0 || fx.Variants[0].Name != DefaultVariant)
            {
                Fail($"{fx.Name}: the \"{DefaultVariant}\" variant must come first");
            }
            if (ok.Count == 0)
            {
                fr.Status = "unmeasured";
                fixtures.Add(fr);
                continue;
            }
            fr.Status = "measured";
            foreach (var v in ok)
            {
                Absolute(fr, v, gaps, falseSafe);
            }
            if (fx.Role == CalibrationRole.HeldOut)
            {
                if (ok.Count < MinVariants)
                {
                    Fail($"{fx.Name}: {ok.Count} variants measured, a ranking needs {MinVariants}");
                }
                else
                {
                    heldMeasured++;
                    var (p, a) = Rank(fx.Name, ok, misrankings, regrets);
                    fr.Pairs = p;
                    fr.Agreed = a;
                    pairs += p;
                    agreed += a;
                    if (p == 0)
                    {
                        Fail($"{fx.Name}: the real sweeps do not order any two variants, so the ranking is unmeasured");
                    }
                }
            }
            fixtures.Add(fr);
        }
        if (heldMeasured == 0)
        {
            Fail($"no held-out fixture was measured: rank agreement and survival misrankings are unmeasured");
        }
        var rankAgreement = 0.0;
        if (pairs > 0)
        {
            rankAgreement = (double)agreed / pairs;
            if (rankAgreement < MinRankAgreement)
            {
                Fail($"rank agreement {rankAgreement:F2} over {pairs} pairs is below {MinRankAgreement:F2}");
            }
        }
        if (misrankings.Count > MaxMisrankings)
        {
            foreach (var m in misrankings)
            {
                Fail($"{m.Fixture}: the sim prefers {m.Preferred} over {m.Other} but the real sweep has {m.RealDeathsPreferred:F2} colonist deaths against {m.RealDeathsOther:F2}");
            }
        }
        var sortedGaps = gaps.OrderByDescending(g => g.Excess()).ToList(); // stable: ties keep their order
        foreach (var g in sortedGaps)
        {
            Fail($"{g.Fixture}/{g.Variant}: sim {g.Measure} mean {g.Sim:F2} vs real {g.Real:F2}, outside +/-{g.Tolerance:F2}");
        }
        return rep with
        {
            Pass = failures.Count == 0,
            Fixtures = fixtures,
            RankAgreement = rankAgreement,
            RankPairs = pairs,
            Misrankings = misrankings.Count > 0 ? misrankings : null,
            Regrets = regrets.Count > 0 ? regrets : null,
            FalseSafe = falseSafe.Count > 0 ? falseSafe : null,
            Gaps = sortedGaps.Count > 0 ? sortedGaps : null,
            Failures = failures.Count > 0 ? failures : null,
        };
    }

    // The allowed distance of a sim mean from the real mean.
    private static double Tolerance(double real) => Math.Max(RelTolerance * Math.Abs(real), SmallMeanPawns);

    // Orders two summaries by survival first: fewer deaths, then fewer downs, then less colonist damage. -1 when a is
    // better, 1 when b is, 0 for a tie.
    private static int Better(Summary a, Summary b)
    {
        (double X, double Y)[] pairs =
        [
            (a.ColonistDeaths.Mean, b.ColonistDeaths.Mean),
            (a.ColonistDowns.Mean, b.ColonistDowns.Mean),
            (a.ColonistDamage.Mean, b.ColonistDamage.Mean),
        ];
        foreach (var (x, y) in pairs)
        {
            if (x < y)
            {
                return -1;
            }
            if (x > y)
            {
                return 1;
            }
        }
        return 0;
    }

    private static int[] SeedsOf(Sweep s) => [.. s.Rows.Select(r => r.Seed)];

    private static string SeedList(int[] seeds) => "[" + string.Join(' ', seeds) + "]";

    // Refuses a pair of sweeps that cannot be compared: another fixture, real defects, too few seeds, or different seed
    // lists. Returns the reason, or null when they can be compared.
    private static string? Check(string fixture, VariantInput v)
    {
        if (v.SimError is not null)
        {
            return "sim could not run: " + v.SimError;
        }
        if (v.Real is null)
        {
            return "no real sweep (unmeasured)";
        }
        if (v.Sim is null)
        {
            return "no sim sweep";
        }
        if (v.Real.Fixture != fixture || v.Sim.Fixture != fixture)
        {
            return $"sweeps are for fixtures \"{v.Real.Fixture}\" and \"{v.Sim.Fixture}\"";
        }
        var defects = v.Real.Defects();
        if (defects.Count > 0)
        {
            return "real sweep is unusable: " + string.Join("; ", defects);
        }
        if (v.Real.Rows.Count < MinSeeds)
        {
            return $"real sweep has {v.Real.Rows.Count} seeds, the gate needs {MinSeeds}";
        }
        if (!SeedsOf(v.Real).SequenceEqual(SeedsOf(v.Sim)))
        {
            return $"real seeds {SeedList(SeedsOf(v.Real))} and sim seeds {SeedList(SeedsOf(v.Sim))} differ";
        }
        foreach (var r in v.Sim.Rows)
        {
            if (r.Metrics is null)
            {
                return $"sim seed {r.Seed} has no metrics: {r.Error}";
            }
        }
        return null;
    }

    // Records a variant's means and the gated measures out of tolerance, and false-safe variants.
    private static void Absolute(FixtureReport fr, VariantInput v, List<Gap> gaps, List<string> falseSafe)
    {
        var rs = v.Real!.Summary;
        var ss = v.Sim!.Summary;
        (fr.Cells ??= []).Add(new Cell
        {
            Variant = v.Name,
            RealDeaths = rs.ColonistDeaths.Mean,
            SimDeaths = ss.ColonistDeaths.Mean,
            RealDowns = rs.ColonistDowns.Mean,
            SimDowns = ss.ColonistDowns.Mean,
            RealDamage = rs.ColonistDamage.Mean,
            SimDamage = ss.ColonistDamage.Mean,
            RealDeathSeeds = rs.SeedsWithColonistDeath,
            SimDeathSeeds = ss.SeedsWithColonistDeath,
        });
        (string Name, double Real, double Sim)[] measures =
        [
            ("colonist deaths", rs.ColonistDeaths.Mean, ss.ColonistDeaths.Mean),
            ("colonist downs", rs.ColonistDowns.Mean, ss.ColonistDowns.Mean),
        ];
        foreach (var (name, real, sim) in measures)
        {
            var tol = Tolerance(real);
            if (Math.Abs(sim - real) > tol)
            {
                gaps.Add(new Gap { Fixture = fr.Name, Variant = v.Name, Measure = name, Real = real, Sim = sim, Tolerance = tol });
            }
        }
        if (ss.SeedsWithColonistDeath == 0 && rs.SeedsWithColonistDeath > 0)
        {
            falseSafe.Add(fr.Name + "/" + v.Name);
        }
    }

    // Compares the sim's and the real sweep's ordering of the variants of a held-out fixture. Returns the pairs the real
    // sweep orders and how many the sim orders the same way, and records survival-critical misrankings and the regret of
    // following the sim's pick.
    private static (int Pairs, int Agreed) Rank(string fixture, List<VariantInput> vs, List<Misranking> misrankings, List<Regret> regrets)
    {
        int pairs = 0, agreed = 0;
        for (var i = 0; i < vs.Count; i++)
        {
            for (var j = i + 1; j < vs.Count; j++)
            {
                var real = Better(vs[i].Real!.Summary, vs[j].Real!.Summary);
                var sim = Better(vs[i].Sim!.Summary, vs[j].Sim!.Summary);
                if (real != 0)
                {
                    pairs++;
                    if (sim == real)
                    {
                        agreed++;
                    }
                }
                if (sim == 0)
                {
                    continue;
                }
                var (pref, other) = sim > 0 ? (vs[j], vs[i]) : (vs[i], vs[j]);
                double pd = pref.Real!.Summary.ColonistDeaths.Mean;
                double od = other.Real!.Summary.ColonistDeaths.Mean;
                if (pd > od)
                {
                    misrankings.Add(new Misranking { Fixture = fixture, Preferred = pref.Name, Other = other.Name, RealDeathsPreferred = pd, RealDeathsOther = od });
                }
            }
        }
        var pick = vs[0];
        var best = vs[0];
        foreach (var v in vs.Skip(1))
        {
            if (Better(v.Sim!.Summary, pick.Sim!.Summary) < 0)
            {
                pick = v;
            }
            if (Better(v.Real!.Summary, best.Real!.Summary) < 0)
            {
                best = v;
            }
        }
        var p = pick.Real!.Summary;
        var b = best.Real!.Summary;
        regrets.Add(new Regret
        {
            Fixture = fixture,
            SimPick = pick.Name,
            RealBest = best.Name,
            Deaths = p.ColonistDeaths.Mean - b.ColonistDeaths.Mean,
            Downs = p.ColonistDowns.Mean - b.ColonistDowns.Mean,
            Damage = p.ColonistDamage.Mean - b.ColonistDamage.Mean,
        });
        return (pairs, agreed);
    }
}

/// <summary>A recorded calibration pass does not apply to this build, or did not pass (Go <c>ErrStale</c>).</summary>
internal sealed class CalibrationStaleException(string message) : Exception(message);
