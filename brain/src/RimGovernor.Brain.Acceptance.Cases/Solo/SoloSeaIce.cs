using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The year-long solo acceptance cases of epic #2745 (Go <c>solo/seaice.go</c>): one colonist on a very cold ice sheet
/// under Randy Random at the top difficulty must survive the RimWorld year, <see cref="PolicyFunctions.YearDays"/> (60)
/// game days. <see cref="SoloDaysEnv"/> sets a shorter or longer run.
/// <para>
/// The year is played in ten-day segments. Each segment launches the serve process, watches a tick window, stops it
/// and reattaches, and the reattached harness then reads what the service's own census cannot: the colonist's hediffs
/// and the sleeping temperature and power facts. The segment boundary is the probe cadence; a hypothermia or
/// malnutrition excursion that heals inside one segment is not seen (the bound is ten days, not a continuous watch).
/// </para>
/// </summary>
internal static class SoloSeaIce
{
    /// <summary>The environment variable that sets how many game days the solo cases play (default the 60-day RimWorld year).</summary>
    internal const string SoloDaysEnv = "RIMGOVERNOR_ACCEPT_SOLO_DAYS";

    private const ulong TicksPerDay = 60000;

    // The Ultrafast rate measured on the debug colony (nativeaccept.RunSpeed); the budget derives from it. Box load cuts
    // it to about 340, which the suite allowance does not cover.
    private const ulong TicksPerSecond = 899;

    // One serve launch: ten game days.
    private const ulong SegmentTicks = 10 * TicksPerDay;

    // How long the sleeping band is not gated: the first segment is the epic's shelter build-out, and the claim is the
    // shelter once it stands. Survival, hypothermia and food are gated from tick zero.
    private const ulong ShelterGraceTicks = SegmentTicks;

    // The worst Hypothermia severity a probe may read: serious (0.35), short of the extreme tier where death approaches.
    private const double HypothermiaCap = PolicyFunctions.HypothermiaSerious;

    // The wall ceiling of one segment's watch; the box load that cuts long runs (~107k ticks historically) is a stall
    // bound, not a reason to shorten the year.
    private static readonly TimeSpan PerSegmentWall = TimeSpan.FromHours(6);

    /// <summary>The run's length in game ticks.</summary>
    internal static ulong TargetTicks()
    {
        var raw = Environment.GetEnvironmentVariable(SoloDaysEnv);
        if (!string.IsNullOrEmpty(raw) && ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            return n * TicksPerDay;
        }

        return (ulong)PolicyFunctions.YearDays * TicksPerDay;
    }

    /// <summary>
    /// The wall clock a run of <paramref name="target"/> ticks needs: ticksPerSecond of play plus a launch, stop and
    /// probe per segment. The 60-day year is 67 minutes of play, six segments and the fixed 7 minutes: 92 minutes. The
    /// remote plan counts the budget once per attempt (max_attempts 2) against the 345-minute suite allowance.
    /// </summary>
    internal static TimeSpan RunBudget(ulong target)
    {
        var segments = (int)((target + SegmentTicks - 1) / SegmentTicks);
        return TimeSpan.FromSeconds(target / TicksPerSecond) + (segments * TimeSpan.FromMinutes(3)) + TimeSpan.FromMinutes(7);
    }

    /// <summary>Rhadamant's Sea Ice Community Challenge as scenario parts (Go <c>httpapi.RhadamantParts</c>); her cannibalism rules are not included.</summary>
    private static JsonObject RhadamantParts()
    {
        var d = PolicyFunctions.RhadamantThings;
        static JsonObject Thing(string placement, string def, uint n) => new() { ["placement"] = placement, ["thingDef"] = def, ["count"] = n };
        static JsonObject Trait(string def, int degree) => new() { ["trait"] = def, ["degree"] = degree, ["chance"] = 1, ["context"] = "player_starter" };
        return new JsonObject
        {
            ["startingThings"] = new JsonArray(
                Thing("start_with", d.Silver, 800),
                Thing("start_with", d.Meal, 50),
                Thing("start_with", d.Wood, 300),
                Thing("start_with", d.Steel, 450),
                Thing("start_with", d.Component, 30),
                Thing("start_with", d.Medicine, 30),
                Thing("start_with", d.Rifle, 1),
                Thing("scattered", d.Steel, 720),
                Thing("scattered", d.Meal, 7),
                Thing("scattered", d.ShipChunk, 3)),
            ["forcedTraits"] = new JsonArray(Trait("NaturalMood", 2), Trait("Immunity", 1), Trait("SpeedOffset", 2)),
            ["permaGameConditions"] = new JsonArray("ColdSnap"),
        };
    }

    /// <summary>The shared one-colonist ice-sheet start: Rhadamant's preset shape (Crashlanded, VeryCold, Randy, Extreme) with the parts the variant carries.</summary>
    private static ScenarioStart SoloStart(string seed, JsonObject? parts) => new()
    {
        Spec = new ScenarioSpec
        {
            Scenario = DebugGame.DebugScenario,
            Count = 1,
            Seed = seed,
            Difficulty = "Extreme",
            Storyteller = "Randy",
            Biome = "IceSheet",
            WorldTemperature = "VeryCold",
            Parts = parts,
            Size = new DebugStartSize { MapSize = 250, PlanetCoverage = 0.3 },
            SaveName = seed,
        },
    };

    internal static AcceptanceCase StockCase() =>
        Variant(
            "solo/sea-ice-stock",
            "stock IceSheet variant (existing launcher options only: one colonist, IceSheet, VeryCold, Randy, Extreme, the Crashlanded kit)",
            SoloStart("solo-sea-ice-stock-2777", null));

    internal static AcceptanceCase RhadamantCase() =>
        Variant(
            "solo/sea-ice-rhadamant",
            "Rhadamant variant (httpapi.RhadamantParts: her kit, forced traits and permanent ColdSnap)",
            SoloStart("solo-sea-ice-rhadamant-2777", RhadamantParts()));

    private static AcceptanceCase Variant(string name, string variant, ScenarioStart start) => new()
    {
        Name = name,
        Scope = "Issue #2777 (epic #2745): the " + variant + " survives the RimWorld year, 60 game days (" + SoloDaysEnv + " sets another length). At each ten-day segment boundary the colonist is alive, "
            + "has no Hypothermia past serious and no Malnutrition past hungry; after the first segment the sleeping temperature is within the policy's band; "
            + "the census food runway never reaches zero; at the end the colonist lives and power is on. Nightly only.",
        Start = start,
        Keep = [NeedDef.LiveNeeds.Value],
        Quiet = QuietMode.Loud,
        Serve = new ServeSpec
        {
            NativeTimeout = TimeSpan.FromSeconds(15),
            StepStall = TimeSpan.FromSeconds(90),
            Prefix = "solo-sea-ice",
            ClockSpeed = "Ultrafast",
        },
        Reason = "a year of live needs under the scenario's own storyteller (Randy, the top difficulty) is the claim; the quiet swap would replace Randy "
            + "and a short window cannot show a cold year",
        Budget = RunBudget(TargetTicks()),
        Crew = new Crew { Size = 1 },
        Run = RunYearAsync,
    };

    private static async Task RunYearAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var initial = new List<string>();
        var listed = await Colonists.ListColonistsAsync(s.Harness, "solo-initial", false, false, cancellationToken).ConfigureAwait(false);
        foreach (var row in listed)
        {
            var id = Colonists.RowID(row);
            if (id.Length != 0)
            {
                initial.Add(id);
            }
        }

        if (initial.Count != 1)
        {
            throw new AcceptanceException($"a solo start has {initial.Count} colonists: [{string.Join(' ', initial)}]");
        }

        report["initial_colonists"] = new JsonArray([.. initial.Select(id => (JsonNode?)JsonValue.Create(id))]);

        var segments = new JsonArray();
        report["segments"] = segments;
        ulong elapsed = 0;
        var target = TargetTicks();
        for (var index = 0; elapsed < target; index++)
        {
            var window = Math.Min(SegmentTicks, target - elapsed);
            var label = string.Create(CultureInfo.InvariantCulture, $"seg{index:D2}");
            var (row, advanced, error) = await PlaySegmentAsync(s, label, window, elapsed, elapsed + window >= target, initial, cancellationToken).ConfigureAwait(false);
            segments.Add(row);
            elapsed += advanced;
            report["elapsed_ticks"] = elapsed;
            if (error is not null)
            {
                throw new AcceptanceException($"{label} (day {elapsed / TicksPerDay} of {target / TicksPerDay}): {error.Message}", error);
            }
        }

        report["survived_days"] = elapsed / TicksPerDay;
    }

    /// <summary>Launches the service for one window, then probes the reattached game. Returns the segment's report row and the ticks the watch advanced.</summary>
    private static async Task<(JsonObject Row, ulong Advanced, Exception? Error)> PlaySegmentAsync(
        ISession s,
        string label,
        ulong window,
        ulong before,
        bool final,
        IReadOnlyList<string> initial,
        CancellationToken cancellationToken)
    {
        var row = new JsonObject { ["label"] = label, ["window"] = window, ["start_elapsed"] = before };
        var spec = s.Spec with { Prefix = "solo-sea-ice-" + label };
        var observed = ObservedSession.Of(s);
        IWatchedService service;
        try
        {
            service = await observed.ServeAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            return (row, 0, new AcceptanceException($"serve: {e.Message}", e));
        }

        var watch = new Report();
        var timeline = new List<JsonObject>();
        Exception? watchError = null;
        JsonObject? keep = null;
        try
        {
            try
            {
                await SustainedFood.WatchAsync(
                    s.Config,
                    service,
                    new WatchConfig
                    {
                        Watch = PerSegmentWall,
                        Window = window,
                        Poll = TimeSpan.FromSeconds(10),
                        Concern = PolicyFunctions.EnsureFoodSupply.Value,
                        Extra = SustainedColonyCase.ColonyConcerns,

                        // A refusal is part of what the year records, never a reason to cut it.
                        FailFast = new FailFast { Disabled = true },
                    },
                    watch,
                    timeline,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                watchError = e;
            }
        }
        finally
        {
            keep = await service.StopAsync().ConfigureAwait(false);
        }

        var tick = watch["window"].AsMap();
        var advanced = tick?["elapsed_ticks"].AsUInt64() ?? 0;
        var outcome = SustainedFood.DeriveColonyOutcome(timeline);
        row["samples"] = timeline.Count;
        row["advanced"] = advanced;
        row["colony"] = outcome.ToJson();
        row["keepalive"] = keep?.DeepClone();
        if (watchError is not null)
        {
            row["error"] = watchError.Message;
        }

        Harness h;
        try
        {
            h = s.Reattach();
        }
        catch (AcceptanceException e)
        {
            return (row, advanced, SustainedFoodCase.Join([watchError, new AcceptanceException($"reattach: {e.Message}", e)]));
        }

        var failures = new List<Exception?> { watchError, AuditCensus(outcome, tick, window) };
        failures.Add(await ProbeAsync(h, s, label, row, before + advanced, final, initial, cancellationToken).ConfigureAwait(false));
        return (row, advanced, SustainedFoodCase.Join(failures));
    }

    /// <summary>The in-segment gate over the service's own census: the watch reached its window, no colonist was lost and the food runway never reached zero.</summary>
    private static AcceptanceException? AuditCensus(ColonyOutcome outcome, JsonObject? window, ulong ticks)
    {
        var failures = new List<Exception?>();
        if (window is null || window["reached"].AsBool() != true)
        {
            failures.Add(new AcceptanceException($"segment window of {ticks} ticks not reached: {window.ToJson()}"));
        }

        if (outcome.ColonistsLost > 0)
        {
            failures.Add(new AcceptanceException($"colonist count dropped by {outcome.ColonistsLost} during the segment"));
        }

        if (outcome.MinFoodRunwayDays is { } runway && runway <= 0)
        {
            failures.Add(new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"food runway reached {runway:G} days")));
        }

        return SustainedFoodCase.Join(failures);
    }

    /// <summary>
    /// Reads the reattached game: the colonist alive without hypothermia past the cap or malnutrition past hungry, and
    /// the sleeping temperature and power facts. It records what it read on <paramref name="row"/>.
    /// </summary>
    private static async Task<AcceptanceException?> ProbeAsync(
        Harness h,
        ISession s,
        string label,
        JsonObject row,
        ulong elapsed,
        bool final,
        IReadOnlyList<string> initial,
        CancellationToken cancellationToken)
    {
        var failures = new List<Exception?>
        {
            await SustainedFoodCase.CaptureAsync(() => SustainedColonyCase.AuditNutrition(h, new Report(row), cancellationToken)).ConfigureAwait(false),
        };
        IReadOnlyList<JsonObject> listed;
        try
        {
            listed = await Colonists.ListColonistsAsync(h, label + "-health", true, true, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            failures.Add(e);
            return SustainedFoodCase.Join(failures);
        }

        failures.Add(AuditColonists(listed, initial, row));
        ColonyFactsReading facts;
        try
        {
            facts = await ColonyFactsSource.ReadAsync(h, s.Identity, label + "-facts", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            failures.Add(e);
            return SustainedFoodCase.Join(failures);
        }

        row["sleeping_c"] = new JsonObject
        {
            ["min"] = facts.SleepingMin ?? 0,
            ["min_known"] = facts.SleepingMin is not null,
            ["max"] = facts.SleepingMax ?? 0,
            ["max_known"] = facts.SleepingMax is not null,
        };
        row["power"] = new JsonObject
        {
            ["required"] = facts.PowerRequired ?? false,
            ["required_known"] = facts.PowerRequired is not null,
            ["headroom_w"] = facts.PowerHeadroom ?? 0,
            ["headroom_known"] = facts.PowerHeadroom is not null,
            ["disabled"] = facts.DisabledConsumers ?? false,
            ["disabled_known"] = facts.DisabledConsumers is not null,
        };
        if (elapsed > ShelterGraceTicks)
        {
            failures.Add(AuditSleepingBand(facts.SleepingMin, facts.SleepingMax, PolicyFunctions.DefaultRoundsPolicy()));
        }

        if (final)
        {
            failures.Add(AuditPower(facts.PowerRequired, facts.PowerHeadroom, facts.DisabledConsumers));
        }

        return SustainedFoodCase.Join(failures);
    }

    /// <summary>
    /// Holds every initial colonist to alive with Hypothermia at or below the cap (also the layout/sea-ice-envelope
    /// case's probe). A corpse can despawn, so a missing pawn fails too.
    /// </summary>
    internal static AcceptanceException? AuditColonists(IReadOnlyList<JsonObject> listed, IReadOnlyList<string> initial, JsonObject row)
    {
        var failures = new List<Exception?>();
        var alive = new Dictionary<string, bool>(StringComparer.Ordinal);
        var worst = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pawn in listed)
        {
            var id = Colonists.RowID(pawn);
            alive[id] = pawn["dead"].AsBool() is false;
            foreach (var entry in pawn["health"].Member("hediffs").AsSlice())
            {
                if (entry.AsMap() is { } hediff && Colonists.HediffDef(hediff) == "Hypothermia")
                {
                    worst[id] = Math.Max(worst.GetValueOrDefault(id), hediff["severity"].AsNumber());
                }
            }
        }

        var hypothermia = new JsonObject();
        foreach (var (id, severity) in worst)
        {
            hypothermia[id] = severity;
        }

        row["hypothermia"] = hypothermia;
        foreach (var id in initial)
        {
            if (!alive.GetValueOrDefault(id))
            {
                failures.Add(new AcceptanceException($"colonist {id} dead or gone"));
            }
            else if (worst.GetValueOrDefault(id) > HypothermiaCap)
            {
                failures.Add(new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"colonist {id} Hypothermia {worst.GetValueOrDefault(id):F2} past the {HypothermiaCap:F2} cap")));
            }
        }

        return SustainedFoodCase.Join(failures);
    }

    /// <summary>Holds the sleeping temperatures to the rounds policy's foothold band, ColdEnter to HotEnter; an unknown temperature fails.</summary>
    private static AcceptanceException? AuditSleepingBand(double? lo, double? hi, RoundsPolicy p)
    {
        if (lo is null || hi is null)
        {
            return new AcceptanceException($"sleeping temperature unknown (min known {lo is not null}, max known {hi is not null})");
        }

        if (lo < p.ColdEnter || hi > p.HotEnter)
        {
            return new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"sleeping temperature {lo:F1}..{hi:F1} C outside the {p.ColdEnter:F0}..{p.HotEnter:F0} C band"));
        }

        return null;
    }

    /// <summary>"Power on": a consumer exists, none is enabled but unpowered and the weakest network has no deficit.</summary>
    private static AcceptanceException? AuditPower(bool? required, double? headroom, bool? disabled)
    {
        if (required is not true)
        {
            return new AcceptanceException($"no power consumer at the end of the run (required {(required ?? false).ToString().ToLowerInvariant()}, known {(required is not null).ToString().ToLowerInvariant()})");
        }

        if (disabled is not false)
        {
            return new AcceptanceException($"a consumer is enabled but unpowered (disabled {(disabled ?? false).ToString().ToLowerInvariant()}, known {(disabled is not null).ToString().ToLowerInvariant()})");
        }

        if (headroom is null or < 0)
        {
            return new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"power headroom {headroom ?? 0:F0} W (known {(headroom is not null).ToString().ToLowerInvariant()})"));
        }

        return null;
    }
}
