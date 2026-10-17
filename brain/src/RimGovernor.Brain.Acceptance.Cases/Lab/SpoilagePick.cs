using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/spoilage-pick</c>: test/spoilage_pick is an equality check the stage primitives cannot express (it spawns aged
/// stacks and toggles the preference around vanilla's own pick calls); the case reads which stack each pick chose.
/// </summary>
internal static class LabSpoilagePick
{
    private const string Tool = "test/spoilage_pick";

    private static readonly FixtureOp<NoArgs, Fx.SpoilagePickRead> Pick = ServedOps.Read<Fx.SpoilagePickRead>(Tool);

    internal static TypedCase Case() => new()
    {
        Name = "lab/spoilage-pick",
        Scope = "Spoilage preference (#2520): an old and a fresh stack of raw meat and of simple meals near a hungry colonist, "
            + "the fresh one closer and the old one outside vanilla's freshness bonus. With the preference off the real "
            + "bill-ingredient pick and meal pick both choose the closer fresh stack; with it on both choose the old stack; with "
            + "the old stack forbidden both still choose the fresh one. A unit test cannot see the native Harmony patches "
            + "reorder vanilla's candidates.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Ops = [Tool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var reply = await s.FixtureAsync("spoilage-pick", Pick, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("spoilage_pick", ServedOps.Json(reply));
        foreach (var (kind, fresh, old, pick) in new (string, string, string, Func<Fx.SpoilagePicks, string>)[]
        {
            ("bill", reply.FreshMeat, reply.OldMeat, p => p.Bill),
            ("meal", reply.FreshMeal, reply.OldMeal, p => p.Meal),
        })
        {
            foreach (var (name, picks, want) in new[] { ("off", reply.Off, fresh), ("on", reply.On, old), ("forbidden", reply.Forbidden, fresh) })
            {
                var got = pick(picks);
                if (got.Length == 0 || got != want)
                {
                    throw new AcceptanceException($"spoilage_pick: {kind} pick with preference {name} chose \"{got}\", want \"{want}\"");
                }
            }
        }
    }
}
