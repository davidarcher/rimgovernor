using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combat/attack</c>: a drafted colonist attacks through one combat_orders attack, native picking melee or ranged from
/// its weapon, one lane per weapon kind; the same key replays the receipt, and every target goes down within the bound.
/// No damage or completion injection.
/// </summary>
public static class CombatCases
{
    /// <summary>The bound for every target to go down: each attacker stands ten cells from a weak animal.</summary>
    private const int FightTicks = 4800 + CombatLabKit.ProbeTicks;

    /// <summary>One lane per weapon kind: the attacker's weapon, its target's kind and the job native picks.</summary>
    private static readonly (string Weapon, string Target, string Job)[] Lanes =
    [
        (CombatFixtures.Longsword, "Hare", "AttackMelee"),
        (CombatFixtures.Rifle, "Tortoise", "AttackStatic"),
        ("Weapon_GrenadeFrag", "Tortoise", "AttackStatic"),
    ];

    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-open", (f, cx, cz) =>
    {
        f.Colonists = Lanes.Length;
        f.Layout = null;
        f.Things = [];
        f.Pawns = [];
        for (var i = 0; i < Lanes.Length; i++)
        {
            var x = cx + ((i - 1) * 20);
            f.Pawns.Add(new LabPawn { Side = CombatFixtures.Colonist, Index = i, X = x, Z = cz - 5, Weapon = Lanes[i].Weapon });
            f.Pawns.Add(new LabPawn { Side = CombatFixtures.Manhunter, Kind = Lanes[i].Target, X = x, Z = cz + 5 });
        }
    });

    /// <summary>Registers every <c>combat/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(new TypedCase
        {
            Name = "combat/attack",
            Scope = "One combat_orders attack per drafted colonist, native picking melee or ranged from the weapon (a longsword, an assault rifle, "
                + "frag grenades): each order applies with the expected job, the same key replays the same receipts, and every target is downed "
                + "or dead within the bound. No damage or completion injection.",
            Ticks = FightTicks,
            Tier = CaseTier.OnDemand,
            Start = new LabStart { Colonists = Fixture.Colonists },
            Stage = CombatLabKit.Spec(Fixture),

            // The targets are manhunters; a quiet world would freeze them.
            QuietWorld = false,
            Run = RunAsync,
        });
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var attackers = lab.Colonists();
        var targets = lab.Manhunters();
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        await CombatLabKit.DraftAsync(s, attackers, cancellationToken).ConfigureAwait(false);
        var orders = attackers.Select((a, i) => CombatLabKit.Order(a, new Operations.CombatOrderOrder.AttackCase(CombatLabKit.Pawn(targets[i])))).ToArray();
        var results = await CombatLabKit.OrdersAsync(s, "attack", cancellationToken, orders).ConfigureAwait(false);
        for (var i = 0; i < Lanes.Length; i++)
        {
            CombatLabKit.Applied("attack " + Lanes[i].Weapon, results[i], Lanes[i].Job);
        }

        // Still paused: each attacker is on its order's job.
        var pawns = await CombatLabKit.PawnsAsync(s, "attacking", attackers, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < Lanes.Length; i++)
        {
            if (CombatLabKit.Job(pawns[attackers[i]]) != Lanes[i].Job || CombatLabKit.JobThing(pawns[attackers[i]]) != targets[i])
            {
                throw new AcceptanceException($"attacking: {Lanes[i].Weapon} attacker on {CombatLabKit.Job(pawns[attackers[i]])} at {CombatLabKit.JobThing(pawns[attackers[i]])}, want {Lanes[i].Job} at {targets[i]}");
            }
        }

        // The same key replays the committed receipts without a second order.
        var replay = await CombatLabKit.OrdersAsync(s, "attack", cancellationToken, orders).ConfigureAwait(false);
        if (!replay.SequenceEqual(results))
        {
            throw new AcceptanceException($"attack replay: {string.Join("; ", replay)} differs from {string.Join("; ", results)}");
        }

        _ = await s.RunUntilAsync("down", FightTicks, async ct =>
        {
            var rows = await CombatLabKit.PawnsAsync(s, "targets", targets, ct).ConfigureAwait(false);
            List<string> standing = [.. targets.Where(id => rows.GetValueOrDefault(id) is { Dead: false, Downed: false })];
            return (Wait.Signature(string.Join(",", standing)), standing.Count == 0);
        }, cancellationToken).ConfigureAwait(false);
    }
}
