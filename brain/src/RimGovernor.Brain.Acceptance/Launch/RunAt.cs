using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

public sealed partial class Harness
{
    /// <summary>Reads the current game tick through lifecycle_read_identity; an unloaded game is an error.</summary>
    public async Task<ulong> TickAsync(CancellationToken cancellationToken)
    {
        var reply = await WireAsync("tick", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Outcome(reply, "loaded");
        return (ulong)loaded["context"].Member("tick").AsNumber();
    }
}

/// <summary>
/// Why the game stopped ticking under <see cref="RunAt.RunUntilAsync(Harness, string, ulong, Wait, Probe, bool, Report?, CancellationToken)"/>:
/// what <see cref="RunAt.ReadPauseStateAsync"/> showed when a probe found the game paused although the harness asked for
/// <see cref="RunAt.RunSpeed"/>. <see cref="Letters"/> are the stack's rows (id, label, letterDef), <see cref="Windows"/> the
/// open windows that force a pause (type, title). A wait fails with it at once instead of burning its stall budget.
/// </summary>
public sealed class PauseCause : AcceptanceException
{
    public PauseCause(string label, ulong tick, bool forcePaused, IReadOnlyList<JsonObject> letters, IReadOnlyList<JsonObject> windows, JsonObject status)
        : base(Describe(label, tick, forcePaused, letters, windows))
    {
        Label = label;
        Tick = tick;
        ForcePaused = forcePaused;
        Letters = letters;
        Windows = windows;
        Status = status;
    }

    public string Label { get; }

    public ulong Tick { get; }

    public bool ForcePaused { get; }

    public IReadOnlyList<JsonObject> Letters { get; }

    public IReadOnlyList<JsonObject> Windows { get; }

    /// <summary>The pause state read when the pause was found.</summary>
    public JsonObject Status { get; }

    private static string Describe(string label, ulong tick, bool forcePaused, IReadOnlyList<JsonObject> letters, IReadOnlyList<JsonObject> windows)
    {
        var parts = new List<string>();
        foreach (var letter in letters)
        {
            parts.Add($"letter \"{letter["label"].AsString()}\" ({letter["letterDef"].AsString()})");
        }

        foreach (var window in windows)
        {
            parts.Add($"window {window["type"].AsString()} \"{window["title"].AsString()}\"");
        }

        var cause = parts.Count == 0 ? "no letter or force-pausing window" : string.Join(", ", parts);
        var forced = forcePaused ? "true" : "false";
        return string.Create(CultureInfo.InvariantCulture, $"{label}: game paused at tick {tick} while running (forcePaused={forced}): {cause}");
    }
}

/// <summary>Running the game while a harness waits for it to do something (Go <c>runat.go</c>).</summary>
public static class RunAt
{
    /// <summary>
    /// How a harness runs the game while it waits for the game to do something: Ultrafast with RimWorld's dev tick boost,
    /// which drops the per-frame tick cap. Measured on the debug colony: Fast 168 ticks/s, Superfast 348, Ultrafast 899,
    /// boosted about 7000, so a wait stated in ticks costs the least wall clock. Neither needs Prefs.devMode (which would
    /// add a 35 s def check to every boot). Waits bounded in ticks mean the same at any speed, so nothing about the
    /// assertion depends on it.
    /// </summary>
    public const string RunSpeed = "Ultrafast";

    /// <summary>The tick boost <see cref="RunSpeed"/> runs with.</summary>
    public const bool RunBoost = true;

    /// <summary>RunUntil's default pause between probes.</summary>
    public static readonly TimeSpan RunInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Removes one letter from the stack (scripts/fixtures/LetterFixture.cs); every fixture build carries it.</summary>
    public const string DismissLetterTool = "test/dismiss_letter";

    // The letter defs a running wait acknowledges by default: informational events and the choice letters whose
    // unanswered outcome is harmless to an assertion (a joiner leaves, a quest stays offered). Threat letters are never
    // here. (Go AcknowledgedLetterDefs lives in scenario.go; the scenario port owns the shared copy.)
    private static readonly HashSet<string> AcknowledgedLetterDefs = new(StringComparer.Ordinal)
    {
        "NeutralEvent",
        "PositiveEvent",
        "NegativeEvent",
        "NewQuest",
        "AcceptJoiner",
        "AcceptVisitors",
        "AcceptCreepJoiner",
        "RitualOutcomePositive",
        "RitualOutcomeNegative",
        "BabyBirth",
        "BabyToChild",
        "ChildToAdult",
        "ChildBirthday",
    };

    /// <summary>
    /// The game's pause state and its visible causes: lifecycle_read_tick's paused, forcePaused, timeSpeed, tick and
    /// force-pausing windows ({type, title}), and presentation_notifications' letter stack ({id, label, letterDef}).
    /// </summary>
    public static async Task<JsonObject> ReadPauseStateAsync(WireFunc wire, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        var reply = await wire(label + "-tick", "lifecycle_read_tick", new JsonObject(), cancellationToken).ConfigureAwait(false);
        JsonObject loaded;
        try
        {
            (_, loaded) = Harness.Outcome(reply, "loaded");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: lifecycle_read_tick: {e.Message}", e);
        }

        var loadedContext = loaded["context"].AsMap();
        var windows = new JsonArray();
        foreach (var raw in loaded["pausingWindows"].AsSlice())
        {
            if (raw is JsonObject row)
            {
                windows.Add(new JsonObject { ["type"] = row["nativeType"]?.DeepClone(), ["title"] = row["title"]?.DeepClone() });
            }
        }

        var letters = await ReadLettersAsync(wire, label, loadedContext?["identity"], cancellationToken).ConfigureAwait(false);
        var rows = new JsonArray();
        foreach (var letter in letters)
        {
            rows.Add(letter);
        }

        return new JsonObject
        {
            ["tick"] = loadedContext?["tick"].AsNumber() ?? -1,
            ["paused"] = loaded["paused"].AsBool() == true,
            ["forcePaused"] = loaded["forcePaused"].AsBool() == true,
            ["timeSpeed"] = loaded["timeSpeed"]?.DeepClone(),
            ["windows"] = windows,
            ["letters"] = rows,
        };
    }

    /// <summary>presentation_notifications' letter stack, newest first, as {id, label, letterDef} rows.</summary>
    public static async Task<IReadOnlyList<JsonObject>> ReadLettersAsync(WireFunc wire, string label, JsonNode? identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        var reply = await wire(
            label + "-letters",
            "presentation_notifications",
            new JsonObject { ["identity"] = identity?.DeepClone(), ["includeMessages"] = false, ["includeAlerts"] = false },
            cancellationToken).ConfigureAwait(false);
        JsonObject snapshot;
        try
        {
            (_, snapshot) = Harness.Outcome(reply, "notifications");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: presentation_notifications: {e.Message}", e);
        }

        JsonObject observed;
        try
        {
            (_, observed) = Harness.Outcome(snapshot["letters"].AsMap() ?? [], "observed");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: letter section: {e.Message}", e);
        }

        var rows = new List<JsonObject>();
        foreach (var raw in observed["letters"].AsSlice())
        {
            if (raw is JsonObject row)
            {
                rows.Add(new JsonObject
                {
                    ["id"] = row["id"]?.DeepClone(),
                    ["label"] = row["label"]?.DeepClone(),
                    ["letterDef"] = row["defName"]?.DeepClone(),
                });
            }
        }

        return rows;
    }

    /// <summary>
    /// Decides what a paused <see cref="ReadPauseStateAsync"/> means for a running wait. A status that shows the game
    /// running again is a transient (no cause, nothing to dismiss). A stack of letters that are all acknowledged defs is
    /// returned to dismiss (when the catalog carries the dismiss tool), as the service acknowledges them, since a
    /// bridge-only case has no routine layer to do it. Quiet starts also dismiss the ancient-shrine discovery warning: it
    /// is map discovery, not an incident the quiet storyteller can suppress. A force-pausing window, any other letter, or
    /// a pause with no visible cause is the <see cref="PauseCause"/> that fails the wait.
    /// </summary>
    public static (PauseCause? Cause, IReadOnlyList<JsonObject> Dismiss) ClassifyPause(JsonObject status, IReadOnlyList<string> tools, string label, ulong tick, bool quiet)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(tools);
        if (status["paused"].AsBool() != true)
        {
            return (null, []);
        }

        var letters = status["letters"].AsSlice().OfType<JsonObject>().ToList();
        var windows = status["windows"].AsSlice().OfType<JsonObject>().ToList();
        var cause = new PauseCause(label, tick, status["forcePaused"].AsBool() == true, letters, windows, status);
        if (windows.Count > 0 || letters.Count == 0 || !Session.HasTool(tools, DismissLetterTool))
        {
            return (cause, []);
        }

        foreach (var letter in letters)
        {
            var definition = letter["letterDef"].AsString();
            var discovery = quiet && letter["label"].AsString() == "Ancient danger" && definition == "ThreatBig";
            if (!AcknowledgedLetterDefs.Contains(definition) && !discovery)
            {
                return (cause, []);
            }
        }

        return (null, letters);
    }

    /// <summary>
    /// Runs the game at <see cref="RunSpeed"/> and polls <paramref name="probe"/> until it is done, then pauses;
    /// <paramref name="ticks"/> is the game-time budget and <paramref name="w"/>'s own bounds still apply (Interval defaults
    /// to 250 ms, Tick is set here). It returns how many ticks the game advanced. The probe's signature may be empty: the
    /// tick is always part of the signature, so a game that stops ticking stalls. A game found paused is resolved before
    /// the probe: benign letters are acknowledged and the run resumed, anything else fails the wait at once with a
    /// <see cref="PauseCause"/>. <paramref name="quiet"/> says the world was started quiet (its discovery warning is
    /// dismissed too) and <paramref name="report"/>, when given, collects the dismissed_letters rows (the Go harness carried
    /// both on the Harness).
    /// </summary>
    public static async Task<ulong> RunUntilAsync(
        Harness h,
        string label,
        ulong ticks,
        Wait w,
        Probe probe,
        bool quiet,
        Report? report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(probe);
        if (ticks == 0)
        {
            throw new AcceptanceException($"{label}: a tick budget is required");
        }

        await h.CallAsync(label + "-run", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = RunSpeed, ["ultraSpeedBoost"] = RunBoost }, cancellationToken).ConfigureAwait(false);

        // A probe is a ~100 ms round trip and the game runs thousands of ticks a second here, so the default 2 s poll is
        // most of a short harness's run in overshoot; poll often instead.
        ulong latest = 0;
        var paused = false;
        ulong first = 0;
        var haveFirst = false;
        var bounded = w with
        {
            Ticks = ticks,
            Interval = w.Interval <= TimeSpan.Zero ? RunInterval : w.Interval,
            Tick = async ct =>
            {
                var loaded = await ReadLoadedAsync(h, "tick", ct).ConfigureAwait(false);
                latest = (ulong)loaded["context"].Member("tick").AsNumber();
                paused = loaded["paused"].AsBool() == true;
                return latest;
            },
        };

        ExceptionDispatchInfo? failure = null;
        try
        {
            await bounded.WaitProgressAsync(
                async ct =>
                {
                    if (!haveFirst)
                    {
                        first = latest;
                        haveFirst = true;
                    }

                    if (paused)
                    {
                        // The awaited event may be what paused the game (a chased joiner arrives with its raid letter): a
                        // done probe wins.
                        var (early, earlyDone) = await probe(ct).ConfigureAwait(false);
                        if (earlyDone)
                        {
                            return (Wait.Signature(latest, early), true);
                        }

                        await ResolvePauseAsync(h, label, latest, quiet, report, ct).ConfigureAwait(false);
                    }

                    var (signature, done) = await probe(ct).ConfigureAwait(false);
                    return (Wait.Signature(latest, signature), done);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            failure = ExceptionDispatchInfo.Capture(e);
        }
        catch (OperationCanceledException e)
        {
            failure = ExceptionDispatchInfo.Capture(e);
        }

        // The pause is made even when the wait failed or was cancelled, under its own bound; the wait's error wins.
        using var pauseBound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await h.CallAsync(label + "-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, pauseBound.Token).ConfigureAwait(false);
        }
        catch (AcceptanceException) when (failure is not null)
        {
            // Reported through the wait's error.
        }
        catch (OperationCanceledException) when (failure is not null)
        {
            // Reported through the wait's error.
        }

        failure?.Throw();
        return latest > first ? latest - first : 0;
    }

    /// <summary><see cref="RunUntilAsync(Harness, string, ulong, Wait, Probe, bool, Report?, CancellationToken)"/> for a loud world with no report.</summary>
    public static Task<ulong> RunUntilAsync(Harness h, string label, ulong ticks, Wait w, Probe probe, CancellationToken cancellationToken) =>
        RunUntilAsync(h, label, ticks, w, probe, quiet: false, report: null, cancellationToken);

    /// <summary>
    /// RunUntil's answer to a probe that found the game paused: it reads the pause state and either clears the cause and
    /// resumes <see cref="RunSpeed"/>, or throws the <see cref="PauseCause"/> found.
    /// </summary>
    private static async Task ResolvePauseAsync(Harness h, string label, ulong tick, bool quiet, Report? report, CancellationToken cancellationToken)
    {
        var status = await ReadPauseStateAsync(h.WireFunc(), label + "-paused", cancellationToken).ConfigureAwait(false);
        var (cause, dismiss) = ClassifyPause(status, h.Tools, label, tick, quiet);
        if (cause is not null)
        {
            throw cause;
        }

        foreach (var letter in dismiss)
        {
            var reply = await h.CallAsync(label + "-dismiss", DismissLetterTool, new JsonObject { ["letterId"] = letter["id"]?.DeepClone() }, cancellationToken).ConfigureAwait(false);
            if (reply["removed"].AsBool() != true)
            {
                throw new AcceptanceException($"{label}: letter {letter["id"].ToJson()} was not on the stack: {reply.ToJson()}");
            }

            if (report is not null)
            {
                if (report.Data["dismissed_letters"] is not JsonArray rows)
                {
                    rows = [];
                    report.Data["dismissed_letters"] = rows;
                }

                rows.Add(new JsonObject
                {
                    ["phase"] = label,
                    ["tick"] = (long)tick,
                    ["id"] = letter["id"]?.DeepClone(),
                    ["label"] = letter["label"]?.DeepClone(),
                    ["letterDef"] = letter["letterDef"]?.DeepClone(),
                });
            }
        }

        if (dismiss.Count > 0)
        {
            await h.CallAsync(label + "-resume", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = RunSpeed, ["ultraSpeedBoost"] = RunBoost }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The "loaded" outcome of lifecycle_read_identity.</summary>
    private static async Task<JsonObject> ReadLoadedAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(reply, "loaded");
        return loaded;
    }
}
