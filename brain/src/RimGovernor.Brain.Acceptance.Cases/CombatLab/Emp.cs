
namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/emp</c> (#1049): EMP ground targeting. On lab-ranged with rifleman 0 holding EMP grenades and the four
/// raiders downed in a clump past the sandbags, each in a charged shield belt, the drafted thrower's attack_ground at the
/// clump's centre leaves every raider's shield energy at 0.
/// </summary>
internal static class CombatLabEmp
{
    /// <summary>The clump the raiders lie in, centre-relative; the grenade lands on its centre.</summary>
    private static readonly (int Dx, int Dz)[] Clump = [(-1, 2), (0, 2), (1, 2), (0, 3)];

    // The thrower's EMP grenades and the raiders downed (so they hold still) in a clump within throwing range, shield
    // belts on; a fifth raider stunned far off keeps the combat sections live (they fill only while a hostile stands).
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-ranged", (f, cx, cz) =>
    {
        var h = 0;
        LabPawn? raider = null;
        foreach (var p in f.Pawns)
        {
            if (p.Side == CombatFixtures.Colonist && p.Index == 0)
            {
                p.Weapon = "Weapon_GrenadeEMP";
                p.WeaponStuff = "";
            }
            else if (p.Side == CombatFixtures.Hostile)
            {
                p.X = cx + Clump[h].Dx;
                p.Z = cz + Clump[h].Dz;
                p.Downed = true;
                p.Apparel = "Apparel_ShieldBelt";
                raider = p;
                h++;
            }
        }

        f.Pawns.Add(new LabPawn { Side = CombatFixtures.Hostile, Kind = raider!.Kind, X = cx + 25, Z = cz + 25, StunTicks = 30000 });
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/emp",
        Scope = "EMP ground targeting (#1049), vanilla physics no snapshot can prove: on lab-ranged with rifleman 0 holding EMP grenades and the "
            + "four raiders downed in a clump past the sandbags, each wearing a charged shield belt (a fifth raider stunned far off keeps combat "
            + "live), the frame reads every clump shield charged, one combat_orders batch drafts the thrower and orders attack_ground at the "
            + "clump's centre (both applied), and a later frame reads every clump raider's shield energy 0.",
        Ticks = 2 * CombatLabKit.ProbeTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var thrower = lab.Colonists()[0];
        var clump = lab.Hostiles().Take(Clump.Length).ToList();
        var frames = new LabFrames(s);
        var before = await frames.CombatAsync("before", cancellationToken).ConfigureAwait(false);
        foreach (var id in clump)
        {
            if (LabFrames.Row(before, id) is not { Shield.Energy: > 0 })
            {
                throw new AcceptanceException($"raider {id} before the EMP: want a charged shield, got {LabFrames.Row(before, id)}");
            }
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var results = await CombatLabKit.OrdersAsync(
            s,
            "emp",
            cancellationToken,
            CombatLabKit.Order(thrower, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DraftCase()),
            CombatLabKit.Order(thrower, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AttackGroundCase(lab.Cell(0, 2)))).ConfigureAwait(false);
        CombatLabKit.Applied("draft", results[0]);
        CombatLabKit.Applied("attack_ground", results[1]);

        var after = await frames.UntilAsync("dropped", CombatLabKit.ProbeTicks, before.Context?.Tick + 1 ?? 0, f => clump.All(id => LabFrames.Row(f, id) is { Shield.Energy: 0 }), cancellationToken).ConfigureAwait(false);
        s.RecordWireRows("after", clump.Select(id => LabFrames.Row(after, id)));
    }
}
