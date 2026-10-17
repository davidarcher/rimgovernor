using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>medical/plague-readback</c> (Go <c>plague.go</c>).</summary>
internal static class PlagueReadback
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "medical/plague-readback",
        Scope = "Routine colonist health carries Plague severity and immunity per day, untended/tended presence and quality; colony facts retain envelope headroom.",
        Start = new FixtureStart { Op = "test/medical_plague_prepare", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(1),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var patients = (s.Prepared ?? [])["patients"].AsSlice();
        if (patients.Count != 2)
        {
            throw new AcceptanceException("expected two Plague patients");
        }

        var identity = await Session.ReadIdentityAsync(h, "plague-identity", cancellationToken).ConfigureAwait(false);
        var observed = await MedicalKit.ListPawnsAsync(h, "plague-readback", identity, MedicalKit.ColonistFilter(), MedicalKit.Details(health: true), cancellationToken).ConfigureAwait(false);
        var found = 0;
        foreach (var pawn in observed["pawns"].Rows())
        {
            var id = pawn["pawn"].RefId();
            var index = -1;
            for (var i = 0; i < patients.Count; i++)
            {
                if (id == patients[i].AsString())
                {
                    index = i;
                }
            }

            if (index < 0)
            {
                continue;
            }

            foreach (var condition in pawn["health"].AsMap()?["hediffs"].Rows() ?? [])
            {
                if (condition["defName"].AsString() != "Plague")
                {
                    continue;
                }

                found++;
                foreach (var (field, want) in new (string, double)[] { ("severity", .2), ("immunity", .1), ("severityPerDay", .666 - (index * .3628 * .75)) })
                {
                    var got = condition[field].Strict();
                    if (got is null || Math.Abs(got.Value - want) > .0001)
                    {
                        throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{id} {field} = {condition[field].ToText()}, want {want:R}"));
                    }
                }

                var rate = condition["immunityPerDay"].Strict();
                if (rate is null || rate < .01 || rate > 2)
                {
                    throw new AcceptanceException($"{id} immunity/day = {condition["immunityPerDay"].ToText()}");
                }

                // The writer omits false, so an absent flag is untended.
                var tended = condition["tended"].AsBool() == true;
                if (tended != (index == 1))
                {
                    throw new AcceptanceException($"{id} tended = {condition["tended"].ToText()}");
                }

                var quality = condition["tendQuality"].Strict();
                if ((index == 0 && quality is not null) || (index == 1 && (quality is null || Math.Abs(quality.Value - .75) > .0001)))
                {
                    throw new AcceptanceException($"{id} tend quality = {condition["tendQuality"].ToText()}");
                }
            }
        }

        if (found != 2)
        {
            throw new AcceptanceException($"read {found} Plague conditions, want 2");
        }

        s.Report["plague_readback"] = observed.DeepClone();
    }
}
