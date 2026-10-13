using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>letter/pause</c> (Go <c>pause.go</c>): proves AdvanceGame's letter handling. An informational letter that pauses
/// the clock is acknowledged, dismissed through test/dismiss_letter and the window completes; an informational letter
/// that does not pause under the profile's AutomaticPauseMode leaves the window untouched; an unexpected threat letter
/// still interrupts, and a strict window (WithExpectedLetters()) interrupts on an informational one. It also checks
/// rimgovernor/presentation_notifications while a letter is up.
/// </summary>
internal static class LetterPause
{
    private const string NotApproved = "Letter is not approved for this scenario";

    internal static AcceptanceCase Case() => new()
    {
        Name = "letter/pause",
        Scope = "AdvanceGame acknowledges and dismisses informational letters, ignores ones that do not pause, "
            + "and still interrupts on an unexpected threat letter or, in strict mode, on any letter.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    // Reads rimgovernor/presentation_notifications: the letter under test is listed with its def, the letter listing
    // describes every row, messages and alerts are observed, and an explicit include_letters:false leaves the letter
    // section absent.
    private static async Task<JsonObject> NotificationsMatchAsync(Harness h, JsonObject identity, string letterId, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync("notifications", "presentation_notifications", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        JsonObject snapshot;
        try
        {
            (_, snapshot) = Harness.Outcome(reply, "notifications");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"presentation_notifications: {e.Message}", e);
        }

        JsonObject Observed(string section)
        {
            if (snapshot[section].AsMap() is not { } raw)
            {
                throw new AcceptanceException($"presentation_notifications: {section} section absent");
            }

            try
            {
                return Harness.Outcome(raw, "observed").Value;
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"presentation_notifications: {section}: {e.Message}", e);
            }
        }

        var letters = Observed("letters");
        var rows = letters["letters"].AsSlice();
        var listing = letters["listing"].AsMap();
        if ((int)listing.Member("totalCount").AsNumber() != rows.Count || (int)listing.Member("returnedCount").AsNumber() != rows.Count || listing.Member("complete").AsBool() != true)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"typed letter listing does not describe {rows.Count} rows: {listing.ToJson()}"));
        }

        JsonObject? found = null;
        foreach (var raw in rows)
        {
            if (raw.AsMap() is { } row && row["id"].AsString() == letterId)
            {
                found = row;
            }
        }

        if (found is null)
        {
            throw new AcceptanceException($"typed letters omit {letterId}: {letters["letters"].ToJson()}");
        }

        if (found["defName"].AsString() != "PositiveEvent" || found["label"].AsString().Length == 0 || found["nativeType"].AsString().Length == 0)
        {
            throw new AcceptanceException($"typed letter {letterId} lacks def, label or type: {found.ToJson()}");
        }

        _ = Observed("messages");
        var alerts = Observed("alerts");
        if (alerts["snapshotFingerprint"].AsString().Length == 0)
        {
            throw new AcceptanceException($"typed alerts lack a snapshot fingerprint: {alerts.ToJson()}");
        }

        // A fresh debug colony always has at least "Need colonist beds"; an empty list here means the headless alert
        // readout stopped running.
        if (alerts["alerts"].AsSlice().Count == 0)
        {
            throw new AcceptanceException("typed alerts are empty on a fresh colony: the headless AlertsReadout is not updating");
        }

        var narrowed = await h.WireAsync(
            "notifications-no-letters",
            "presentation_notifications",
            new JsonObject { ["identity"] = identity.DeepClone(), ["includeLetters"] = false, ["alertLimit"] = 1 },
            cancellationToken).ConfigureAwait(false);
        JsonObject narrowedSnapshot;
        try
        {
            (_, narrowedSnapshot) = Harness.Outcome(narrowed, "notifications");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"presentation_notifications(includeLetters=false): {e.Message}", e);
        }

        if (narrowedSnapshot.ContainsKey("letters"))
        {
            throw new AcceptanceException("includeLetters:false still returned a letter section");
        }

        if (narrowedSnapshot["alerts"].AsMap() is not { } narrowedAlerts || !narrowedAlerts.ContainsKey("observed"))
        {
            throw new AcceptanceException($"includeLetters:false lost the alert section: {narrowedSnapshot.ToJson()}");
        }

        return new JsonObject
        {
            ["letterId"] = letterId,
            ["letters"] = rows.Count,
            ["alerts"] = alerts["alerts"].AsSlice().Count,
            ["alertFingerprint"] = alerts["snapshotFingerprint"]?.DeepClone(),
        };
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var names = s.Names;
        foreach (var tool in new[] { ScenarioRuntime.DismissLetterTool, "test/deliver_letter", "test/letter_pause_mode", "rimgovernor/presentation_notifications" })
        {
            if (!Dyn.Contains(names, tool))
            {
                throw new AcceptanceException($"{tool} is not installed: build the mod with -Fixture LetterFixture");
            }
        }

        var supervisor = new ScenarioClock(h.WireFunc(), identity, Draft.Controller, report);
        _ = await supervisor.AcquireAsync("acquire", cancellationToken).ConfigureAwait(false);
        var rt = new ScenarioRuntime(h.CallAsync, supervisor, report, names);
        var window = TimeSpan.FromSeconds(120);

        async Task<string> Deliver(string label, string definition)
        {
            var reply = await h.CallAsync(
                label,
                "test/deliver_letter",
                new JsonObject { ["definition"] = definition, ["label"] = label, ["delayTicks"] = 60 },
                cancellationToken).ConfigureAwait(false);
            return reply["success"].AsBool() == true
                ? reply["letterId"].AsString()
                : throw new AcceptanceException($"{label}: deliver_letter refused: {reply.ToJson()}");
        }

        async Task<bool> OnStack(string label, string letterId)
        {
            var letters = await RunAt.ReadLettersAsync(h.WireFunc(), label, identity, cancellationToken).ConfigureAwait(false);
            return letters.Any(row => row["id"].AsString() == letterId);
        }

        List<JsonObject> NotesSince(int from)
        {
            var output = new List<JsonObject>();
            var notes = report["notes"].AsSlice();
            for (var i = from; i < notes.Count; i++)
            {
                if (notes[i].AsMap() is { } row && row["kind"].AsString() == "scenario_interruption")
                {
                    output.Add(row);
                }
            }

            return output;
        }

        async Task SetPauseMode(string mode, CancellationToken token)
        {
            var reply = await h.CallAsync("pause-mode-" + mode, "test/letter_pause_mode", new JsonObject { ["mode"] = mode }, token).ConfigureAwait(false);
            if (reply["mode"].AsString() != mode)
            {
                throw new AcceptanceException($"letter_pause_mode did not take: {reply.ToJson()}");
            }
        }

        // The strict-mode and unexpected-threat windows leave the letter on the stack and the clock paused; clear the
        // letter before the next window.
        async Task Dismiss(string label, string letterId)
        {
            var reply = await h.CallAsync(label, ScenarioRuntime.DismissLetterTool, new JsonObject { ["letterId"] = letterId }, cancellationToken).ConfigureAwait(false);
            if (reply["removed"].AsBool() != true)
            {
                throw new AcceptanceException($"{label}: letter {letterId} was not on the stack: {reply.ToJson()}");
            }
        }

        // Prefs are process-wide and outlive a reused game (RIMGOVERNOR_ACCEPT_KEEP_GAME): put the pause mode back for
        // the next harness.
        var initial = await h.CallAsync("pause-mode-initial", "test/letter_pause_mode", [], cancellationToken).ConfigureAwait(false);
        try
        {
            var summary = new JsonObject();
            report["letters"] = summary;

            // 1. Under the headless profile's pause mode (MajorThreat, set here explicitly so a reused process cannot
            // hand over another) an informational letter does not pause: no interruption, and the letter stays on the
            // stack for the harness to clear.
            await SetPauseMode("MajorThreat", cancellationToken).ConfigureAwait(false);
            var before = report["notes"].AsSlice().Count;
            var quietId = await Deliver("quiet-positive", "PositiveEvent").ConfigureAwait(false);
            try
            {
                _ = await AdvanceGame.RunAsync(rt, 300, [AdvanceOptions.WithTimeout(window)], cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"non-pausing informational window: {e.Message}", e);
            }

            var interruptions = NotesSince(before).Count;
            if (interruptions != 0)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"non-pausing informational letter interrupted the window {interruptions} times"));
            }

            if (!await OnStack("quiet-stack", quietId).ConfigureAwait(false))
            {
                throw new AcceptanceException($"non-pausing informational letter {quietId} never reached the stack");
            }

            await Dismiss("quiet-dismiss", quietId).ConfigureAwait(false);
            summary["non_pausing"] = new JsonObject { ["letterId"] = quietId, ["interruptions"] = 0 };

            // 2. Pausing on any letter: the informational letter pauses the clock, AdvanceGame acknowledges and
            // dismisses it and the window completes.
            await SetPauseMode("AnyLetter", cancellationToken).ConfigureAwait(false);
            before = report["notes"].AsSlice().Count;
            var ackId = await Deliver("acknowledged-positive", "PositiveEvent").ConfigureAwait(false);
            JsonObject final;
            try
            {
                final = await AdvanceGame.RunAsync(rt, 300, [AdvanceOptions.WithTimeout(window)], cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"acknowledged informational window: {e.Message}", e);
            }

            var acks = NotesSince(before);
            if (acks.Count != 1 || acks[0]["acknowledgedLetterId"].AsString() != ackId || acks[0]["informational"].AsBool() != true)
            {
                throw new AcceptanceException($"expected one informational acknowledgement of {ackId}, got {new JsonArray([.. acks.Select(a => (JsonNode?)a.DeepClone())]).ToJson()}");
            }

            var dismissed = acks[0]["dismissed"].AsMap();
            if (dismissed?["removed"].AsBool() != true)
            {
                throw new AcceptanceException($"acknowledged letter was not dismissed: {acks[0].ToJson()}");
            }

            if (await OnStack("acknowledged-stack", ackId).ConfigureAwait(false))
            {
                throw new AcceptanceException($"acknowledged letter {ackId} is still on the stack");
            }

            if (final["stopReason"].AsString() != "tick_budget")
            {
                throw new AcceptanceException($"acknowledged window did not run to its budget: {final.ToJson()}");
            }

            summary["acknowledged"] = new JsonObject { ["letterId"] = ackId, ["dismissed"] = dismissed.DeepClone() };

            // 3. An unexpected threat letter still interrupts, even with acknowledgement on.
            var threatId = await Deliver("unexpected-threat", "ThreatBig").ConfigureAwait(false);
            var threat = await Interrupted(rt, [AdvanceOptions.WithTimeout(window)], "unexpected threat letter should interrupt the window", cancellationToken).ConfigureAwait(false);
            await Dismiss("threat-dismiss", threatId).ConfigureAwait(false);
            summary["unexpected_threat"] = new JsonObject { ["letterId"] = threatId, ["reason"] = threat.Reason };

            // 4. A strict window (interruption harnesses) refuses the informational letter too.
            var strictId = await Deliver("strict-positive", "PositiveEvent").ConfigureAwait(false);
            var strict = await Interrupted(rt, [AdvanceOptions.WithTimeout(window), AdvanceOptions.WithExpectedLetters()], "strict window should interrupt on an informational letter", cancellationToken).ConfigureAwait(false);
            if (!await OnStack("strict-stack", strictId).ConfigureAwait(false))
            {
                throw new AcceptanceException($"strict window dismissed letter {strictId}");
            }

            // 5. The typed notifications read lists the letter under test and answers every section; an explicit
            // include_letters:false omits only the letter section.
            summary["typed_notifications"] = await NotificationsMatchAsync(h, identity, strictId, cancellationToken).ConfigureAwait(false);
            await Dismiss("strict-dismiss", strictId).ConfigureAwait(false);
            summary["strict"] = new JsonObject { ["letterId"] = strictId, ["reason"] = strict.Reason };
        }
        finally
        {
            try
            {
                await SetPauseMode(initial["mode"].AsString(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (AcceptanceException tolerated)
            {
                // Best effort, as in Go: the case's own verdict stands.
                Tolerated.Note("pause mode restore", tolerated);
            }
        }
    }

    private static async Task<ScenarioInterruptedException> Interrupted(ScenarioRuntime rt, IScenarioAdvanceOption[] options, string what, CancellationToken cancellationToken)
    {
        try
        {
            _ = await AdvanceGame.RunAsync(rt, 300, options, cancellationToken).ConfigureAwait(false);
        }
        catch (ScenarioInterruptedException e) when (e.Reason == NotApproved)
        {
            return e;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{what}, got {e.Message}", e);
        }

        throw new AcceptanceException($"{what}, got <nil>");
    }
}
