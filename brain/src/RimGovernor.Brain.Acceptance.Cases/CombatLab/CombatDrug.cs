using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/combat_drug</c> (#1311): the combat_drug order. On lab-open with colonist 0 carrying go-juice and the
/// others nothing, all drafted, one batch orders combat_drug for each: colonist 0's applies as Ingest, the others refuse
/// no_drug, and colonist 0 goes GoJuiceHigh.
/// </summary>
internal static class CombatLabCombatDrug
{
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-open", (f, _, _) =>
    {
        foreach (var p in f.Pawns.Where(p => p.Side == CombatFixtures.Colonist && p.Index == 0))
        {
            p.Inventory = ["GoJuice"];
        }
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/combat_drug",
        Scope = "The combat_drug order (#1311), a native op contract no snapshot can prove: on lab-open with colonist 0 carrying go-juice and "
            + "colonists 1 and 2 carrying nothing, all drafted, one combat_orders batch orders combat_drug for each; colonist 0's applies as "
            + "Ingest, colonists 1 and 2 refuse no_drug, and colonist 0's health then lists GoJuiceHigh.",
        Ticks = CombatLabKit.ProbeTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var colonists = new StagedLab(Fixture, s.Staged!).Colonists();
        if (colonists.Count != 3)
        {
            throw new AcceptanceException($"staged {colonists.Count} colonists, want 3");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        await CombatLabKit.DraftAsync(s, colonists, cancellationToken).ConfigureAwait(false);
        var results = await CombatLabKit.OrdersAsync(s, "combat-drug", cancellationToken, [.. colonists.Select(c => CombatLabKit.Order(c, new Operations.CombatOrderOrder.CombatDrugCase("GoJuice")))]).ConfigureAwait(false);
        CombatLabKit.Applied("colonist 0", results[0], "Ingest");
        CombatLabKit.Refused("colonist 1", results[1], "no_drug");
        CombatLabKit.Refused("colonist 2", results[2], "no_drug");

        _ = await s.RunUntilAsync("high", CombatLabKit.ProbeTicks, async ct =>
        {
            var p = await CombatLabKit.PawnAsync(s, "colonist-0", colonists[0], ct).ConfigureAwait(false);
            var hediffs = p.Health?.Hediffs.Select(h => h.DefName).ToList() ?? [];
            return (Wait.Signature(CombatLabKit.Job(p), hediffs.Count), hediffs.Contains("GoJuiceHigh"));
        }, cancellationToken).ConfigureAwait(false);
    }
}
