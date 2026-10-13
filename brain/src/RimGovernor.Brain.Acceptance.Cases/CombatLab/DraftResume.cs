using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>One combat_fights row as the resume case reads it: whether the fight is open and its drafted roster.</summary>
/// <param name="Open">Whether the fight is still open.</param>
/// <param name="Roster">The pawn ids the fight drafted.</param>
public sealed record CombatFightRow(bool Open, IReadOnlySet<string> Roster);

/// <summary>The brain's durable combat_fights table (Go <c>store.Store.OpenCombatFights</c> / <c>LoadCombatFight</c>).</summary>
public interface ICombatFightJournal
{
    /// <summary>The open fights by plan id.</summary>
    Task<IReadOnlyDictionary<string, CombatFightRow>> OpenCombatFightsAsync(CancellationToken cancellationToken);

    /// <summary>The fight of <paramref name="plan"/>, null when it has no row.</summary>
    Task<CombatFightRow?> LoadCombatFightAsync(string plan, CancellationToken cancellationToken);
}

/// <summary>
/// <c>combatlab/resume-drafted</c> (Go <c>draft_resume.go</c>). The case reads the brain's journal, whose location the
/// service/journal slice (#3085, #3087) owns: until it assigns <see cref="JournalOpener"/> the case fails with a message
/// naming the missing reader, as <see cref="ObservedSession.StoreOpener"/> does.
/// </summary>
public static class CombatLabResume
{
    // Bounds each wait of the resume case: the fight drafting its defenders, and the restarted controller reaching automate.
    private static readonly TimeSpan ResumeWait = TimeSpan.FromSeconds(120);

    /// <summary>Opens the journal of the run <paramref name="config"/> describes. Assigned with the brain's journal reader at the #3085 cutover.</summary>
    public static Func<Config, CancellationToken, Task<ICombatFightJournal>>? JournalOpener { get; set; }

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/resume-drafted",
        Scope = "Defenders stay drafted through a --resume restart (#916): lab-choke served by the routine defense planner under --resume until its fight drafts a roster, "
            + "the controller killed and restarted on the same journal; once the restarted controller is back in automate every standing defender is still on the fight's roster, and each reads drafted in the game.",
        Start = new LabStart { Colonists = 5 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool, CombatServe.PauseModeTool],
        QuietWorld = true,

        // A checkpoint capture pauses the served game mid-fight.
        NoCheckpoint = true,

        // Normal speed: the fight must outlast the kill-and-restart window.
        Serve = new ServeSpec { Families = CombatServe.MetricsFamilies, Resume = true, PlayerSpeed = "Normal", Prefix = "combatlab-resume" },
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var staged = await CombatStage.StageAsync(s.Harness, "lab-choke", cancellationToken).ConfigureAwait(false);
        if (staged.Fixture.Layout is { } layout)
        {
            _ = await CombatLabMetrics.StoreLayoutAsync(s.Harness, s.Identity, layout, cancellationToken).ConfigureAwait(false);
        }

        // A MajorThreat letter would pause the game as a player.
        await CombatServe.NeverPauseAsync(s.Harness, cancellationToken).ConfigureAwait(false);
        var open = JournalOpener
            ?? throw new AcceptanceException("no journal reader is wired for the in-game brain's combat_fights table (CombatLabResume.JournalOpener): the case cannot read the fight's drafted roster");
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        string plan;
        CombatFightRow before;
        try
        {
            service.KeepAuthority(cancellationToken);
            var journal = await open(s.Config, cancellationToken).ConfigureAwait(false);
            (plan, before) = await WaitDraftedAsync(journal, service, cancellationToken).ConfigureAwait(false);
            report["before"] = new JsonObject { ["plan"] = plan, ["roster"] = Strings(before.Roster) };
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        // Kill mid-fight and restart on the same journal. The game frees the GABP slot shortly after the kill, so the
        // restart may retry.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        ServiceProcess restarted;
        while (true)
        {
            try
            {
                restarted = await service.RestartAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (AcceptanceException e)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new AcceptanceException($"restarted controller never attached: {e.Message}", e);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }

        CombatFightRow? after;
        try
        {
            restarted.KeepAuthority(cancellationToken);
            await WaitAutomateAsync(restarted, cancellationToken).ConfigureAwait(false);
            var journal = await open(s.Config, cancellationToken).ConfigureAwait(false);
            after = await journal.LoadCombatFightAsync(plan, cancellationToken).ConfigureAwait(false);
            report["after"] = new JsonObject { ["open"] = after?.Open == true, ["roster"] = Strings(after?.Roster ?? new HashSet<string>(StringComparer.Ordinal)) };
        }
        finally
        {
            _ = await restarted.StopAsync().ConfigureAwait(false);
        }

        if (after is not { Open: true })
        {
            throw new AcceptanceException($"fight {plan} closed across the restart (row {(after is not null ? "true" : "false")}): the fight ended inside the restart window, nothing proven");
        }

        var h = s.Reattach();
        await CombatServe.PauseAfterAsync(h, cancellationToken).ConfigureAwait(false);
        var read = await LabOps.TickReadAsync(h, cancellationToken).ConfigureAwait(false);
        var checkedPawns = new List<string>();
        foreach (var pawn in before.Roster.Order(StringComparer.Ordinal))
        {
            var row = read.Pawn(pawn);
            if (row is null || row["downed"].AsBool() == true)
            {
                continue; // a downed or gone defender is undrafted on purpose
            }

            if (row["dead"].AsBool() == true)
            {
                continue;
            }

            if (!after.Roster.Contains(pawn))
            {
                throw new AcceptanceException($"defender {pawn} left the fight's roster across the restart: before [{string.Join(' ', before.Roster.Order(StringComparer.Ordinal))}], after [{string.Join(' ', after.Roster.Order(StringComparer.Ordinal))}]");
            }

            if (row["drafted"].AsBool() != true)
            {
                throw new AcceptanceException($"defender {pawn} undrafted across the restart: {row.ToJson()}");
            }

            checkedPawns.Add(pawn);
        }

        report["stayedDrafted"] = Strings(checkedPawns);
        if (checkedPawns.Count == 0)
        {
            throw new AcceptanceException($"no standing defender left to check after the restart: roster before [{string.Join(' ', before.Roster.Order(StringComparer.Ordinal))}]");
        }
    }

    // Waits for an open fight with a drafted roster (the combat_fights row keyed by plan_id is the fight's draft record).
    private static async Task<(string Plan, CombatFightRow Fight)> WaitDraftedAsync(ICombatFightJournal journal, ServiceProcess service, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ResumeWait;
        while (true)
        {
            if (service.Exited() is { } exited)
            {
                throw new AcceptanceException($"service exited before the fight drafted: {exited.Message}", exited);
            }

            var fights = await journal.OpenCombatFightsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (plan, fight) in fights.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (fight.Open && fight.Roster.Count > 0)
                {
                    return (plan, fight);
                }
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"no open fight drafted a roster within {ResumeWait}: {fights.Count} open fights"));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    // Waits for the restarted controller's state to read automate: its resume (ManualForResume, then Acquire) is done.
    private static async Task WaitAutomateAsync(ServiceProcess service, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ResumeWait;
        while (true)
        {
            if (service.Exited() is { } exited)
            {
                throw new AcceptanceException($"restarted service exited: {exited.Message}", exited);
            }

            try
            {
                var reply = await service.ApiAsync("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
                if (reply.Status == 200 && reply.Body?["mode"].AsString() == "automate")
                {
                    return;
                }
            }
            catch (AcceptanceException)
            {
                // The poll is a probe: the restarted controller may not answer yet.
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"restarted controller never reached automate within {ResumeWait}"));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private static JsonArray Strings(IEnumerable<string> values) =>
        new([.. values.Order(StringComparer.Ordinal).Select(v => (JsonNode?)JsonValue.Create(v))]);
}
