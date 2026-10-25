using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>medical/plague-readback</c>: list_pawns' health detail carries a disease's native progression. test/medical_plague_prepare
/// gives two colonists Plague at severity 0.2 and immunity 0.1 with the rolled progression factor pinned, the second tended
/// at quality 0.75, so the read's severity per day is exact: 0.666 untended, less the tended reduction (0.3628 times the
/// quality) for the second.
/// </summary>
internal static class PlagueReadback
{
    internal const string PrepareTool = "test/medical_plague_prepare";

    private static readonly FixtureOp<NoArgs, Fx.PlagueSetup> Prepare = ServedOps.Read<Fx.PlagueSetup>(PrepareTool);

    internal static TypedCase Case() => new()
    {
        Name = "medical/plague-readback",
        Scope = "The health detail reads each Plague patient's severity, immunity, exact severity per day, a positive immunity per day, "
            + "and tended presence and quality: untended for the first patient, tended at 0.75 for the second.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Ops = [PrepareTool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var setup = await s.FixtureAsync("plague-prepare", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        if (!setup.Success || setup.Patients.Count != 2)
        {
            throw new AcceptanceException($"plague-prepare: want two patients, got {ServedOps.Json(setup)}");
        }

        var rows = await TypedPawns.ListAsync(s, "plague-readback", new RimGovernor.Brain.GameFacts.Observations.PawnFilter { Ids = [.. setup.Patients] }, cancellationToken).ConfigureAwait(false);
        var problems = new List<string>();
        for (var index = 0; index < 2; index++)
        {
            var id = setup.Patients[index];
            var plague = rows.SingleOrDefault(r => r.Pawn.Id == id)?.Health.Hediffs.SingleOrDefault(h => h.DefName == "Plague");
            if (plague is null)
            {
                problems.Add($"{id}: no single Plague condition read");
                continue;
            }

            s.RecordWire("plague_" + CaseSupport.Tag(index), plague);
            foreach (var (field, got, want) in new (string, double?, double)[]
            {
                ("severity", plague.Severity, .2),
                ("immunity", plague.Immunizable?.Immunity, .1),
                ("severityPerDay", plague.Immunizable?.SeverityPerDay, .666 - (index * .3628 * .75)),
            })
            {
                if (got is not { } value || Math.Abs(value - want) > .0001)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{id} {field} = {got}, want {want:R}"));
                }
            }

            if (plague.Immunizable?.Gain is not RimGovernor.Brain.GameFacts.Observations.ImmunityGain.PerDay { Value: >= .01 and <= 2 })
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{id} immunity gain = {plague.Immunizable?.Gain}"));
            }

            var tended = index == 1;
            if (tended ? plague.Tend is not RimGovernor.Brain.GameFacts.Observations.HediffTend.Tended { Quality: var q } || Math.Abs(q - .75) > .0001 : plague.Tend is not RimGovernor.Brain.GameFacts.Observations.HediffTend.Untended)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{id} tend = {plague.Tend}, want tended = {tended}"));
            }
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}
