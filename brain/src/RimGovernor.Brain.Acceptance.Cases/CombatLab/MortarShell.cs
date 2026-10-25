
namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/mortar-shell</c> (#1051, #1202): the mortar shell order. On lab-siege an HE mortar_fire at the camp and a
/// man_mortar crew load Shell_HighExplosive (the frame's combat_mortars row), a Steel shell refuses unknown_shell, and an
/// EMP mortar_fire alone has the crew load Shell_EMP.
/// </summary>
internal static class CombatLabMortarShell
{
    /// <summary>Bounds each load: a few cells' walk to the shell and the load.</summary>
    private const int LoadTicks = 1800 + CombatLabKit.ProbeTicks;

    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-siege");

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/mortar-shell",
        Scope = "The combat_orders mortar shell op (#1051), a native op no snapshot can prove: on lab-siege, a mortar_fire order for HE at the "
            + "siege camp and a man_mortar crew load Shell_HighExplosive (the frame's combat_mortars row's loaded_shell), a mortar_fire naming "
            + "Steel refuses unknown_shell, and a mortar_fire alone naming Shell_EMP unloads the HE (if still loaded) and the crew loads the EMP "
            + "shell (#1202).",
        Ticks = 2 * LoadTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var crew = lab.Colonists()[0];
        var mortar = lab.Cell(0, CombatFixtures.SiegeOurMortar);
        var camp = lab.Cell(0, CombatFixtures.SiegeCampMortar);
        RimGovernor.Brain.GameOrders.Operations.CombatOrder Fire(string shell) =>
            CombatLabKit.Order(null, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.MortarFireCase(new RimGovernor.Brain.GameOrders.Operations.CombatMortarFire { Mortar = mortar, Aim = new RimGovernor.Brain.GameOrders.Operations.MortarAim.At(camp), Shell = shell }));

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var results = await CombatLabKit.OrdersAsync(
            s,
            "load-he",
            cancellationToken,
            CombatLabKit.Order(crew, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DraftCase()),
            Fire("Shell_HighExplosive"),
            CombatLabKit.Order(crew, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.ManMortarCase(mortar)),
            Fire("Steel")).ConfigureAwait(false);
        CombatLabKit.Applied("draft", results[0]);
        CombatLabKit.Applied("HE fire", results[1]);
        CombatLabKit.Applied("crew", results[2], "ManTurret");
        CombatLabKit.Refused("steel fire", results[3], "unknown_shell");

        var frames = new LabFrames(s);
        await LoadedAsync(s, frames, mortar, "Shell_HighExplosive", cancellationToken).ConfigureAwait(false);

        // The crew still mans it: the fire order alone retakes ManTurret.
        CombatLabKit.Applied("EMP fire", (await CombatLabKit.OrdersAsync(s, "load-emp", cancellationToken, Fire("Shell_EMP")).ConfigureAwait(false))[0]);
        await LoadedAsync(s, frames, mortar, "Shell_EMP", cancellationToken).ConfigureAwait(false);
    }

    // Runs until a frame's combat_mortars row for the mortar shows the shell loaded.
    private static async Task LoadedAsync(TypedSession s, LabFrames frames, RimGovernor.Brain.Domain.Cell mortar, string shell, CancellationToken cancellationToken)
    {
        var frame = await frames.UntilAsync("loaded-" + shell, LoadTicks, 0, f => f.CombatMortars.Any(m => m.Cell == mortar && m.LoadedShell == shell), cancellationToken).ConfigureAwait(false);
        s.RecordWire("loaded_" + shell, frame.CombatMortars.First(m => m.Cell == mortar));
    }
}
