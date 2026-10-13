using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/decree</c> (Go <c>decree.go</c>): real produce-item and harvest decree signal graphs, fulfilled by controller
/// bill and growing-zone orders, normal pawn production and harvesting, and native quest Success.
/// </summary>
internal static class QuestDecree
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "quest/decree",
        Scope = "Real produce-item and harvest decree signal graphs, fulfilled by controller bill and growing-zone orders, normal pawn production/harvesting, and native quest Success. A Go snapshot cannot prove vanilla Notify_ThingsProduced/Notify_PlantHarvested completion. Hunt and monument decrees excluded.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/quest_decree_prepare" },
        Crew = new Crew { Size = 6 },
        Expansions = ["ludeon.rimworld.royalty"],
        NoKeep = true,
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(10),
        RequiredOps = ["test/quest_decree_prepare", "test/quest_lifecycle_read"],
        Serve = new ServeSpec
        {
            Families = ["population-joiner", "field", "work", "bill", "supply", "dialog"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "quest-decree",
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var prepared = s.Prepared ?? [];
        var produce = prepared["produceQuest"].AsString();
        var harvest = prepared["harvestQuest"].AsString();
        if (produce.Length == 0 || harvest.Length == 0)
        {
            throw new AcceptanceException($"invalid decree fixture: {prepared.ToJson()}");
        }

        foreach (var id in new[] { produce, harvest })
        {
            var row = await QuestLifecycle.QuestAsync(s.Harness, s.Identity, id, cancellationToken).ConfigureAwait(false);
            if (row["state"].AsString() != "QUEST_STATUS_ONGOING")
            {
                throw new AcceptanceException($"decree must begin unfinished and ongoing: {row.ToJson()}");
            }

            foreach (var raw in row["objectives"].AsSlice())
            {
                var objective = raw.AsMap() ?? [];
                if (Num(objective["produced"]) > 0)
                {
                    throw new AcceptanceException($"fixture supplied objective progress: {objective.ToJson()}");
                }
            }
        }

        var service = await QuestLifecycle.StartServiceAsync(s, cancellationToken).ConfigureAwait(false);
        var bench = prepared["benchId"].AsString();
        using (var journal = await CaseJournal.OpenAsync(s.Config, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await QuestLifecycle.WaitOrdersAsync(
                    service,
                    journal,
                    plans =>
                    {
                        bool bill = false, zone = false;
                        foreach (var (kind, action) in CaseJournal.CompletedActions(plans))
                        {
                            bill = bill || (kind == Wire.Name(ActionKind.ProductionBill) && action["bench"].AsString() == bench);
                            zone = zone || kind == Wire.Name(ActionKind.ZoneCreate);
                        }

                        return bill && zone;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException("decree bill and growing-zone orders: " + e.Message, e);
            }
        }

        s.Report["run_keepalive"] = (await service.StopAsync().ConfigureAwait(false))?.DeepClone();
        await QuestLifecycle.FinishAsync(s, cancellationToken, produce, harvest).ConfigureAwait(false);
        var read = await s.Harness.CallAsync("decree-physical-proof", "test/quest_lifecycle_read", new JsonObject { ["questId"] = produce }, cancellationToken).ConfigureAwait(false);
        s.Report["native_work"] = read.DeepClone();
        if (Num(read["bills"]) < 1 || Num(read["growingZones"]) < 1 || Num(read["products"]) < 1)
        {
            throw new AcceptanceException($"native decree work missing: {read.ToJson()}");
        }
    }
}
