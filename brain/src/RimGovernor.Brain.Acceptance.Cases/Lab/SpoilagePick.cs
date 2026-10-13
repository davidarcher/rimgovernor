using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>lab/spoilage-pick</c> (Go <c>spoilage_pick.go</c>).</summary>
internal static class LabSpoilagePick
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "lab/spoilage-pick",
        Scope = "Spoilage preference (#2520): test/spoilage_pick spawns an old and a fresh stack of raw meat and of simple meals "
            + "near a hungry colonist, the fresh one closer and the old one outside vanilla's freshness bonus, and runs the real "
            + "bill-ingredient pick and meal pick. With the preference off both choose the closer fresh stack; with it on both "
            + "choose the old stack; with the old stack forbidden both still choose the fresh one. A Go snapshot test cannot see "
            + "the native Harmony patches reorder vanilla's candidates.",
        Start = new LabStart { Colonists = 1 },
        RequiredOps = ["test/spoilage_pick"],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var reply = await s.Harness.CallAsync("spoilage-pick", "test/spoilage_pick", null, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"spoilage_pick: {reply.ToJson()}");
        }

        s.Report["spoilage_pick"] = reply;
        foreach (var kind in new[] { "bill", "meal" })
        {
            var fresh = reply["fresh" + Capital(kind)].AsString();
            var old = reply["old" + Capital(kind)].AsString();
            foreach (var (name, want) in new[] { ("off", fresh), ("on", old), ("forbidden", fresh) })
            {
                var picks = reply[name].AsMap();
                var got = picks?[kind].AsString() ?? "";
                if (got != want)
                {
                    throw new AcceptanceException($"spoilage_pick: {kind} pick with preference {name} chose \"{got}\", want \"{want}\": {reply.ToJson()}");
                }
            }
        }
    }

    // Maps the pick kind to the reply's stack-id suffix: bill picks meat, meal picks meals.
    private static string Capital(string kind) => kind == "bill" ? "Meat" : "Meal";
}
