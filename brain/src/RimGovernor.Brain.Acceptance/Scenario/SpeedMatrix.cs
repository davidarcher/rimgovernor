using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One column of the speed matrix: the serve speed it runs at (the player's choice serve follows) and whether the test
/// acceleration is on (Go <c>SpeedCase</c>). "uncapped" is Ultrafast with acceleration; both acceptance profiles
/// (headless and rendered) admit it, a player launch does not. "regulated" is uncapped under the native blind-tick
/// regulator, the budget in <see cref="BlindTicks"/>: the row that must match the capped speeds' outcome while beating
/// their wall time. "governor-off" is uncapped native play with no controller attached, the simulation ceiling on the
/// same save and renderer; it builds nothing, so it is outside the outcome comparison. "player" is Ultrafast under
/// player acceleration (any Ultrafast without test acceleration): no test acceleration, native paces ticks per frame
/// against its frame budget and the controller backs off before its evidence goes stale, the mode a player launch runs.
/// </summary>
public sealed record SpeedCase
{
    public string Name { get; init; } = "";

    public string Speed { get; init; } = "";

    public bool TestAcceleration { get; init; }

    public uint BlindTicks { get; init; }

    public bool GovernorOff { get; init; }

    public bool Player { get; init; }

    /// <summary>Adds <see cref="SpeedMatrix.ObservationLoadReaders"/> concurrent state readers beside the governor.</summary>
    public bool ObservationLoad { get; init; }

    /// <summary>Whether the case's pawn outcome takes part in the cross-speed comparison: every governed row does.</summary>
    public bool Compared => !GovernorOff;

    /// <summary>
    /// The serve flags a case adds (its speed is the player's choice, <see cref="Speed"/>, written before serve
    /// starts): for uncapped the test-acceleration opt-in and for regulated the blind-tick budget.
    /// </summary>
    public IReadOnlyList<string> ServeArgs()
    {
        var args = new List<string>();
        if (TestAcceleration)
        {
            args.Add("--clock-test-acceleration");
        }

        if (BlindTicks > 0)
        {
            args.Add("--clock-blind-ticks");
            args.Add(BlindTicks.ToString(CultureInfo.InvariantCulture));
        }

        return args;
    }

    /// <summary>The case as its report row (json tags name, speed, test_acceleration, blind_ticks, governor_off, player, observation_load).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject { ["name"] = Name, ["speed"] = Speed, ["test_acceleration"] = TestAcceleration };
        if (BlindTicks > 0)
        {
            row["blind_ticks"] = BlindTicks;
        }

        if (GovernorOff)
        {
            row["governor_off"] = true;
        }

        if (Player)
        {
            row["player"] = true;
        }

        if (ObservationLoad)
        {
            row["observation_load"] = true;
        }

        return row;
    }
}

/// <summary>The main-thread queue wait of the commands the player row's service queued (Go <c>bridge.Quantiles</c>), in milliseconds.</summary>
public sealed record DispatchQuantiles
{
    public ulong Samples { get; init; }

    public double P50 { get; init; }

    public double P95 { get; init; }

    public double P99 { get; init; }

    public double Max { get; init; }

    public double Sum { get; init; }

    /// <summary>Sum over Samples, or 0 without any.</summary>
    public double Mean => Samples == 0 ? 0 : Sum / Samples;
}

/// <summary>What the player row's checks read (Go <c>PlayerRow</c>).</summary>
public sealed record PlayerRow
{
    public IReadOnlyList<HazardGap> HazardGaps { get; init; } = [];

    public int SpeedChanges { get; init; }

    public ulong Ticks { get; init; }

    public ulong PacedFrames { get; init; }

    public ulong OverBudget { get; init; }

    /// <summary>The pacing reason of the last step that found a window running.</summary>
    public string LastPacingReason { get; init; } = "";

    /// <summary>
    /// The main-thread queue wait of the commands the row's service queued while the clock ran (the observation hops'
    /// queueMs): its p95 must land inside the frame budget.
    /// </summary>
    public DispatchQuantiles Dispatch { get; init; } = new();
}

/// <summary>What the pawns achieved in one case: the postconditions the matrix requires to agree within a tolerance across speeds (Go <c>SpeedOutcome</c>).</summary>
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
        ["case"] = Case,
        ["stored_stacks"] = StoredStacks,
        ["stored_units"] = StoredUnits,
        ["loose_units"] = LooseUnits,
        ["walls_built"] = WallsBuilt,
        ["healthy_colonists"] = HealthyColonists,
        ["unsuccessful_stages"] = UnsuccessfulStages,
    };
}

/// <summary>The speed matrix's cases, bounds and cross-case comparisons (Go speedmatrix.go).</summary>
public static class SpeedMatrix
{
    /// <summary>The -speeds default: every native speed plus uncapped, regulated and governor-off.</summary>
    public const string DefaultSpeedMatrix = "Normal,Fast,Superfast,Ultrafast,uncapped,regulated,governor-off";

    /// <summary>The regulated row's budget: the planning fact tolerance and the combat window already encode this horizon.</summary>
    public const uint RegulatedBlindTicks = 300;

    /// <summary>
    /// The player row's bounds. A command queued for the game's main thread waits behind at most one frame's tick work,
    /// so the frame budget holding is the dispatch bound: at most this share of the paced frames may exceed it (the
    /// frame that trips the decrease, a long single tick).
    /// </summary>
    public const double MaxPlayerOverBudgetShare = 0.05;

    /// <summary>
    /// Recovery from a stop resumes the backoff's earned rate rather than restarting at full speed, so tick-rate
    /// changes stay at most this many per 6000 ticks.
    /// </summary>
    public const int MaxPlayerSpeedChangesPer6000 = 6;

    /// <summary>Native's default frame budget, the one the player row runs under.</summary>
    public const int PlayerFrameBudgetMs = 30;

    /// <summary>The observation-load row's reader count: several clients' worth of state reads, well above the launcher's own cadence, so readers contend with the controller's refreshes.</summary>
    public const int ObservationLoadReaders = 3;

    /// <summary>The observation-load row's polling interval.</summary>
    public static readonly TimeSpan ObservationLoadInterval = TimeSpan.FromMilliseconds(250);

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
            ["uncapped"] = new() { Name = "uncapped", Speed = "Ultrafast", TestAcceleration = true },
            ["regulated"] = new() { Name = "regulated", Speed = "Ultrafast", TestAcceleration = true, BlindTicks = RegulatedBlindTicks },
            ["governor-off"] = new() { Name = "governor-off", Speed = "Ultrafast", TestAcceleration = true, GovernorOff = true },
            ["player"] = new() { Name = "player", Speed = "Ultrafast", Player = true },
            ["observation-load"] = new() { Name = "observation-load", Speed = "Ultrafast", TestAcceleration = true, ObservationLoad = true },
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
                throw new AcceptanceException($"unknown speed \"{part.Trim()}\" (want Normal, Fast, Superfast, Ultrafast, uncapped, regulated, governor-off, observation-load or player)");
            }

            if (!seen.Add(key))
            {
                throw new AcceptanceException($"speed \"{speedCase.Name}\" listed twice");
            }

            cases.Add(speedCase);
        }

        return cases.Count == 0 ? throw new AcceptanceException("no speeds selected") : cases;
    }

    /// <summary>Lists every player-row bound the run broke.</summary>
    public static IReadOnlyList<string> PlayerRowProblems(PlayerRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var problems = new List<string>();
        foreach (var gap in row.HazardGaps)
        {
            if (gap.BoundTicks > 0 && gap.MaxTickGap > gap.BoundTicks)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"hazard {gap.HazardClass}: widest detection gap {gap.MaxTickGap} ticks past its {gap.BoundTicks}-tick bound"));
            }
        }

        if (row.PacedFrames == 0)
        {
            problems.Add("no frame ran under player pacing");
        }
        else
        {
            var share = (double)row.OverBudget / row.PacedFrames;
            if (share > MaxPlayerOverBudgetShare)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{row.OverBudget} of {row.PacedFrames} paced frames ({share * 100:F1}%) exceeded the frame budget"));
            }
        }

        if (row.Dispatch.Samples == 0)
        {
            problems.Add("no queued command reported its main-thread dispatch wait");
        }
        else if (row.Dispatch.P95 > PlayerFrameBudgetMs)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"queued commands waited {row.Dispatch.P95:F1}ms (p95 over {row.Dispatch.Samples}) for dispatch, past the {PlayerFrameBudgetMs}ms frame budget"));
        }

        if (row.Ticks > 0)
        {
            var per = row.SpeedChanges * 6000.0 / row.Ticks;
            if (per > MaxPlayerSpeedChangesPer6000)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{per:F1} tick-rate changes per 6000 ticks ({row.SpeedChanges} over {row.Ticks} ticks) oscillate past {MaxPlayerSpeedChangesPer6000}"));
            }
        }

        switch (row.LastPacingReason)
        {
            case "accelerated" or "frame_budget":
                break;
            case "":
                problems.Add("no running step reported a pacing reason");
                break;
            default:
                problems.Add($"the last running window was held at \"{row.LastPacingReason}\", not the accelerated rate");
                break;
        }

        return problems;
    }

    /// <summary>The pacing reason of the newest clock_step row that carries one.</summary>
    public static string LastPacingReason(IReadOnlyList<TimelineRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Kind != "clock_step")
            {
                continue;
            }

            if (StepFields(rows[i]).GetValueOrDefault("pacing_reason") is string { Length: > 0 } reason)
            {
                return reason;
            }
        }

        return "";
    }

    /// <summary>
    /// A clock_step decision row's data flat, with the step wall as elapsed_ms (dur_ms) and the step's cause as reason
    /// (target): the field names the step readers aggregate (Go <c>bridge.StepFields</c>).
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
        return new SpeedOutcome
        {
            Case = name,
            StoredStacks = (int)control["storedStacks"].AsNumber(),
            StoredUnits = (int)control["storedUnits"].AsNumber(),
            LooseUnits = (int)control["looseUnits"].AsNumber(),
            WallsBuilt = (int)control["wallsBuilt"].AsNumber(),
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
    /// Lists the rows whose costliest live step issued more than <paramref name="maxReads"/> native round trips, the
    /// step-cost bound: a live step plans under a running window off the facts its one review bundle carries, so its
    /// round trips are the bundle plus what an event within the step made stale. Rows that ran no live step are
    /// skipped, and a maxReads of 0 disables the check. The step's wall time is reported beside it (live_step_ms_mean)
    /// but not bounded: wall measures the box and the transport floor, the call count measures this repository.
    /// </summary>
    public static IReadOnlyList<string> CheckLiveStepCost(IReadOnlyList<SpeedMetrics> rows, double maxReads)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var problems = new List<string>();
        if (maxReads <= 0)
        {
            return problems;
        }

        foreach (var row in rows)
        {
            if (row.LiveSteps <= 0)
            {
                continue;
            }

            if (row.MaxLiveStepReads > maxReads)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{row.Case}: a live step issued {row.MaxLiveStepReads:F0} native reads, over {maxReads:F0} ({row.LiveSteps:F0} live steps, wall mean {row.LiveStepMs:F0}ms)"));
            }
        }

        return problems;
    }

    /// <summary>
    /// Lists the clock-throughput expectations the matrix missed. <paramref name="maxPausedFraction"/>, when positive,
    /// bounds the paused fraction of every case at Superfast or faster (the speeds whose windows the wall-time sizing
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

            if (maxPausedFraction > 0 && row.Speed is "Superfast" or "Ultrafast" && row.PausedShare > maxPausedFraction)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{row.Case}: paused fraction {row.PausedShare:F2} exceeds {maxPausedFraction:F2}"));
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
                PausedFractionNative = row.PausedShare,
            });
        }

        return output;
    }
}

/// <summary>
/// One governed row's achieved wall TPS against the governor-off ceiling of the same fixture: the number that judges how
/// close governed play runs to the simulation's own speed, reported beside paused_fraction_native (Go <c>CeilingRatio</c>).
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
        ["case"] = Case,
        ["achieved_tps"] = AchievedTps,
        ["ceiling_tps"] = CeilingTps,
        ["ratio"] = Ratio,
        ["paused_fraction_native"] = PausedFractionNative,
    };
}

/// <summary>
/// The per-case row a speed matrix reports (caseMetrics in cases/speedmatrix): the fields CheckSpeedMetrics and
/// SpeedRowProblems read, decoded from the report's metrics rows. PausedFraction is the status-sample ratio (a sampling
/// diagnostic); PausedFractionNative is native's own account of its stop/start transitions, the paused share the
/// thresholds bound where the row carries it.
/// </summary>
public sealed record SpeedMetrics
{
    public string Case { get; init; } = "";

    public string Speed { get; init; } = "";

    public double WallTps { get; init; }

    public double TicksAdvanced { get; init; }

    public double PausedFraction { get; init; }

    public double PausedFractionNative { get; init; }

    public double NativePauseSamples { get; init; }

    /// <summary>
    /// The live steps: how many the row ran, the native round trips the costliest issued and the mean wall of one. A
    /// row without a live step (no running window was planned under) leaves LiveSteps at 0 and CheckLiveStepCost skips it.
    /// </summary>
    public double LiveSteps { get; init; }

    public double MaxLiveStepReads { get; init; }

    public double LiveStepMs { get; init; }

    /// <summary>The paused fraction a threshold bounds: native's account when the row sampled it, else the sample ratio.</summary>
    public double PausedShare => NativePauseSamples > 0 ? PausedFractionNative : PausedFraction;

    /// <summary>
    /// Decodes the "case", "speed", "wall_tps", "ticks_advanced", "paused_fraction", "paused_fraction_native",
    /// "native_pause_samples", "live_steps", "max_live_step_reads" and "live_step_ms_mean" fields of each metrics row.
    /// </summary>
    public static IReadOnlyList<SpeedMetrics> FromRows(IEnumerable<JsonObject> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.Select(row => new SpeedMetrics
        {
            Case = row["case"].AsString(),
            Speed = row["speed"].AsString(),
            WallTps = row["wall_tps"].AsNumber(),
            TicksAdvanced = row["ticks_advanced"].AsNumber(),
            PausedFraction = row["paused_fraction"].AsNumber(),
            PausedFractionNative = row["paused_fraction_native"].AsNumber(),
            NativePauseSamples = row["native_pause_samples"].AsNumber(),
            LiveSteps = row["live_steps"].AsNumber(),
            MaxLiveStepReads = row["max_live_step_reads"].AsNumber(),
            LiveStepMs = row["live_step_ms_mean"].AsNumber(),
        })];
    }
}
