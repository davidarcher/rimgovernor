using System.Text.Json.Nodes;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/expedition</c> and <c>quest/bandit-camp</c> (Go <c>expedition.go</c>): a real adjacent bandit quest round
/// trip, accept or follow, staff, form or board, clear the generated site and return living travelers with identifiable
/// loot. The bandit-camp variant travels by a Royalty shuttle mission.
/// </summary>
internal static class QuestExpedition
{
    private static readonly string[] RoundTripFields = ["arrived", "cleared", "returned", "lootReturned"];

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        foreach (var mission in new[] { false, true })
        {
            yield return new AcceptanceCase
            {
                Name = mission ? "quest/bandit-camp" : "quest/expedition",
                Scope = "Real adjacent bandit quest round trip: accept or follow, staff, form/board, clear the generated site and return living travelers with identifiable loot. The fixture stages 30 silver on the site when its native map first generates. Go snapshots cannot prove transport, vanilla combat quest signals, physical home arrival or loot extraction.",
                Start = new FixtureStart { On = new LabStart(), Op = "test/quest_expedition_prepare", Args = new JsonObject { ["shuttleMission"] = mission } },
                Crew = new Crew { Size = 6 },
                Expansions = mission ? ["ludeon.rimworld.royalty"] : [],
                NoKeep = true,
                Quiet = QuietMode.Required,
                Budget = TimeSpan.FromMinutes(12),
                RequiredOps = ["test/quest_expedition_prepare", "test/quest_expedition_read"],
                Serve = new ServeSpec
                {
                    Families = ["population-joiner", "defense", "work", "supply", "sleeping", "dialog"],
                    NativeTimeout = TimeSpan.FromSeconds(15),
                    Prefix = "quest-expedition",
                },
                Run = RunAsync,
            };
        }
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var prepared = s.Prepared ?? [];
        var id = prepared["questId"].AsString();
        var mission = Flag(prepared["shuttleMission"]);
        if (id.Length == 0 || prepared["siteId"].AsString().Length == 0)
        {
            throw new AcceptanceException($"invalid expedition fixture: {prepared.ToJson()}");
        }

        var service = await QuestLifecycle.StartServiceAsync(s, cancellationToken).ConfigureAwait(false);

        // Keep the governor attached until the original native roster leaves and returns. The final witness also
        // proves site combat, transport and loot.
        var left = false;
        async Task<ulong> Tick(CancellationToken ct) =>
            (ulong)Num((await service.GetAsync("GET", "/api/player/colony", ct).ConfigureAwait(false))["tick"]);

        var wait = new Wait
        {
            Ceiling = TimeSpan.FromMinutes(8),
            Stall = Wait.StallBudget(),
            Terminal = service.Exited,
            Ticks = 30000,
            Tick = Tick,
        };
        try
        {
            await wait.WaitProgressAsync(
                async ct =>
                {
                    var row = await service.GetAsync("GET", "/api/player/colony", ct).ConfigureAwait(false);
                    var present = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var raw in row["pawns"].AsSlice())
                    {
                        _ = present.Add(raw.AsMap()?["id"].AsString() ?? "");
                    }

                    var allHome = prepared["pawnIds"].AsSlice().All(raw => present.Contains(raw.AsString()));
                    left = left || !allHome;
                    return (Wait.Signature(row["tick"]?.ToJsonString(), row["pawns"]?.ToJsonString()), left && allHome);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("expedition outbound and return lifecycle: " + e.Message, e);
        }

        s.Report["run_keepalive"] = (await service.StopAsync().ConfigureAwait(false))?.DeepClone();
        var h = s.Reattach();
        var elapsed = await RunAt.RunUntilAsync(
            h,
            "quest-expedition-physical-round-trip",
            30000,
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                var read = await h.CallAsync("expedition-physical-proof", "test/quest_expedition_read", new JsonObject { ["questId"] = id }, ct).ConfigureAwait(false);
                s.Report["native_expedition"] = read.DeepClone();
                var signature = Wait.Signature(read.ToJsonString());
                foreach (var field in RoundTripFields)
                {
                    if (!Flag(read[field]))
                    {
                        return (signature, false);
                    }
                }

                if (!Flag(read[mission ? "boarded" : "formed"]))
                {
                    return (signature, false);
                }

                return (signature, read["state"].AsString() == "EndedSuccess" && Num(read["loot"]) >= 30);
            },
            quiet: true,
            report: s.Report,
            cancellationToken).ConfigureAwait(false);
        s.Report["native_round_trip_ticks"] = elapsed;
    }
}
