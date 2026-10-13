using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/hospitality</c> (Go <c>hospitality.go</c>): a real Hospitality_Joiners offer with a titled lodger, native
/// bedroom requirement, hosted guest bed assignment, pickup boarding and vanilla Success through world progression.
/// </summary>
internal static class QuestHospitality
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "quest/hospitality",
        Scope = "Real Hospitality_Joiners offer with a titled lodger: native bedroom requirement, hosted guest bed assignment, pickup boarding and vanilla Success through world progression. A Go snapshot cannot prove arrival, bed ownership, transporter boarding or the vanilla end signal.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/quest_hospitality_prepare" },
        Crew = new Crew { Size = 6 },
        Expansions = ["ludeon.rimworld.royalty"],
        NoKeep = true,
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(12),
        RequiredOps = ["test/quest_hospitality_prepare", "test/quest_lifecycle_read"],
        Serve = new ServeSpec
        {
            Families = ["population-joiner", "sleeping", "shelter", "dialog"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "quest-hospitality",
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var prepared = s.Prepared ?? [];
        var id = prepared["questId"].AsString();
        var pawn = prepared["pawnId"].AsString();
        var bed = prepared["bedId"].AsString();
        if (id.Length == 0 || pawn.Length == 0 || bed.Length == 0 || prepared["title"].AsString().Length == 0)
        {
            throw new AcceptanceException($"invalid hospitality fixture: {prepared.ToJson()}");
        }

        var before = await QuestLifecycle.QuestAsync(s.Harness, s.Identity, id, cancellationToken).ConfigureAwait(false);
        if (!Flag(before["canAccept"]) || before["state"].AsString() != "QUEST_STATUS_NOT_YET_ACCEPTED" || before["scriptDef"].AsString() != "Hospitality_Joiners")
        {
            throw new AcceptanceException($"hospitality precondition: {before.ToJson()}");
        }

        var service = await QuestLifecycle.StartServiceAsync(s, cancellationToken).ConfigureAwait(false);
        using (var journal = await CaseJournal.OpenAsync(s.Config, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await QuestLifecycle.WaitOrdersAsync(
                    service,
                    journal,
                    plans =>
                    {
                        bool assigned = false, launched = false;
                        foreach (var (kind, action) in CaseJournal.CompletedActions(plans))
                        {
                            assigned = assigned || (kind == Wire.Name(ActionKind.Assign) && action["pawn"].AsString() == pawn && action["thing"].AsString() == bed);
                            launched = launched || (kind == Wire.Name(ActionKind.QuestShuttle) && action["quest"].AsString() == id && Flag(action["launch"]));
                        }

                        return assigned && launched;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException("guest bed and pickup orders: " + e.Message, e);
            }
        }

        s.Report["run_keepalive"] = (await service.StopAsync().ConfigureAwait(false))?.DeepClone();
        await QuestLifecycle.FinishAsync(s, cancellationToken, id).ConfigureAwait(false);
        var read = await s.Harness.CallAsync("hospitality-physical-proof", "test/quest_lifecycle_read", new JsonObject { ["questId"] = id }, cancellationToken).ConfigureAwait(false);
        s.Report["native_hosting"] = read.DeepClone();
        foreach (var key in new[] { "hosted", "bedAssigned", "boarded" })
        {
            if (!Flag(read[key]))
            {
                throw new AcceptanceException($"native hospitality {key} not observed: {read.ToJson()}");
            }
        }
    }
}
