using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/mortar</c> (#930, #931, #1202, #1235): combat orders on buildings. On lab-open plus a hostile ship part,
/// a player mortar with an HE shell and a stone chunk, an attack on the part, a mortar_fire at it and a man_mortar crew
/// apply while the same pair on the chunk refuses not_a_mortar; the paused game reads the rifleman on the part and the
/// crew on the mortar, and a targetless mortar_fire clears the mortar (and refuses on the chunk).
/// </summary>
internal static class CombatLabMortar
{
    // A hostile crashed ship part 20 north, a player mortar behind the line and one HE shell beside it: the last three things.
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-open", (f, cx, cz) =>
    {
        f.Things.Add(new FixtureThing { Def = "DefoliatorShipPart", X = cx, Z = cz + 20, Hostile = true });
        f.Things.Add(new FixtureThing { Def = "Turret_Mortar", X = cx, Z = cz - 15 });
        f.Things.Add(new FixtureThing { Def = "Shell_HighExplosive", X = cx + 2, Z = cz - 16, Item = true });
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/mortar",
        Scope = "combat_orders on buildings (#930, #931), native op contracts no snapshot can prove: on lab-open plus a hostile crashed ship part, "
            + "a player mortar with one HE shell behind the line and a stone chunk, one batch drafts the riflemen and orders an attack on the ship "
            + "part, a mortar_fire at the part with a man_mortar crew, and the same pair on the chunk; the first three apply (AttackStatic, the aim, "
            + "ManTurret) and the chunk pair refuses not_a_mortar (#1202). The paused pawn read gives the rifleman shooting the part and the crew "
            + "manning the mortar; then a mortar_fire with no target applies on the mortar (clearing its forced target) and refuses not_a_mortar "
            + "on the chunk (#1235).",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var colonists = lab.Colonists().GetRange(0, 3);
        var n = Fixture.Things.Count;
        var part = lab.Thing(n - 3);
        var mortarId = lab.Thing(n - 2);
        var mortar = lab.Cell(0, -15);
        var chunk = lab.Cell(-3, -9);
        var target = lab.Cell(0, 20);
        static Operations.CombatOrderOrder.MortarFireCase Fire(RimGovernor.Brain.Domain.Cell mortar, RimGovernor.Brain.Domain.Cell? target) =>
            new(new Operations.CombatMortarFire { Mortar = mortar, Aim = target is {} at ? new Operations.MortarAim.At(at) : new Operations.MortarAim.Cleared() });

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        await CombatLabKit.DraftAsync(s, colonists, cancellationToken).ConfigureAwait(false);
        var results = await CombatLabKit.OrdersAsync(
            s,
            "mortar",
            cancellationToken,
            CombatLabKit.Order(colonists[0], new Operations.CombatOrderOrder.AttackCase(CombatLabKit.Pawn(part))),
            CombatLabKit.Order(null, Fire(mortar, target)),
            CombatLabKit.Order(colonists[1], new Operations.CombatOrderOrder.ManMortarCase(mortar)),
            CombatLabKit.Order(null, Fire(chunk, target)),
            CombatLabKit.Order(colonists[2], new Operations.CombatOrderOrder.ManMortarCase(chunk))).ConfigureAwait(false);
        CombatLabKit.Applied("attack part", results[0], "AttackStatic");
        CombatLabKit.Applied("mortar_fire", results[1]);
        CombatLabKit.Applied("man_mortar", results[2], "ManTurret");
        CombatLabKit.Refused("mortar_fire chunk", results[3], "not_a_mortar");
        CombatLabKit.Refused("man_mortar chunk", results[4], "not_a_mortar");

        var after = await CombatLabKit.PawnsAsync(s, "after", colonists, cancellationToken).ConfigureAwait(false);
        s.RecordWireRows("after", after.Values.OrderBy(p => p.Pawn.Id, StringComparer.Ordinal));
        if (after.GetValueOrDefault(colonists[0]) is not { } rifleman || CombatLabKit.Job(rifleman) != "AttackStatic" || CombatLabKit.JobThing(rifleman) != part)
        {
            throw new AcceptanceException($"rifleman does not shoot the ship part {part}: {after.GetValueOrDefault(colonists[0])?.Job}");
        }

        if (after.GetValueOrDefault(colonists[1]) is not { } crew || CombatLabKit.Job(crew) != "ManTurret" || CombatLabKit.JobThing(crew) != mortarId)
        {
            throw new AcceptanceException($"crew does not man the mortar {mortarId}: {after.GetValueOrDefault(colonists[1])?.Job}");
        }

        // A mortar_fire with no target clears the forced target.
        var cleared = await CombatLabKit.OrdersAsync(s, "clear", cancellationToken, CombatLabKit.Order(null, Fire(mortar, null)), CombatLabKit.Order(null, Fire(chunk, null))).ConfigureAwait(false);
        CombatLabKit.Applied("clear mortar", cleared[0]);
        CombatLabKit.Refused("clear chunk", cleared[1], "not_a_mortar");
    }
}
