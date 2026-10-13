using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>anomaly/hold-study</c> (Go <c>anomaly/hold_study.go</c>) proves a colony captures a downed entity into its
/// containment cell, holds it and studies it.
/// </summary>
internal static class AnomalyHoldStudy
{
    private const string PrepareTool = "test/entity_hold_prepare";
    private const string InspectTool = "test/entity_hold_inspect";

    // Bounds the journal wait in wall time; the stall budget ends it earlier when nothing moves.
    private static readonly TimeSpan HoldCeiling = TimeSpan.FromMinutes(8);

    // Bounds the native run after the service stops: the carried entity is chained to the platform and a colonist walks
    // to it and works one DarkStudy interaction (a few thousand ticks).
    private const ulong StudyTicks = 3 * Wait.TicksPerDay;

    internal static AcceptanceCase Case() => new()
    {
        Name = "anomaly/hold-study",
        Scope = "Issue #1747: a colony with a finished containment cell captures a downed, capturable entity, holds it on the "
            + "platform and studies it: the capture rule (#1742) takes the entity to the platform, the work planner owes a DarkStudy "
            + "owner while the held entity is currently studiable (#1744), and natively the entity is alive, chained on the platform "
            + "and has had a study interaction worked on it. A Go snapshot over recorded facts cannot cover it: containment strength, "
            + "the carry to the platform, the chains and the study job and its interval are vanilla's, asserted on the live pawn "
            + "and its CompStudiable; the rule and the planner's decisions are policy/entity_capture_test.go and entity_study_test.go. "
            + "The fixture picks the entity and the platform from the game's defs and builds the cell finished (its planning is "
            + "#1741's, policy tests).",
        Start = new FixtureStart { Op = PrepareTool, On = new LabStart() },
        Expansions = ["ludeon.rimworld.anomaly"],
        NoKeep = true,
        Quiet = QuietMode.Required,
        RequiredOps = [PrepareTool, InspectTool],

        // population-custody captures and keeps the held entity, work owes the DarkStudy owner and tend answers a
        // captive's wounds; the shelter family keeps supervised windows running and dialog answers letters.
        Serve = new ServeSpec
        {
            Families = ["population-custody", "work", "tend", "shelter", "dialog"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "anomaly-hold-study",
        },
        Budget = TimeSpan.FromMinutes(12),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>
    /// What the journal shows of the colony's answer: Captured is a completed Capture of the entity, StudyOwner a completed
    /// work assignment that gives a colonist a DarkStudy priority.
    /// </summary>
    private static async Task<(bool Captured, bool StudyOwner)> JournalHoldAsync(Journal journal, string entity, CancellationToken cancellationToken)
    {
        var captured = false;
        var studyOwner = false;
        foreach (var plan in await ServiceJournal.PlanHistoryAsync(journal, "population-capture-*", cancellationToken).ConfigureAwait(false))
        {
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.Capture is { } capture && capture.Patient.Value == entity && progress.View.Stage == Stage.Completed)
                {
                    captured = true;
                }
            }
        }

        var darkStudy = Wire.Name(WorkType.DarkStudy);
        foreach (var plan in await ServiceJournal.PlanHistoryAsync(journal, "work-*", cancellationToken).ConfigureAwait(false))
        {
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.WorkAssignment is not { } work || progress.View.Stage != Stage.Completed)
                {
                    continue;
                }

                foreach (var setting in work.Settings)
                {
                    if (setting.Definition == darkStudy && setting.Priority > 0)
                    {
                        studyOwner = true;
                    }
                }
            }
        }

        return (captured, studyOwner);
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        var entity = prepared["entityId"].AsString();
        var platform = prepared["platformId"].AsString();
        if (entity.Length == 0 || platform.Length == 0 || prepared["platformStrength"].AsNumber() <= prepared["need"].AsNumber())
        {
            throw new AcceptanceException($"fixture: unexpected entity hold staging: {prepared.ToJson()}");
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
                // The colony's answer is journalled: the capture order for the entity, then the DarkStudy owner the work
                // planner owes once the entity is held and currently studiable. The native outcome is read after the
                // service releases the game slot.
                try
                {
                    await new Wait { Ceiling = HoldCeiling, Stall = Wait.StallBudget(), Terminal = service.Exited }.WaitProgressAsync(
                        async ct =>
                        {
                            var (captured, studyOwner) = await JournalHoldAsync(store, entity, ct).ConfigureAwait(false);
                            report["captured"] = captured;
                            report["study_owner"] = studyOwner;
                            return (Wait.Signature(captured, studyOwner), captured && studyOwner);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"capture and study owner: {e.Message}", e);
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

        // Native postconditions: the entity is alive and chained on the platform and a colonist has worked a study
        // interaction on it.
        var h = s.Reattach();
        JsonObject? last = null;
        ulong elapsed = 0;
        try
        {
            elapsed = await RunAt.RunUntilAsync(
                h,
                "entity-studied",
                StudyTicks,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var reply = await h.CallAsync("hold-inspect", InspectTool, new JsonObject { ["entityId"] = entity, ["platformId"] = platform }, ct).ConfigureAwait(false);
                    if (reply["success"].AsBool() != true)
                    {
                        throw new AcceptanceException($"{InspectTool} refused: {reply.ToJson()}");
                    }

                    last = reply;
                    if (reply["found"].AsBool() != true)
                    {
                        throw new AcceptanceException($"the entity {entity} is gone: {reply.ToJson()}");
                    }

                    if (reply["dead"].AsBool() == true)
                    {
                        throw new AcceptanceException($"the entity {entity} died: {reply.ToJson()}");
                    }

                    var held = reply["held"].AsBool() == true;
                    var interactions = reply["studyInteractions"].AsNumber();
                    return (Wait.Signature(held, interactions), held && interactions >= 1);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            report["study_ticks"] = elapsed;
            report["inspect"] = last?.DeepClone();
            throw new AcceptanceException($"study: {e.Message} (last read {last?.ToJson() ?? "null"})", e);
        }

        report["study_ticks"] = elapsed;
        report["inspect"] = last?.DeepClone();
        if (last!["onPlatform"].AsBool() != true)
        {
            throw new AcceptanceException($"the entity {entity} is held but not on the staged platform {platform}: {last.ToJson()}");
        }

        if (last["escaping"].AsBool() == true)
        {
            throw new AcceptanceException($"the entity {entity} is escaping: {last.ToJson()}");
        }
    }
}
