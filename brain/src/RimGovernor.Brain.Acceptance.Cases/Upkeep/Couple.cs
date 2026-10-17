using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>upkeep/couple</c>: test/sleeping_setup(couple) makes the first two colonists bedless lovers beside one vacant double
/// bed. Under the served sleeping family the native game assigns both to it, so neither sleeps alone. The fixture stays
/// bespoke: the stage primitives cannot add a relation or claim beds.
/// </summary>
internal static class UpkeepCouple
{
    private const string Setup = "test/sleeping_setup";

    private static readonly FixtureOp<NoArgs, Fx.SleepingPrepared> Prepared = new(
        Setup,
        _ => ServedOps.Args(("couple", true)),
        (label, reply) => ServedOps.Reply<Fx.SleepingPrepared>(Setup)(label, reply) is { Success: true, Partner.Length: > 0, DoubleBed.Length: > 0 } prepared
            ? prepared
            : throw new AcceptanceException($"{label}: {Setup} returned no couple: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "upkeep/couple",
        Scope = "Couples share a bedroom (#812): under the served sleeping family two bedless lovers both come to own one vacant "
            + "double bed, read back from the native bed owners. Only the live assignment through the game proves ownership lands for both.",
        Ticks = 6000,
        Tier = CaseTier.OnDemand,
        Ops = [Setup],
        Keep = ["Rest"],
        Serve = new ServeSpec { Families = ["sleeping", "work"], Prefix = "upkeep-couple" },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("sleeping-setup", Prepared, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("prepared", ServedOps.Json(prepared));
        var couple = new HashSet<string>(StringComparer.Ordinal) { prepared.Pawn, prepared.Partner! };
        _ = await s.ServeUntilAsync("couple", s.TicksLeft, async ct =>
        {
            var reply = await s.CallAsync("beds", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = false }, ct).ConfigureAwait(false);
            var facts = TypedReads.Observed("beds", NativeOps.ReadColonyFacts, reply, reply.Outcome?.Observed);
            var upkeep = facts.Upkeep?.Outcome ?? throw new AcceptanceException("beds: upkeep facts unavailable");
            var row = upkeep.Beds.FirstOrDefault(b => b.Bed.Id == prepared.DoubleBed)
                ?? throw new AcceptanceException($"double bed {prepared.DoubleBed} missing from the bed facts");
            var owners = row.Owners.Select(o => o.Id ?? "").Order(StringComparer.Ordinal).ToList();
            var signature = string.Join(' ', owners);
            s.Record("double_bed_owners", signature);
            return (signature, owners.Count == 2 && couple.SetEquals(owners));
        }, cancellationToken).ConfigureAwait(false);
    }
}
