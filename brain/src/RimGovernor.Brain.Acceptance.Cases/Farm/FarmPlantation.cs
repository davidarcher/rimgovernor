using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>farm/plantation-rows</c> (Go <c>plantation.go</c>): the private <c>test/plantation_prepare</c> fixture sows an oak
/// (growth 0.8) and a rice plant (growth 0.9) in an oak growing zone. The colony read's acquisition census must list the
/// sown oak as a plantation row carrying its growth fraction, the fixture must report it designatable, and the rice crop
/// must never become a row.
/// </summary>
internal static class FarmPlantation
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "farm/plantation-rows",
        Scope = "Native plantation census: a sown oak in a growing zone is an acquisition row marked plantation with its growth fraction "
            + "and is designatable by chop acquisition; a rice plant in the same zone is not a row (#2292).",
        Start = new FixtureStart { Op = "test/plantation_prepare", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var prepared = s.Prepared ?? [];
        if (prepared["success"].AsBool() != true)
        {
            throw new AcceptanceException($"plantation fixture refused: {prepared.ToJson()}");
        }

        var oak = prepared["oak"].AsMap() ?? [];
        var rice = prepared["rice"].AsMap() ?? [];
        report["fixture"] = prepared;
        if (oak["eligible"].AsBool() != true)
        {
            throw new AcceptanceException($"sown oak in the growing zone is not chop-eligible: {oak.ToJson()}");
        }

        if (oak["designatorAccepts"].AsBool() != true)
        {
            throw new AcceptanceException($"the harvest-wood designator refuses the sown oak: {oak.ToJson()}");
        }

        if (rice["eligible"].AsBool() == true)
        {
            throw new AcceptanceException($"a rice plant in the growing zone became chop-eligible: {rice.ToJson()}");
        }

        var reply = await h.WireAsync(
            "plantation-facts",
            "observations_read_colony_facts",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() }, ["planning"] = true },
            cancellationToken).ConfigureAwait(false);
        var (_, facts) = Harness.Outcome(reply, "observed");
        JsonObject? oakRow = null;
        foreach (var raw in facts["acquisition"].AsSlice())
        {
            var row = raw.AsMap();
            var source = row?["source"].AsMap();
            var id = source?["id"].AsString();
            if (id == rice["id"].AsString())
            {
                throw new AcceptanceException($"a rice plant in the growing zone is an acquisition row: {row.ToJson()}");
            }

            if (id == oak["id"].AsString())
            {
                oakRow = row;
            }
        }

        report["oak_row"] = oakRow;
        if (oakRow is null)
        {
            throw new AcceptanceException($"the sown oak is not an acquisition row: {facts["acquisition"].ToJson()}");
        }

        if (oakRow["plantation"].AsBool() != true)
        {
            throw new AcceptanceException($"the oak row is not marked plantation: {oakRow.ToJson()}");
        }

        var growth = oakRow["growth"].AsNumber();
        if (growth < 0.79 || growth > 0.81)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the oak row growth {growth:F3} is not the fixture's 0.8: {oakRow.ToJson()}"));
        }
    }
}
