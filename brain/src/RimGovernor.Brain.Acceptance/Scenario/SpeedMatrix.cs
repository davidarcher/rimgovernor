using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One column of the speed matrix: the serve speed it runs at (the player's choice serve follows) and whether the test
/// acceleration is on. Every Run runs under the one ceiling (Ultrafast is the game's 15x, Hyperfast the tick boost
/// capped at 30x); test acceleration only adds the acceptance-only world patches (both acceptance profiles admit it, a player
/// launch does not). "uncapped" is Hyperfast with them, the speed test acceleration pins. "governor-off" is uncapped native play with no controller attached, the simulation ceiling on the
/// same save and renderer; it builds nothing, so it is outside the outcome comparison.
/// </summary>
public sealed record SpeedCase
{
    public string Name { get; init; } = "";

    public string Speed { get; init; } = "";

    public bool TestAcceleration { get; init; }

    public bool GovernorOff { get; init; }

    /// <summary>Whether the case's pawn outcome takes part in the cross-speed comparison: every governed row does.</summary>
    public bool Compared => !GovernorOff;

    /// <summary>
    /// The serve flags a case adds (its speed is the player's choice, <see cref="Speed"/>, written before serve
    /// starts): for uncapped the test-acceleration opt-in.
    /// </summary>
    public IReadOnlyList<string> ServeArgs()
    {
        var args = new List<string>();
        if (TestAcceleration)
        {
            args.Add("--clock-test-acceleration");
        }

        return args;
    }

    /// <summary>The case as its report row (json tags name, speed, test_acceleration, governor_off).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject { { "name", Name }, { "speed", Speed }, { "test_acceleration", TestAcceleration } };
        if (GovernorOff)
        {
            row.Set("governor_off", true);
        }

        return row;
    }
}

/// <summary>What the pawns achieved in one case: the postconditions the matrix requires to agree within a tolerance across speeds.</summary>
public sealed record SpeedOutcome
{
    public string Case { get; init; } = "";

    public int StoredStacks { get; init; }

    public int StoredUnits { get; init; }

    public int LooseUnits { get; init; }

    public int WallsBuilt { get; init; }

    public int HealthyColonists { get; init; }

    public int UnsuccessfulStages { get; init; }

    /// <summary>The outcome as its report row (json tags case, stored_stacks, ...).</summary>
    public JsonObject ToJson() => new()
    {
        { "case", Case },
        { "stored_stacks", StoredStacks },
        { "stored_units", StoredUnits },
        { "loose_units", LooseUnits },
        { "walls_built", WallsBuilt },
        { "healthy_colonists", HealthyColonists },
        { "unsuccessful_stages", UnsuccessfulStages },
    };
}

/// <summary>The speed matrix's cases, bounds and cross-case comparisons.</summary>
public static class SpeedMatrix
{
    /// <summary>The -speeds default: every native speed plus uncapped and governor-off.</summary>
    public const string DefaultSpeedMatrix = "Normal,Fast,Superfast,Ultrafast,Hyperfast,uncapped,governor-off";

    /// <summary>Turns a comma-separated -speeds value into cases, in order, refusing unknown names and repeats.</summary>
    public static IReadOnlyList<SpeedCase> ParseSpeedCases(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var known = new Dictionary<string, SpeedCase>(StringComparer.Ordinal)
        {
            ["normal"] = new() { Name = "Normal", Speed = "Normal" },
            ["fast"] = new() { Name = "Fast", Speed = "Fast" },
            ["superfast"] = new() { Name = "Superfast", Speed = "Superfast" },
            ["ultrafast"] = new() { Name = "Ultrafast", Speed = "Ultrafast" },
            ["hyperfast"] = new() { Name = "Hyperfast", Speed = "Hyperfast" },
            ["uncapped"] = new() { Name = "uncapped", Speed = "Hyperfast", TestAcceleration = true },
            ["governor-off"] = new() { Name = "governor-off", Speed = "Hyperfast", TestAcceleration = true, GovernorOff = true },
        };
        var cases = new List<SpeedCase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in spec.Split(','))
        {
            var key = part.Trim().ToLowerInvariant();
            if (key.Length == 0)
            {
                continue;
            }

            if (!known.TryGetValue(key, out var speedCase))
            {
                throw new AcceptanceException($"unknown speed \"{part.Trim()}\" (want Normal, Fast, Superfast, Ultrafast, Hyperfast, uncapped or governor-off)");
            }

            if (!seen.Add(key))
            {
                throw new AcceptanceException($"speed \"{speedCase.Name}\" listed twice");
            }

            cases.Add(speedCase);
        }

        return cases.Count == 0 ? throw new AcceptanceException("no speeds selected") : cases;
    }

    /// <summary>
    /// The ticks per second a speed runs at when nothing slows it: the game's own rates (Normal 60, Fast 180, Superfast
    /// 360, Ultrafast 900) and, for Hyperfast, the tick boost under its 30x cap (1800).
    /// </summary>
    public static double TargetTps(string speed) => speed switch
    {
        "Normal" => 60,
        "Fast" => 180,
        "Superfast" => 360,
        "Ultrafast" => 900,
        "Hyperfast" => 1800,
        _ => 0,
    };

    /// <summary>
    /// The share of the speed's own rate a run achieved (wall TPS over <see cref="TargetTps"/>), or 0 for an unknown
    /// speed: 1 means the clock kept the speed's pace through every hold, below 1 is the cost of holds and slow frames.
    /// </summary>
    public static double EffectiveSpeed(double wallTps, string speed) => TargetTps(speed) is > 0 and var target ? wallTps / target : 0;

    /// <summary>
    /// A clock_step decision row's data flat, with the step wall as elapsed_ms (dur_ms) and the step's cause as reason
    /// (target): the field names the step readers aggregate.
    /// </summary>
    internal static Dictionary<string, object?> StepFields(TimelineRecord row)
    {
        var fields = new Dictionary<string, object?>(LogRow.From(row, 0).Fields(), StringComparer.Ordinal);
        if (fields.TryGetValue(Decision.DurMsKey, out var ms))
        {
            fields["elapsed_ms"] = ms;
        }

        if (fields.GetValueOrDefault(Decision.TargetKey) is string { Length: > 0 } target)
        {
            fields["reason"] = target;
        }

        return fields;
    }

    /// <summary>Reads the counters a test/throughput_control reply carries; colonists and plan stages are the harness's own reads.</summary>
    public static SpeedOutcome OutcomeFromControl(string name, JsonObject control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var counters = Fx.FixtureJson.Deserialize<Fx.ThroughputControlCounters>(control.ToJsonString());
        return new SpeedOutcome
        {
            Case = name,
            StoredStacks = counters.StoredStacks,
            StoredUnits = counters.StoredUnits,
            LooseUnits = counters.LooseUnits,
            WallsBuilt = counters.WallsBuilt,
        };
    }

    /// <summary>
    /// Lists every postcondition whose spread across the cases exceeds tolerance, plus any case that recorded an
    /// unsuccessful plan stage. An empty result means the matrix agrees.
    /// </summary>
    public static IReadOnlyList<string> CompareOutcomes(IReadOnlyList<SpeedOutcome> outcomes, int tolerance)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var problems = new List<string>();
        var fields = new (string Name, Func<SpeedOutcome, int> Get)[]
        {
            ("stored_units", o => o.StoredUnits),
            ("stored_stacks", o => o.StoredStacks),
            ("walls_built", o => o.WallsBuilt),
            ("healthy_colonists", o => o.HealthyColonists),
        };
        foreach (var (name, get) in fields)
        {
            if (outcomes.Count == 0)
            {
                break;
            }

            var lo = get(outcomes[0]);
            var hi = lo;
            foreach (var o in outcomes.Skip(1))
            {
                var v = get(o);
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }

            if (hi - lo > tolerance)
            {
                var parts = outcomes.Select(o => string.Create(CultureInfo.InvariantCulture, $"{o.Case}={get(o)}"));
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} differs by {hi - lo} (tolerance {tolerance}): {string.Join(' ', parts)}"));
            }
        }

        foreach (var o in outcomes)
        {
            if (o.UnsuccessfulStages > 0)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{o.Case}: {o.UnsuccessfulStages} unsuccessful plan stage(s)"));
            }
        }

        return problems;
    }

    /// <summary>
    /// The runner-boundary check the outcome and metric comparators cannot make, since both accept an empty matrix:
    /// every required case must have a metrics row that advanced the tick and, when the case is compared, an outcome
    /// row. Each problem names the row. An empty result means every required row is present and non-empty.
    /// </summary>
    public static IReadOnlyList<string> SpeedRowProblems(IReadOnlyList<SpeedCase> required, IReadOnlyList<SpeedOutcome> outcomes, IReadOnlyList<SpeedMetrics> metrics)
    {
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(metrics);
        var problems = new List<string>();
        var byCase = new Dictionary<string, SpeedMetrics>(StringComparer.Ordinal);
        foreach (var m in metrics)
        {
            byCase[m.Case] = m;
        }

        var outcomeByCase = outcomes.Select(o => o.Case).ToHashSet(StringComparer.Ordinal);
        foreach (var c in required)
        {
            if (!byCase.TryGetValue(c.Name, out var m))
            {
                problems.Add($"{c.Name}: no metrics row");
            }
            else if (m.TicksAdvanced <= 0 || m.WallTps <= 0)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{c.Name}: empty metrics row (ticks_advanced={m.TicksAdvanced:F0} wall_tps={m.WallTps:F1})"));
            }

            if (c.Compared && !outcomeByCase.Contains(c.Name))
            {
                problems.Add($"{c.Name}: no outcome row");
            }
        }

        return problems;
    }

    /// <summary>
    /// Lists the clock-throughput expectations the matrix missed. <paramref name="maxPausedFraction"/>, when positive,
    /// bounds the paused fraction of every case at Superfast or faster (the speeds whose runs the wall-time sizing
    /// must widen). <paramref name="minUltrafastRatio"/>, when positive, requires the Ultrafast case's wall TPS to be
    /// at least that multiple of the Fast case's; it is skipped unless both ran. An empty result means the matrix met
    /// every enabled expectation.
    /// </summary>
    public static IReadOnlyList<string> CheckSpeedMetrics(IReadOnlyList<SpeedMetrics> rows, double maxPausedFraction, double minUltrafastRatio)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var problems = new List<string>();
        SpeedMetrics? fast = null;
        SpeedMetrics? ultrafast = null;
        foreach (var row in rows)
        {
            switch (row.Case)
            {
                case "Fast":
                    fast = row;
                    break;
                case "Ultrafast":
                    ultrafast = row;
                    break;
            }

            if (maxPausedFraction > 0 && row.Speed is "Superfast" or "Ultrafast" or "Hyperfast" && row.PausedFractionNative > maxPausedFraction)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{row.Case}: paused fraction {row.PausedFractionNative:F2} exceeds {maxPausedFraction:F2}"));
            }
        }

        if (minUltrafastRatio > 0 && fast is not null && ultrafast is not null && ultrafast.WallTps < minUltrafastRatio * fast.WallTps)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"Ultrafast wall TPS {ultrafast.WallTps:F1} is under {minUltrafastRatio:F1}x the Fast wall TPS {fast.WallTps:F1}"));
        }

        return problems;
    }

    /// <summary>
    /// Reports achieved_tps / ceiling_tps for every governed row that advanced, the ceiling being the run's own
    /// "governor-off" wall TPS. Empty when the run has no such row.
    /// </summary>
    public static IReadOnlyList<CeilingRatio> CeilingRatios(IReadOnlyList<SpeedMetrics> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        double ceiling = 0;
        foreach (var row in rows)
        {
            if (row.Case == "governor-off" && row.WallTps > 0)
            {
                ceiling = row.WallTps;
            }
        }

        var output = new List<CeilingRatio>();
        if (ceiling <= 0)
        {
            return output;
        }

        foreach (var row in rows)
        {
            if (row.Case == "governor-off" || row.WallTps <= 0)
            {
                continue;
            }

            output.Add(new CeilingRatio
            {
                Case = row.Case,
                AchievedTps = row.WallTps,
                CeilingTps = ceiling,
                Ratio = row.WallTps / ceiling,
                PausedFractionNative = row.PausedFractionNative,
            });
        }

        return output;
    }
}

/// <summary>
/// One governed row's achieved wall TPS against the governor-off ceiling of the same fixture: the number that judges how
/// close governed play runs to the simulation's own speed, reported beside paused_fraction_native.
/// </summary>
public sealed record CeilingRatio
{
    public string Case { get; init; } = "";

    public double AchievedTps { get; init; }

    public double CeilingTps { get; init; }

    public double Ratio { get; init; }

    public double PausedFractionNative { get; init; }

    /// <summary>The ratio as its report row (json tags case, achieved_tps, ceiling_tps, ratio, paused_fraction_native).</summary>
    public JsonObject ToJson() => new()
    {
        { "case", Case },
        { "achieved_tps", AchievedTps },
        { "ceiling_tps", CeilingTps },
        { "ratio", Ratio },
        { "paused_fraction_native", PausedFractionNative },
    };
}

/// <summary>
/// The per-case row a speed matrix reports (caseMetrics in cases/speedmatrix): the fields CheckSpeedMetrics and
/// SpeedRowProblems read, decoded from the report's metrics rows. PausedFractionNative is native's own account of its hold/run time, the paused share the
/// thresholds bound.
/// </summary>
public sealed record SpeedMetrics
{
    public string Case { get; init; } = "";

    public string Speed { get; init; } = "";

    public double WallTps { get; init; }

    public double TicksAdvanced { get; init; }

    public double PausedFractionNative { get; init; }

    /// <summary>The holds the row's journal carried, the mean native time a Run took to resume one, and wall TPS as a share of the speed's own rate.</summary>
    public double Holds { get; init; }

    public double MeanResumeMs { get; init; }

    public double EffectiveSpeed { get; init; }

    /// <summary>
    /// Decodes the "case", "speed", "wall_tps", "ticks_advanced", "paused_fraction_native", "holds",
    /// "resume_latency_ms_mean" and "effective_speed" fields of each metrics row.
    /// </summary>
    public static IReadOnlyList<SpeedMetrics> FromRows(IEnumerable<JsonObject> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.Select(node => node.Deserialize(HarnessRowJsonContext.Default.SpeedMetricsRow)!).Select(row => new SpeedMetrics
        {
            Case = row.Case,
            Speed = row.Speed,
            WallTps = row.WallTps,
            TicksAdvanced = row.TicksAdvanced,
            PausedFractionNative = row.PausedFractionNative,
            Holds = row.Holds,
            MeanResumeMs = row.ResumeLatencyMsMean,
            EffectiveSpeed = row.EffectiveSpeed,
        })];
    }
}
