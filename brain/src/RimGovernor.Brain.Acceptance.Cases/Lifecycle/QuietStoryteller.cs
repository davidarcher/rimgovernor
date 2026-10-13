using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/quiet_storyteller</c> (Go <c>quiet_storyteller.go</c>): the shipped RimGovernorQuiet StorytellerDef has
/// no storyteller comps, keeps none after a save and reload, and fires no incident over a day of ticks. Nothing here
/// uses the <c>test/quiet_storyteller</c> apply path; its inspect action is only the read.
/// </summary>
internal static class LifecycleQuietStoryteller
{
    private const string QuietStorytellerDef = "RimGovernorQuiet";

    /// <summary>One in-game day.</summary>
    private const ulong DayTicks = 60000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/quiet_storyteller",
        Scope = "RimGovernorQuiet storyteller def (#2023): a colony started with it via the new-colony op reports that storyteller with zero storytellerComps, "
            + "keeps both after a save and lifecycle_load, and a day of ticks leaves the letter stack empty (no incident). Residuals "
            + "(inspirations, social fights, map-gen strangers and hives) are not asserted.",
        Start = new OwnedStart(),
        Reason = "the start needs a fresh main menu process",
        NoKeep = true,
        Budget = TimeSpan.FromMinutes(20),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static Task RunAsync(ISession s, CancellationToken cancellationToken) =>
        NewColonyKit.MenuAsync(
            s,
            "quiet",
            ["rimgovernor/lifecycle_new_colony", "rimgovernor/lifecycle_save", "rimgovernor/lifecycle_load", DebugGame.QuietStorytellerTool],
            async (h, row) =>
            {
                var report = s.Report;
                row["storyteller"] = QuietStorytellerDef;
                var spec = NewColonyKit.Spec();
                spec["storyteller"] = QuietStorytellerDef;
                spec["seed"] = "quietteller-accept";
                spec["saveName"] = "quietteller-accept";
                _ = await NewColonyKit.RunAsync(h, $"quietteller-{LifecycleCases.Stamp()}", spec, "quiet", cancellationToken).ConfigureAwait(false);
                _ = await h.CallAsync("quiet-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
                await InspectAsync(h, "fresh", report, cancellationToken).ConfigureAwait(false);

                var identity = await Session.ReadIdentityAsync(h, "quiet-identity", cancellationToken).ConfigureAwait(false);
                var saveName = $"quietteller-{LifecycleCases.Stamp()}";
                var saved = await h.WireAsync(
                    "quiet-save",
                    "lifecycle_save",
                    new JsonObject
                    {
                        ["player"] = new JsonObject { ["identity"] = identity.DeepClone(), ["playerDirection"] = 1, ["requestId"] = saveName },
                        ["saveName"] = saveName,
                    },
                    cancellationToken).ConfigureAwait(false);
                _ = LifecycleCases.Outcome("save", saved, "completed");
                var loadId = saveName + "-load";
                var loadReply = await h.WireAsync(
                    "quiet-load",
                    "lifecycle_load",
                    new JsonObject
                    {
                        ["requestId"] = loadId,
                        ["saveName"] = saveName,
                        ["readiness"] = "READINESS_MAP",
                        ["expectedPlayer"] = new JsonObject { ["identity"] = identity.DeepClone(), ["playerDirection"] = 1, ["requestId"] = loadId },
                        ["playerDirection"] = 1,
                    },
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    _ = await GameReuse.PollLoadAsync(h, loadReply, loadId, "quiet-load-poll", cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"reload: {e.Message}", e);
                }

                await InspectAsync(h, "reloaded", report, cancellationToken).ConfigureAwait(false);

                // A day of ticks: any incident delivers a letter, and a pausing letter fails RunUntil with a PauseCause.
                var start = await h.TickAsync(cancellationToken).ConfigureAwait(false);
                var advanced = await RunAt.RunUntilAsync(
                    h,
                    "quiet-day",
                    2 * DayTicks,
                    new Wait(),
                    async token =>
                    {
                        var tick = await h.TickAsync(token).ConfigureAwait(false);
                        var letters = await RunAt.ReadLettersAsync(h.WireFunc(), "quiet-day", null, token).ConfigureAwait(false);
                        if (letters.Count > 0)
                        {
                            throw new AcceptanceException($"tick {tick}: a quiet colony delivered letters: [{string.Join(' ', letters.Select(l => l.ToJson()))}]");
                        }

                        return ("", tick >= start + DayTicks);
                    },
                    HarnessQuiet.Get(h),
                    report,
                    cancellationToken).ConfigureAwait(false);
                report["quiet_day_ticks"] = advanced;
            },
            cancellationToken);

    /// <summary>Asserts the loaded game's storyteller is RimGovernorQuiet with no comps, via the fixture op's read-only inspect action.</summary>
    private static async Task InspectAsync(Harness h, string label, Report report, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync("quiet-inspect-" + label, DebugGame.QuietStorytellerTool, new JsonObject { ["action"] = "inspect" }, cancellationToken).ConfigureAwait(false);
        var got = reply["storyteller"].AsString();
        if (got != QuietStorytellerDef)
        {
            throw new AcceptanceException($"{label}: storyteller \"{got}\", want {QuietStorytellerDef}");
        }

        var comps = (int)reply["comps"].AsNumber();
        if (comps != 0)
        {
            throw new AcceptanceException($"{label}: storytellerComps has {comps} entries, want 0");
        }

        report["quiet_" + label] = new JsonObject { ["storyteller"] = reply["storyteller"]?.DeepClone(), ["comps"] = reply["comps"]?.DeepClone() };
    }
}
