using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>anomaly/monolith-disrupt</c> (Go <c>anomaly/monolith_disrupt.go</c>) proves a colony awakens the void monolith,
/// activates the structures the quest spawns and disrupts at the void node.
/// </summary>
internal static class AnomalyMonolithDisrupt
{
    private const string PrepareTool = "test/monolith_awaken_prepare";
    private const string InspectTool = "test/monolith_awaken_inspect";

    // Bounds the journal wait in wall time (under the case budget); the stall budget ends it earlier when nothing moves.
    private static readonly TimeSpan MonolithCeiling = TimeSpan.FromMinutes(13);

    // The slack the staged weapons must clear the awaken gate by, so the case fails at once on a fixture that cannot
    // pass the gate.
    private const double ArmedMargin = 1.5;

    // The least the quest can offer: one void structure, the Gleaming monolith and the void node.
    private const int MinInteractTargets = 3;

    internal static AcceptanceCase Case() => new()
    {
        Name = "anomaly/monolith-disrupt",
        Scope = "Issue #2439: a colony with a void monolith at the Waking level, the codex entries the awakening needs and armed colonists "
            + "awakens the monolith (#2437, the awaken gate of policy/monolith.go), activates the void structures, uses the Gleaming monolith and "
            + "disrupts at the void node (#2438: the VoidNodeDisrupt dialog answer, never the embrace). The journal shows the awakening order, "
            + "the interactions and a dialog answered after the last of them; natively the monolith is at the Disrupted level, the questline has "
            + "ended and every colonist is alive. A Go snapshot over recorded facts cannot cover it: the awakening quest, its waves, the "
            + "structures, the pocket map the Gleaming monolith skips a colonist into and the node's dialog are vanilla's, asserted on the live "
            + "game; the rule and the dialog choice are policy/monolith_test.go and dialog_answer_test.go. The fixture sets the level and the "
            + "codex through the game's own GameComponent_Anomaly and EntityCodex (no entity, category, weapon or level literal is listed).",
        Start = new FixtureStart { Op = PrepareTool, On = new LabStart() },
        Expansions = ["ludeon.rimworld.anomaly"],
        NoKeep = true,
        Quiet = QuietMode.Required,
        RequiredOps = [PrepareTool, InspectTool],

        // population-custody carries the monolith advance, defense answers the waves, tend and rescue the casualties, the
        // shelter family keeps supervised windows running and dialog answers the confirmation, the letters and the node's
        // choice.
        Serve = new ServeSpec
        {
            Families = ["population-custody", "defense", "tend", "rescue", "shelter", "dialog"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "anomaly-monolith-disrupt",
        },
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>
    /// What the journal shows of the colony's answer: Awakened is a completed ActivateMonolith order (from Waking the only
    /// next level is the awakening), Interacts the targets of completed interact orders, Windows the dialog windows
    /// answered, completed.
    /// </summary>
    private sealed record Progressed(bool Awakened, HashSet<string> Interacts, HashSet<int> Windows);

    private static async Task<Progressed> JournalMonolithAsync(Journal journal, CancellationToken cancellationToken)
    {
        var awakened = false;
        var interacts = new HashSet<string>(StringComparer.Ordinal);
        var windows = new HashSet<int>();
        foreach (var plan in await ServiceJournal.PlanHistoryAsync(journal, "population-monolith-*", cancellationToken).ConfigureAwait(false))
        {
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.RecoveryService is not { } service || progress.View.Stage != Stage.Completed)
                {
                    continue;
                }

                switch (service.Method)
                {
                    case Domain.RecoveryMethod.ActivateMonolith:
                        awakened = true;
                        break;
                    case Domain.RecoveryMethod.Interact:
                        _ = interacts.Add(service.Thing);
                        break;
                    default:
                        break;
                }
            }
        }

        var review = await Task.Run(() => journal.LoadRounds(cancellationToken), cancellationToken).ConfigureAwait(false);
        var world = new World(review.Snapshot.Colony, review.Snapshot.Load, review.Snapshot.Map);
        var history = await Task.Run(() => journal.IncidentHistory(world, PolicyFunctions.AnswerDialog, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var incident in history)
        {
            foreach (var method in incident.Methods)
            {
                var plan = await Task.Run(() => journal.LoadPlan(method.Plan, cancellationToken), cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < plan.Spec.Actions.Count; i++)
                {
                    if (plan.Spec.Actions[i].DialogAnswer is { } answer && plan.Progress[i].View.Stage == Stage.Completed)
                    {
                        _ = windows.Add(answer.WindowId);
                    }
                }
            }
        }

        return new Progressed(awakened, interacts, windows);
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        var colonists = AnomalyHorror.IdList(prepared["colonistIds"]);
        var capacity = PolicyFunctions.DefensePointsPerDps * prepared["weaponDps"].AsNumber() * prepared["armed"].AsNumber();
        var need = PolicyFunctions.AwakenStrengthFactor * prepared["raidPoints"].AsNumber();
        if (prepared["monolithId"].AsString().Length == 0 || prepared["levelDef"].AsString() != "Waking" || colonists.Count == 0 || capacity < ArmedMargin * need)
        {
            throw new AcceptanceException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"fixture: unexpected monolith staging (staged defense {capacity:F0}, awakening gate {need:F0}): {prepared.ToJson()}"));
        }

        report["fixture"] = prepared.DeepClone();

        var service = await ObservedSession.Of(s).ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            var store = await ServiceJournal.OpenWithRetryAsync(s.Config, cancellationToken).ConfigureAwait(false);
            try
            {
                // The colony's answer is journalled: the awakening order, the interactions the quest offers (a structure,
                // the Gleaming monolith, the node) and a dialog answered after the latest interaction was ordered (the
                // node's choice opens when its interaction completes). The native outcome is read after the service
                // releases the game slot.
                HashSet<int> windowsAtLastInteract = [];
                var seenInteracts = 0;
                try
                {
                    await new Wait { Ceiling = MonolithCeiling, Stall = Wait.StallBudget(), Terminal = service.Exited }.WaitProgressAsync(
                        async ct =>
                        {
                            var got = await JournalMonolithAsync(store, ct).ConfigureAwait(false);
                            if (got.Interacts.Count != seenInteracts)
                            {
                                seenInteracts = got.Interacts.Count;
                                windowsAtLastInteract = [.. got.Windows];
                            }

                            var answeredAfter = got.Windows.Any(w => !windowsAtLastInteract.Contains(w));
                            var done = got.Awakened && got.Interacts.Count >= MinInteractTargets && answeredAfter;
                            report["awakened"] = got.Awakened;
                            report["interact_targets"] = got.Interacts.Count;
                            report["dialog_windows"] = got.Windows.Count;
                            return (Wait.Signature(got.Awakened, got.Interacts.Count, got.Windows.Count), done);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"awakening and disrupt: {e.Message}", e);
                }
            }
            finally
            {
                store.Dispose();
            }

            report["run_keepalive"] = (await service.StopAsync().ConfigureAwait(false))?.DeepClone();
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        // Native postconditions: the monolith is disrupted (never embraced), the questline has ended and every colonist
        // is alive.
        var h = s.Reattach();
        var reply = await h.CallAsync("monolith-inspect", InspectTool, new JsonObject { ["colonistIds"] = string.Join(',', colonists) }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{InspectTool} refused: {reply.ToJson()}");
        }

        report["inspect"] = reply.DeepClone();
        if (reply["embraced"].AsBool() == true)
        {
            throw new AcceptanceException($"the monolith was embraced: {reply.ToJson()}");
        }

        if (reply["disrupted"].AsBool() != true)
        {
            throw new AcceptanceException($"the monolith is not disrupted (level {reply["level"].ToJson()} {reply["levelDef"].ToJson()}, highest {reply["highestLevelReached"].ToJson()}): {reply.ToJson()}");
        }

        if (reply["questlineEnded"].AsBool() != true)
        {
            throw new AcceptanceException($"the monolith is disrupted but the questline has not ended: {reply.ToJson()}");
        }

        foreach (var raw in reply["colonists"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (row["found"].AsBool() != true)
            {
                throw new AcceptanceException($"colonist {row["id"].ToJson()} is gone: {row.ToJson()}");
            }

            if (row["dead"].AsBool() == true)
            {
                throw new AcceptanceException($"colonist {row["id"].ToJson()} died in the awakening: {row.ToJson()}");
            }
        }
    }
}
