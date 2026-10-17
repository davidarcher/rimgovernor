using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>medical/surgery-facts</c> (Go <c>surgeryfacts.go</c>).</summary>
internal static class SurgeryFactsCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "medical/surgery-facts",
        Scope = "list_pawns' health detail carries the surgery facts (#1161): a colonist missing a leg with a "
            + "prosthetic in stock reports the missing part and a restore operation on it with a vanilla "
            + "success_chance > 0. A native read contract: the recipe discovery and SurgeryOutcomeEffectDef "
            + "quality are vanilla code a Go snapshot cannot exercise.",
        Start = new FixtureStart
        {
            Op = "test/medical_management_setup",
            On = new LabStart(),
            Args = new JsonObject { ["disease"] = false },
        },
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var prepared = s.Prepared ?? [];
        var patient = prepared["surgical"].AsString();
        var part = prepared["part"].Strict();
        if (patient.Length == 0 || part is null)
        {
            throw new AcceptanceException($"medical_management_setup reply lacks surgical/part: {prepared.ToJson()}");
        }

        var h = s.Harness;
        var identity = await Session.ReadIdentityAsync(h, "surgery-facts-identity", cancellationToken).ConfigureAwait(false);
        var observed = await MedicalKit.ListPawnsAsync(h, "surgery-facts-readback", identity, MedicalKit.ColonistFilter(), MedicalKit.Details(health: true), cancellationToken).ConfigureAwait(false);
        foreach (var row in observed["pawns"].Rows())
        {
            if (row["pawn"].RefId() != patient)
            {
                continue;
            }

            var health = row["health"].AsMap() ?? [];
            s.Report["surgery_health"] = new JsonObject { ["missingParts"] = MedicalKit.Surgery(health)["missingParts"].Copy(), ["operations"] = MedicalKit.Surgery(health)["operations"].Copy() };
            Observe(health, (int)part.Value);
            return;
        }

        throw new AcceptanceException($"surgical patient {patient} not in the pawn read");
    }

    // Requires the missing leg and a restore operation on it with a positive vanilla success chance.
    private static void Observe(JsonObject health, int part)
    {
        if (!MedicalKit.PartMissing(health, part))
        {
            throw new AcceptanceException($"missing leg {part} not reported: {MedicalKit.Surgery(health)["missingParts"].ToText()}");
        }

        foreach (var op in MedicalKit.Surgery(health)["operations"].Rows())
        {
            if (op["partIndex"].Strict() is { } index && (int)index == part && op["kind"].AsString() == "SURGERY_KIND_RESTORE" && op["successChance"].Strict() is > 0)
            {
                return;
            }
        }

        throw new AcceptanceException($"no restore operation with success_chance > 0 on part {part}: {MedicalKit.Surgery(health)["operations"].ToText()}");
    }
}
