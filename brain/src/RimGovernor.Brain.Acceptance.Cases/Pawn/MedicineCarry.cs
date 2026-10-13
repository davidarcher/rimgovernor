using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/medicine-carry</c> (Go <c>medicine_carry.go</c>).</summary>
internal static class PawnMedicineCarry
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/medicine-carry",
        Scope = "A fixture doctor on NormalOrWorse care carrying no medicine, beside ample stock, is given a positive Medicine carry count by EnsureWorkAssignments through a PawnSettingsIntent (#1307), and the native policy-inputs read shows it. Native contract: the medicine_carry write and the inventory-stock read; the counts are policy/medicine_carry_test.go.",
        Start = new FixtureStart { Op = "test/doctor_medicine", On = new LabStart() },
        Serve = PawnSupport.WorkSpec,
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>The desired Medicine carry count in a pawn row's <c>settings.policyInputs.inventoryStock</c>.</summary>
    private static double Carry(JsonObject row)
    {
        var count = 0.0;
        foreach (var item in row["settings"].Member("policyInputs").Member("inventoryStock").AsSlice())
        {
            var stock = item.AsMap() ?? [];
            if (stock["group"].AsString() == "Medicine")
            {
                count = PawnSupport.Count(stock["count"]);
            }
        }

        return count;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var pawnId = PawnSupport.Fixture(s, "pawn", "pawn");
        s.Report["pawn"] = pawnId;
        var h = s.Harness;
        await PawnSupport.SettleAsync(
            s,
            async ct =>
            {
                var row = await PawnSupport.ReadPawnAsync(h, s, "medicine-carry-wait", pawnId, true, ct).ConfigureAwait(false);
                return Carry(row) > 0 ? ("carry applied", true) : ("waiting for the medicine carry setting", false);
            },
            cancellationToken).ConfigureAwait(false);
        var final = await PawnSupport.ReadPawnAsync(s.Harness, s, "medicine-carry", pawnId, true, cancellationToken).ConfigureAwait(false);
        var count = Carry(final);
        s.Report["medicine_carry"] = count;
        if (count < 1)
        {
            throw new AcceptanceException($"medicine carry readback {PawnSupport.Text(count)}, want at least 1");
        }
    }
}
