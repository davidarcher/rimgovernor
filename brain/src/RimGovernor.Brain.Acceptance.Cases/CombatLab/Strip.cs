using RimGovernor.Brain.Bridge;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/strip</c> (#1117): the generic strip op. On lab-open cut to one raider staged downed in a duster, a
/// designate Strip applies with Strip designation evidence, a repeat does not apply, and a colonist strips the duster off.
/// </summary>
internal static class CombatLabStrip
{
    private const string Apparel = "Apparel_Duster";

    /// <summary>Bounds the haul: a colonist walks to the raider and strips it through ordinary Hauling work.</summary>
    private const int StripTicks = 2400 + CombatLabKit.ProbeTicks;

    // One raider, downed and wearing a duster.
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-open", (f, _, _) =>
    {
        var raiders = f.Pawns.Where(p => p.Side == CombatFixtures.Hostile).ToList();
        foreach (var extra in raiders.Skip(1))
        {
            _ = f.Pawns.Remove(extra);
        }

        raiders[0].Downed = true;
        raiders[0].Apparel = Apparel;
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/strip",
        Scope = "The generic strip op (#1117), a native op contract no snapshot can prove: on lab-open cut to one raider staged downed in a "
            + "duster (its one worn apparel), one operations_apply designate STRIP on the raider applies with present Strip designation evidence, "
            + "a second one does not apply, and a colonist then strips the duster off through ordinary Hauling work. Who to strip is #1079.",
        Ticks = StripTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var hostiles = new StagedLab(Fixture, s.Staged!).Hostiles();
        if (hostiles.Count != 1)
        {
            throw new AcceptanceException($"staged {hostiles.Count} hostiles, want 1");
        }

        var raider = hostiles[0];
        s.Record("raider", raider);

        // The staging strips the raider's own gear and dresses it in the duster: its one worn apparel.
        var worn = await WornAsync(s, "worn", raider, cancellationToken).ConfigureAwait(false);
        if (worn.Count != 1)
        {
            throw new AcceptanceException($"raider before strip: want one worn apparel ({Apparel}), got [{string.Join(", ", worn)}]");
        }

        var duster = worn[0];
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var first = await StripAsync(s, "strip-1", raider, cancellationToken).ConfigureAwait(false);
        var outcome = first.Outcome?.Applied?.Outcome?.Applied
            ?? throw new AcceptanceException($"strip not applied: {first}");
        if (outcome.Observed?.Effect?.Designation is not { DesignationDef: "Strip", Present: true } evidence || evidence.ThingId != raider)
        {
            throw new AcceptanceException($"strip evidence: want a present Strip designation on {raider}, got {outcome.Observed?.Effect}");
        }

        var second = await StripAsync(s, "strip-2", raider, cancellationToken).ConfigureAwait(false);
        if (second.Outcome?.Applied?.Outcome?.Applied is not null)
        {
            throw new AcceptanceException($"a second strip on a designated raider applied: {second}");
        }

        _ = await s.RunUntilAsync("stripped", StripTicks, async ct =>
        {
            var now = await WornAsync(s, "worn", raider, ct).ConfigureAwait(false);
            return (Wait.Signature(now.Count), !now.Contains(duster));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Operations.ActionResult> StripAsync(ITypedSession s, string key, string raider, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(key, NativeOps.Apply, new Operations.ApplyRequest
        {
            Identity = s.Identity,
            Actions = [new Operations.Action { Key = key, Intent = new Operations.DesignateIntent { ThingId = raider, Designation = Operations.ThingDesignation.Strip } }],
        }, cancellationToken).ConfigureAwait(false);
        var result = reply.Results.Count == 1 ? reply.Results[0] : throw new AcceptanceException($"{key}: {reply.BatchFailure} {string.Join("; ", reply.Results)}");
        s.RecordWire(key, result);
        return result;
    }

    // The ids of the pawn's worn apparel.
    private static async Task<List<string>> WornAsync(ITypedSession s, string label, string id, CancellationToken cancellationToken) =>
        [.. (await CombatLabKit.PawnAsync(s, label, id, cancellationToken).ConfigureAwait(false)).Equipment?.Apparel.Select(a => a.Thing.Id ?? "") ?? []];
}
