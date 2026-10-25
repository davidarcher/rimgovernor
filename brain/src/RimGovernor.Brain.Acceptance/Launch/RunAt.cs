using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The game's pause state and its visible causes (<see cref="RunAt.ReadPauseStateAsync"/>): lifecycle_read_tick's
/// paused, forcePaused, timeSpeed, tick (-1 when unknown) and force-pausing windows, and presentation_notifications'
/// letter stack, newest first.
/// </summary>
public sealed record PauseState(
    long Tick,
    bool Paused,
    bool ForcePaused,
    string? TimeSpeed,
    IReadOnlyList<RimGovernor.Brain.GameFacts.Lifecycle.PausingWindow> Windows,
    IReadOnlyList<RimGovernor.Brain.GameFacts.Presentation.Letter> Letters);

/// <summary>
/// Why the game stopped ticking under <see cref="RunAt.RunUntilAsync(Harness, string, ulong, Wait, Probe, bool, Report?, CancellationToken)"/>:
/// what <see cref="RunAt.ReadPauseStateAsync"/> showed when a probe found the game paused although the harness asked for
/// <see cref="RunAt.RunSpeed"/>. A wait fails with it at once instead of burning its stall budget.
/// </summary>
public sealed class PauseCause : AcceptanceException
{
    public PauseCause(string label, ulong tick, PauseState status)
        : base(Describe(label, tick, status))
    {
        Label = label;
        Tick = tick;
        Status = status;
    }

    public string Label { get; }

    public ulong Tick { get; }

    /// <summary>The pause state read when the pause was found.</summary>
    public PauseState Status { get; }

    private static string Describe(string label, ulong tick, PauseState status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var parts = new List<string>();
        foreach (var letter in status.Letters)
        {
            parts.Add($"letter \"{letter.Label}\" ({letter.DefName})");
        }

        foreach (var window in status.Windows)
        {
            parts.Add($"window {window.NativeType} \"{window.Title}\"");
        }

        var cause = parts.Count == 0 ? "no letter or force-pausing window" : string.Join(", ", parts);
        var forced = status.ForcePaused ? "true" : "false";
        return string.Create(CultureInfo.InvariantCulture, $"{label}: game paused at tick {tick} while running (forcePaused={forced}): {cause}");
    }
}

/// <summary>Running the game while a harness waits for it to do something.</summary>
public static class RunAt
{
    /// <summary>
    /// How a harness runs the game while it waits for the game to do something: Hyperfast, RimWorld's Ultrafast with the
    /// dev tick boost on, which native caps at 30x (1800 ticks/s). Measured on the debug colony: Fast 168 ticks/s,
    /// Superfast 348, Ultrafast 899, so a wait stated in ticks costs the least wall clock. Neither needs Prefs.devMode
    /// (which would add a 35 s def check to every boot). Waits bounded in ticks mean the same at any speed, so nothing
    /// about the assertion depends on it.
    /// </summary>
    public const string RunSpeed = "Hyperfast";

    /// <summary>RunUntil's default pause between probes.</summary>
    public static readonly TimeSpan RunInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Removes one letter from the stack (scripts/fixtures/LetterFixture.cs); every fixture build carries it.</summary>
    public const string DismissLetterTool = "test/dismiss_letter";

    // The informational letter defs a running wait acknowledges by default: an unanswered outcome is harmless to an
    // assertion (a joiner leaves, a quest stays offered). Threat letters are never here.
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

    /// <summary>The game's pause state and its visible causes.</summary>
    public static async Task<PauseState> ReadPauseStateAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(h);
        var loaded = await h.LoadedTickAsync(label + "-tick", cancellationToken).ConfigureAwait(false);
        var letters = await ReadLettersAsync(h, label, loaded.Context?.Identity, cancellationToken).ConfigureAwait(false);
        return new PauseState(
            loaded.Context?.Tick ?? -1,
            loaded.Paused == true,
            loaded.ForcePaused == true,
            loaded.TimeSpeed,
            [.. loaded.PausingWindows],
            letters);
    }

    /// <summary>presentation_notifications' letter stack, newest first.</summary>
    public static async Task<IReadOnlyList<RimGovernor.Brain.GameFacts.Presentation.Letter>> ReadLettersAsync(Harness h, string label, RimGovernor.Brain.GameFacts.Common.Identity? identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(h);
        var snapshot = await h.CallAsync(
            label + "-letters",
            NativeOps.ReadNotifications,
            new RimGovernor.Brain.GameOrders.Presentation.NotificationsRequest { Identity = identity, IncludeLetters = true, IncludeMessages = false, IncludeAlerts = false, LetterLimit = 40, MessageLimit = 12, AlertLimit = 40 },
            cancellationToken).ConfigureAwait(false);
        var observed = snapshot.Letters?.Outcome?.Observed
            ?? throw new AcceptanceException($"{label}: letter section not observed: {OrdersJson.Serialize(snapshot)}");
        return [.. observed.Items];
    }

    /// <summary>
    /// Decides what a paused <see cref="ReadPauseStateAsync"/> means for a running wait. A status that shows the game
    /// running again is a transient (no cause, nothing to dismiss). A stack of letters that are all acknowledged defs is
    /// returned to dismiss (when the catalog carries the dismiss tool), as the service acknowledges them, since a
    /// bridge-only case has no routine layer to do it. Quiet starts also dismiss the ancient-shrine discovery warning: it
    /// is map discovery, not an incident the quiet storyteller can suppress. A force-pausing window, any other letter, or
    /// a pause with no visible cause is the <see cref="PauseCause"/> that fails the wait.
    /// </summary>
    public static (PauseCause? Cause, IReadOnlyList<RimGovernor.Brain.GameFacts.Presentation.Letter> Dismiss) ClassifyPause(PauseState status, IReadOnlyList<string> tools, string label, ulong tick, bool quiet)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(tools);
        if (!status.Paused)
        {
            return (null, []);
        }

        var cause = new PauseCause(label, tick, status);
        if (status.Windows.Count > 0 || status.Letters.Count == 0 || !Session.HasTool(tools, DismissLetterTool))
        {
            return (cause, []);
        }

        foreach (var letter in status.Letters)
        {
            var definition = letter.DefName ?? "";
            var discovery = quiet && letter.Label == "Ancient danger" && definition == "ThreatBig";
            if (!AcknowledgedLetterDefs.Contains(definition) && !discovery)
            {
                return (cause, []);
            }
        }

        return (null, status.Letters);
    }

    /// <summary>
    /// Runs the game at <see cref="RunSpeed"/> and polls <paramref name="probe"/> until it is done, then pauses;
    /// <paramref name="ticks"/> is the game-time budget and <paramref name="w"/>'s own bounds still apply (Interval defaults
    /// to 250 ms, Tick is set here). It returns how many ticks the game advanced. The probe's signature may be empty: the
    /// tick is always part of the signature, so a game that stops ticking stalls. A game found paused is resolved before
    /// the probe: benign letters are acknowledged and the run resumed, anything else fails the wait at once with a
    /// <see cref="PauseCause"/>. <paramref name="quiet"/> says the world was started quiet (its discovery warning is
    /// dismissed too) and <paramref name="report"/>, when given, collects the dismissed_letters rows.
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

        await h.SetTimeSpeedAsync(label + "-run", new() { Speed = RunSpeed }, cancellationToken).ConfigureAwait(false);

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
                var loaded = await h.LoadedAsync("tick", ct).ConfigureAwait(false);
                latest = (ulong)Harness.Context(loaded, "tick").Tick;
                paused = loaded.Paused == true;
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
            await h.PauseClockAsync(label + "-pause", pauseBound.Token).ConfigureAwait(false);
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
        var status = await ReadPauseStateAsync(h, label + "-paused", cancellationToken).ConfigureAwait(false);
        var (cause, dismiss) = ClassifyPause(status, h.Tools, label, tick, quiet);
        if (cause is not null)
        {
            throw cause;
        }

        foreach (var letter in dismiss)
        {
            var arguments = FixtureRecordJson.Args(new Fx.DismissLetterArgs { LetterId = letter.Id });
            var reply = await h.CallAsync(label + "-dismiss", DismissLetterTool, arguments, cancellationToken).ConfigureAwait(false);
            if (!Harness.FixtureReply<Fx.LetterDismissed>(DismissLetterTool, reply).Removed)
            {
                throw new AcceptanceException($"{label}: letter \"{letter.Id}\" was not on the stack: {reply.ToJsonString()}");
            }

            if (report is not null)
            {
                report.AddDismissedLetter(OutputJson.Fields(("phase", label), ("tick", (long)tick), ("id", letter.Id), ("label", letter.Label), ("letterDef", letter.DefName)));
            }
        }

        if (dismiss.Count > 0)
        {
            await h.SetTimeSpeedAsync(label + "-resume", new() { Speed = RunSpeed }, cancellationToken).ConfigureAwait(false);
        }
    }
}
