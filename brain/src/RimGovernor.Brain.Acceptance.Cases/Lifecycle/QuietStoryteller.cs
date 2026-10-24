using RimGovernor.Contracts.Wire;
using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;
using Presentation = RimGovernor.Contracts.Wire.Presentation;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/quiet_storyteller</c>: the shipped RimGovernorQuiet StorytellerDef started through the new-colony op has
/// no storyteller comps, keeps none after a save and reload, and fires no incident over a day of ticks. Nothing here uses
/// the <c>test/quiet_storyteller</c> apply path; its inspect action is only the read.
/// </summary>
internal static class LifecycleQuietStoryteller
{
    private const string QuietStorytellerDef = "RimGovernorQuiet";

    private static readonly FixtureOp<string, Fx.QuietStorytellerRead> Inspect = new(
        DebugGame.QuietStorytellerTool,
        action => new JsonObject([new KeyValuePair<string, JsonNode?>("action", action)]),
        (label, reply) => reply is not null
            ? Fx.FixtureJson.Deserialize<Fx.QuietStorytellerRead>(reply.ToJsonString())
            : throw new AcceptanceException($"{label}: {DebugGame.QuietStorytellerTool} replied nothing"));

    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/quiet_storyteller",
        Scope = "RimGovernorQuiet storyteller def (#2023): a colony started with it through lifecycle_new_colony reports that storyteller with zero storytellerComps, "
            + "keeps both after lifecycle_save and lifecycle_load, and a day of ticks leaves the letter stack empty (no incident). Residuals "
            + "(inspirations, social fights, map-gen strangers and hives) are not asserted.",
        Ticks = Wait.TicksPerDay,
        Tier = CaseTier.OnDemand,
        Start = new OwnedStart(),
        ClaimsStart = new StartClaim { Launches = 1 },
        Ops = [DebugGame.QuietStorytellerTool],
        Run = RunAsync,
    };

    private static Task RunAsync(TypedSession s, CancellationToken cancellationToken) =>
        NewColonyStart.MenuAsync(
            s,
            "quiet",
            async () =>
            {
                var spec = NewColonyStart.Spec() with { Storyteller = QuietStorytellerDef, Seed = "quietteller-accept", SaveName = "quietteller-accept" };
                var (completed, _) = await NewColonyStart.CompleteAsync(s, "quiet", NewColonyStart.Request(TypedLifecycle.Stamp("quietteller"), spec), cancellationToken).ConfigureAwait(false);
                await InspectAsync(s, "fresh", cancellationToken).ConfigureAwait(false);

                var identity = completed.Context?.Identity ?? throw new AcceptanceException($"the completed start names no identity: {completed}");
                var saveName = TypedLifecycle.Stamp("quietteller");
                _ = await TypedLifecycle.SaveAsync(s, "save", identity, saveName, cancellationToken).ConfigureAwait(false);
                _ = await TypedLifecycle.LoadAsync(s, "reload", identity, saveName, Lifecycle.Readiness.Map, cancellationToken).ConfigureAwait(false);
                await InspectAsync(s, "reloaded", cancellationToken).ConfigureAwait(false);

                // A day of ticks: any incident delivers a letter, and a pausing letter fails the wait with its cause.
                var (loaded, start, _) = await TypedLifecycle.IdentityAsync(s, "day-start", cancellationToken).ConfigureAwait(false);
                var ran = await s.RunUntilAsync(
                    "quiet-day",
                    Wait.TicksPerDay,
                    async ct =>
                    {
                        var (_, tick, _) = await TypedLifecycle.IdentityAsync(s, "day-tick", ct).ConfigureAwait(false);
                        var snapshot = await s.CallAsync(
                            "day-letters",
                            NativeOps.ReadNotifications,
                            new Presentation.NotificationsRequest { Identity = loaded, IncludeLetters = true, IncludeMessages = false, IncludeAlerts = false, LetterLimit = 40, MessageLimit = 12, AlertLimit = 40 },
                            ct).ConfigureAwait(false);
                        var letters = snapshot.Letters?.Outcome?.Observed?.Items
                            ?? throw new AcceptanceException($"day-letters: no letter section: {WireJson.Serialize(snapshot)}");
                        if (letters.Count > 0)
                        {
                            throw new AcceptanceException(string.Create(
                                CultureInfo.InvariantCulture,
                                $"tick {tick}: a quiet colony delivered letters: [{string.Join(' ', letters.Select(l => l.DefName))}]"));
                        }

                        return (string.Empty, tick >= start + Wait.TicksPerDay);
                    },
                    cancellationToken).ConfigureAwait(false);
                s.Record("quiet_day_ticks", ran);
            },
            cancellationToken);

    /// <summary>Asserts the loaded game's storyteller is RimGovernorQuiet with no comps, through the fixture op's read-only inspect action.</summary>
    private static async Task InspectAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var read = await s.FixtureAsync("inspect-" + label, Inspect, "inspect", cancellationToken).ConfigureAwait(false);
        s.Record("quiet_" + label, read.Storyteller + "/" + read.Comps.ToString(CultureInfo.InvariantCulture));
        if (read.Storyteller != QuietStorytellerDef || read.Comps != 0)
        {
            throw new AcceptanceException($"{label}: want {QuietStorytellerDef} with no storytellerComps, got {read}");
        }
    }
}
