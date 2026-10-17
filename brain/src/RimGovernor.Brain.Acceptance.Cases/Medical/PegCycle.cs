using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>medical/peg-training</c> (Go <c>pegcycle.go</c>).</summary>
internal static class PegCycle
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "medical/peg-training",
        Scope = "Peg-leg doctor training (#1236): with Medicine 8 doctors (below the floor of 10) and an unrecruitable "
            + "factionless prisoner missing a leg, medical/surgery queues InstallPegLeg on the prisoner, native doctors "
            + "install it, it queues RemoveBodyPart on the peg, and a doctor's Medicine level rises. "
            + "Native: vanilla surgery XP and the wood-part install and removal completing are no Go snapshot.",
        Start = new FixtureStart
        {
            Op = "test/medical_management_setup",
            On = new LabStart(),
            Args = new JsonObject { ["disease"] = false, ["condition"] = "pegTraining" },
        },
        Service = true,
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        var prisoner = prepared["prisonerId"].AsString();
        var index = prepared["conditionPart"].Strict();
        if (prisoner.Length == 0 || index is null || index < 0)
        {
            throw new AcceptanceException($"medical_management_setup reply lacks prisonerId/conditionPart: {prepared.ToJson()}");
        }

        var leg = (int)index.Value;
        var before = await MedicineXpAsync(s, "peg-training-xp-before", cancellationToken).ConfigureAwait(false);

        // A queued bill is clock work: one service queues the install, runs it, queues the removal on the peg and runs that.
        var run = await SurgeryRun.StartAsync(s, "peg-training", cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var recipe in new[] { "InstallPegLeg", "RemoveBodyPart" })
            {
                try
                {
                    await run.UntilAsync(
                        TimeSpan.FromMinutes(6),
                        (surgeries, _) => Task.FromResult((Wait.Signature(surgeries.Count), SurgeryRun.Find(surgeries, prisoner, leg, recipe) is not null)),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"peg-training {recipe}: {e.Message}", e);
                }

                report[recipe] = true;
            }

            await run.ServedAsync("peg-training-remove", prisoner, health => MedicalKit.PartMissing(health, leg), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await run.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        var after = await MedicineXpAsync(s, "peg-training-xp-after", cancellationToken).ConfigureAwait(false);
        report["medicine_xp"] = new JsonObject { ["before"] = before, ["after"] = after };
        if (after <= before)
        {
            throw new AcceptanceException($"doctors' Medicine did not rise over the cycle: {NativeReads.Fmt(before)} -> {NativeReads.Fmt(after)}");
        }
    }

    // Sums the free colonists' Medicine levels. The read carries no XP, so the fixture starts each doctor 1000 XP short
    // of level 9 with a major passion: the cycle's 5600 XP (x1.5) crosses it.
    private static async Task<double> MedicineXpAsync(ISession s, string label, CancellationToken cancellationToken)
    {
        var observed = await MedicalKit.ListPawnsAsync(s.Harness, label, s.Identity, MedicalKit.HumanFilter(), MedicalKit.Details(health: false, biography: true), cancellationToken).ConfigureAwait(false);
        double total = 0;
        var seen = false;
        foreach (var row in observed["pawns"].Rows())
        {
            if (row["freeColonist"].AsBool() != true)
            {
                continue;
            }

            foreach (var skill in row["biography"].AsMap()?["skills"].Rows() ?? [])
            {
                if (skill["defName"].AsString() == "Medicine")
                {
                    // A level-0 skill omits its level from the wire.
                    total += skill["level"] is null ? 0 : skill["level"].AsNumber();
                    seen = true;
                }
            }
        }

        if (!seen)
        {
            throw new AcceptanceException($"{label}: no Medicine skill in the pawn read");
        }

        return total;
    }
}
