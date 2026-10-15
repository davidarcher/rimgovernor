using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Explicit native ritual mechanics after normal review declines unsupported participant planning.</summary>
internal static class IdeologyRitual
{
    private const string PrepareTool = "test/first_ritual_prepare";
    private const string InspectTool = "test/first_ritual_inspect";

    // Bounds the native run after the begin: attendees walk to the building and the lord job runs the ritual to its end.
    private const ulong RitualTicks = 30000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "ideology/first-ritual",
        Scope = "Normal review explicitly leaves ritual labor unsupported and admits no Ritual action. An explicit intent carries the native dialog roles and spectators; after authority release the native ritual lord completes and advances lastFinishedTick. This is mechanics coverage, not automatic participant planning.",
        Start = new FixtureStart { Op = PrepareTool, On = new LabStart() },
        Expansions = ["ludeon.rimworld.ideology"],
        NoKeep = true,
        Quiet = QuietMode.Required,
        RequiredOps = [PrepareTool, InspectTool],

        // Preserve the normal review and role configuration before the explicit mechanics phase.
        Serve = new ServeSpec
        {
            Families = ["rituals", "ideo-roles", "shelter", "dialog"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "ideology-first-ritual",
        },
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        var ritual = prepared["ritualId"].AsString();
        var building = prepared["buildingId"].AsString();
        var baseline = prepared["lastFinishedTick"].AsNumber();
        if (ritual.Length == 0 || building.Length == 0 || prepared["pawnIds"].AsSlice().Count < 2)
        {
            throw new AcceptanceException($"fixture: unexpected first-ritual staging: {prepared.ToJson()}");
        }

        report["ritual"] = ritual;
        report["fixture"] = prepared.DeepClone();

        var h = s.Harness;
        async Task<JsonObject> InspectAsync(string label, CancellationToken ct)
        {
            var reply = await h.CallAsync(label, InspectTool, new JsonObject { ["ritualId"] = ritual }, ct).ConfigureAwait(false);
            return reply["success"].AsBool() != true
                ? throw new AcceptanceException($"ritual inspect refused: {reply.ToJson()}")
                : reply;
        }

        h = await UnsupportedLabor.ReviewAsync(s, "unsupported_forecast_ritual",
            RimGovernor.Brain.Domain.ActionKind.Ritual, cancellationToken).ConfigureAwait(false);
        var explicitRead = await InspectAsync("ritual-explicit-assignment", cancellationToken).ConfigureAwait(false);
        var intent = JsonNode.Parse(explicitRead["explicitIntent"].AsString()) as JsonObject
            ?? throw new AcceptanceException("Native ritual dialog supplied no complete legal explicit assignment.");
        var receipt = await ApplyIntents.ApplyOneAsync(h, "explicit-ritual", s.Identity, "explicit-ritual",
            new JsonObject { ["ritual"] = intent }, cancellationToken).ConfigureAwait(false);
        if (receipt["refused"] is not null) throw new AcceptanceException("Explicit ritual refused: " + receipt.ToJson());
        report["explicit_receipt"] = receipt.DeepClone();
        _ = await Authority.ReleaseAuthorityAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        // Native postcondition after the service releases the game: the ritual's lord job runs on and the precept's
        // last finished tick advances past the staged baseline.
        h = s.Reattach();
        var sawRunning = false;
        double finishedTick = 0;
        double participants = 0;
        var elapsed = await RunAt.RunUntilAsync(
            h,
            "ritual-finished",
            RitualTicks,
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                var reply = await InspectAsync("ritual-inspect", ct).ConfigureAwait(false);
                var running = reply["running"].AsBool() == true;
                finishedTick = reply["lastFinishedTick"].AsNumber();
                if (running)
                {
                    sawRunning = true;
                    participants = reply["participants"].AsNumber();
                }

                return (Wait.Signature(running, finishedTick, participants), finishedTick > baseline);
            },
            cancellationToken).ConfigureAwait(false);
        report["ritual_ticks"] = elapsed;
        report["saw_running"] = sawRunning;
        report["participants"] = participants;
        report["last_finished_tick"] = finishedTick;
        if (finishedTick <= baseline)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"ritual {ritual} last finished tick {finishedTick} did not advance past {baseline}"));
        }
    }
}
