using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>ideology/first-ritual</c> (Go <c>ritual.go</c>): the colony holds a ritual of its ideoligion on its own.
/// MaintainRituals finds the staged ritual due, plans its organizer and attendees at the finished building and commits
/// the Ritual begin; native runs the begin dialog's confirm action, and the game's own lord job holds the ritual to its
/// end (#1665).
/// </summary>
internal static class IdeologyRitual
{
    private const string PrepareTool = "test/first_ritual_prepare";
    private const string InspectTool = "test/first_ritual_inspect";

    // Bounds the begin wait; the stall budget, whose signature carries the game day, ends it earlier when nothing moves.
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(10);

    // Bounds the native run after the begin: attendees walk to the building and the lord job runs the ritual to its end.
    private const ulong RitualTicks = 30000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "ideology/first-ritual",
        Scope = "Issue #1665: a colony whose ideoligion has a free-start ritual and its finished building plans the ritual "
            + "through MaintainRituals, begins it through the Ritual begin verb, and the game's lord job of that ritual "
            + "precept runs to its end and advances the precept's last finished tick. A Go snapshot over recorded facts "
            + "cannot cover it: the start and the outcome are vanilla's own (the begin dialog's confirm action and role "
            + "assignment, attendees' availability, the lord job and its toils, the ritual's completion), asserted on the "
            + "live precept; the planner's decisions are policy/ritual_test.go. The fixture picks the ritual and its "
            + "building from the game's defs and places the building finished (the worship room's construction is "
            + "#1658's); it passes the ritual's cooldown.",
        Start = new FixtureStart { Op = PrepareTool, On = new LabStart() },
        Expansions = ["ludeon.rimworld.ideology"],
        NoKeep = true,
        Quiet = QuietMode.Required,
        RequiredOps = [PrepareTool, InspectTool],

        // rituals plans the begin and ideo-roles fills the role precepts its slots name; the shelter family keeps
        // supervised windows running (the planners alone never advance the clock) and dialog answers letters.
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

        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);

            // The colony's answer to a ritual that is due: one Ritual begin the game accepted. The Go case read the
            // begin's journalled stage; the in-mod brain's journal is the game's own, so the begin shows as the
            // precept's lord job running (or already finished). The signature carries the game day so a long wait for
            // the attendees does not read as a stall and a broken planner does.
            try
            {
                await new Wait { Ceiling = Ceiling, Stall = Wait.StallBudget(), Terminal = service.Exited }.WaitProgressAsync(
                    async ct =>
                    {
                        var reply = await InspectAsync("ritual-begin", ct).ConfigureAwait(false);
                        var running = reply["running"].AsBool() == true;
                        var finished = reply["lastFinishedTick"].AsNumber();
                        var state = await service.ApiAsync("GET", "/api/state", null, "", ct).ConfigureAwait(false);
                        var tick = state.Body.Member("game").AsMap()?["tick"].AsUInt64() ?? 0;
                        report["begun"] = running || finished > baseline;
                        return (Wait.Signature(running, finished, tick / (ulong)Wait.TicksPerDay), running || finished > baseline);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (WaitError e)
            {
                throw new AcceptanceException($"ritual begin: {e.Message}", e);
            }
        }
        finally
        {
            report["run_keepalive"] = (await service.StopAsync().ConfigureAwait(false))?.DeepClone();
        }

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
