using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/first-title</c> (Go <c>title.go</c>): the Royalty path from an Empire quest to a first royal title, end to
/// end in vanilla. The colony accepts the Empire favor quest through QuestAccept, native grants the favor and generates
/// the bestowing-ceremony quest, the title claim gate lets the colony accept it, the bestower waits and the Ritual
/// intent starts the ceremony, and the colonist ends up holding the Empire title.
/// </summary>
internal static class QuestTitle
{
    private const string PrepareTool = "test/royal_title_prepare";
    private const string PawnsReadWire = "observations_list_pawns";

    /// <summary>Bounds the journal wait; the stall budget ends it earlier when nothing moves.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(8);

    /// <summary>Bounds the native run after the ceremony command: the bestowing ritual runs a few thousand ticks before the title is set.</summary>
    private const ulong Ticks = 20000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "quest/first-title",
        Scope = "Issue #1613: a colony takes an Empire favor quest, accepts the bestowing-ceremony quest native generates " +
            "once the favor suffices, starts the ceremony and the colonist holds the Empire title natively. A Go snapshot over " +
            "recorded facts cannot cover it: the chain is vanilla's own (quest reward, favor, ceremony quest generation, " +
            "bestower lord, ritual and title assignment), asserted on the live royalty read.",
        Start = new FixtureStart { Op = PrepareTool, On = new LabStart() },
        Expansions = ["ludeon.rimworld.royalty"],
        NoKeep = true,
        Quiet = QuietMode.Required,
        RequiredOps = [PrepareTool],

        // The shelter family keeps supervised windows running (the population planner alone never advances the clock);
        // dialog answers the letters.
        Serve = new ServeSpec
        {
            Families = ["population-joiner", "shelter", "dialog"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "quest-first-title",
        },
        Budget = TimeSpan.FromMinutes(12),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>Whether the journal holds a completed bestowing ritual start for <paramref name="pawn"/>.</summary>
    private static async Task<bool> RitualStartedAsync(ICaseJournal journal, string pawn, CancellationToken cancellationToken)
    {
        var plans = await journal.PlanHistoryAsync(256, ["ritual-start-*"], cancellationToken).ConfigureAwait(false);
        foreach (var (kind, action) in CaseJournal.CompletedActions(plans))
        {
            if (kind == Wire.Name(ActionKind.Ritual) && action["pawn"].AsString() == pawn && action["ritual"].AsString() == RitualKind.Bestowing.Value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The title <paramref name="pawn"/> holds from any faction in a pawn list's royalty holdings (PawnState.royalty), with
    /// the favor held; empty when none.
    /// </summary>
    private static (string Title, double Favor) EmpireTitle(JsonObject observed, string pawn)
    {
        var favor = 0.0;
        foreach (var raw in observed["pawns"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (row["pawn"].AsMap()?["id"].AsString() != pawn)
            {
                continue;
            }

            foreach (var h in (row["royalty"].AsMap() ?? [])["holdings"].AsSlice())
            {
                var holding = h.AsMap() ?? [];
                var title = holding["title"].AsString();
                if (title.Length > 0)
                {
                    return (title, Num(holding["favor"]));
                }

                favor = Num(holding["favor"]);
            }
        }

        return ("", favor);
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var offer = prepared["questId"].AsString();
        var pawn = prepared["pawnId"].AsString();
        var title = prepared["title"].AsString();
        if (offer.Length == 0 || pawn.Length == 0 || title.Length == 0 || Num(prepared["favor"]) <= 0)
        {
            throw new AcceptanceException($"fixture: unexpected first-title staging: {prepared.ToJson()}");
        }

        if (Flag(prepared["needsThrone"]))
        {
            throw new AcceptanceException($"fixture: the first title {title} asks for a throne room, which this case does not stage: {prepared.ToJson()}");
        }

        report["fixture_quest"] = offer;
        report["pawn"] = pawn;
        report["title"] = title;

        var service = await QuestLifecycle.StartServiceAsync(s, cancellationToken).ConfigureAwait(false);
        Dictionary<string, JoinerAnswer> answers = [];
        using (var journal = await CaseJournal.OpenAsync(s.Config, cancellationToken).ConfigureAwait(false))
        {
            // The colony's own answers: the Empire offer, then the bestowing quest native generated for the favor (a
            // second quest accept), then the ceremony command once the bestower waits.
            var wait = new Wait { Ceiling = Ceiling, Stall = Wait.StallBudget(), Terminal = service.Exited };
            try
            {
                await wait.WaitProgressAsync(
                    async ct =>
                    {
                        (answers, _) = await QuestJoiner.AnswersAsync(journal, ct).ConfigureAwait(false);
                        foreach (var (quest, answer) in answers)
                        {
                            if (answer.Stage is "unsuccessful" or "cancelled")
                            {
                                throw new AcceptanceException($"the acceptance of quest {quest} ended {answer.Stage}");
                            }
                        }

                        var started = await RitualStartedAsync(journal, pawn, ct).ConfigureAwait(false);
                        report["answers"] = QuestJoiner.AnswersJson(answers);
                        var offerStage = answers.TryGetValue(offer, out var mine) ? mine.Stage : "";
                        return (Wait.Signature(answers.Count, offerStage, started), started);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException("ceremony command: " + e.Message, e);
            }
        }

        if (answers.Count < 2 || !answers.TryGetValue(offer, out var accepted) || accepted.Stage != "completed")
        {
            throw new AcceptanceException($"the ceremony started without the Empire offer and a bestowing quest both accepted: {QuestJoiner.AnswersJson(answers).ToJson()}");
        }

        report["run_keepalive"] = (await service.StopAsync().ConfigureAwait(false))?.DeepClone();

        // Native postcondition after the service releases the game slot: running the game on lets the bestowing ritual
        // finish, and the colonist holds the Empire title the fixture's favor was costed for.
        var h = s.Reattach();
        var held = "";
        var favor = 0.0;
        var elapsed = await RunAt.RunUntilAsync(
            h,
            "title-granted",
            Ticks,
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                var reply = await h.WireAsync(
                    "pawn-royalty-read",
                    PawnsReadWire,
                    new JsonObject
                    {
                        ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                        ["filter"] = new JsonObject { ["ids"] = new JsonArray(pawn) },
                        ["details"] = new JsonObject(),
                    },
                    ct).ConfigureAwait(false);
                var (_, observed) = Harness.Outcome(reply, "observed");
                (held, favor) = EmpireTitle(observed, pawn);
                return (Wait.Signature(held, favor), held.Length > 0);
            },
            quiet: true,
            report: report,
            cancellationToken).ConfigureAwait(false);
        report["title_ticks"] = elapsed;
        report["held_title"] = held;
        report["held_favor"] = favor;
        if (held != title)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"colonist {pawn} holds \"{held}\", want the first title \"{title}\""));
        }
    }
}
