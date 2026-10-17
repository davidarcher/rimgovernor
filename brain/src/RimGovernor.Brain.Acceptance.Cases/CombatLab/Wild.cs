using Mirror = RimGovernor.Contracts.Wire.Mirror;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/wild</c> (#1116): the combat mirror's wild-animal side. lab-ranged with a thrumbo just past the raiders,
/// a hare beside them and a warg 40+ cells away: the first live frame has a wild-animal row for the thrumbo only.
/// </summary>
internal static class CombatLabWild
{
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-ranged", (f, cx, cz) =>
    {
        f.Pawns.Add(new LabPawn { Side = CombatFixtures.Wild, Kind = "Thrumbo", X = cx + 7, Z = cz + 18 });
        f.Pawns.Add(new LabPawn { Side = CombatFixtures.Wild, Kind = "Hare", X = cx - 7, Z = cz + 18 });
        f.Pawns.Add(new LabPawn { Side = CombatFixtures.Wild, Kind = "Warg", X = cx + 30, Z = cz - 30 });
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/wild",
        Scope = "The combat mirror's wild-animal side (#1116), a native read: on lab-ranged with three calm wild animals the first live snapshot frame "
            + "has a COMBAT_SIDE_WILD_ANIMAL row for a thrumbo 4 cells past the raiders, and no row for a hare beside them (small, no predator) or a "
            + "warg 40+ cells away.",
        Ticks = CombatLabKit.ProbeTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var wild = new StagedLab(Fixture, s.Staged!).Ids(CombatFixtures.Wild);
        var frame = await new LabFrames(s).CombatAsync("frame", cancellationToken).ConfigureAwait(false);
        s.RecordWire("rows", frame.CombatPawns);
        if (LabFrames.Row(frame, wild[0]) is not { Side: Mirror.CombatSide.WildAnimal } thrumbo)
        {
            throw new AcceptanceException($"thrumbo {wild[0]} near the raiders: row {LabFrames.Row(frame, wild[0])}, want side wild_animal");
        }

        s.RecordWire("thrumbo", thrumbo);
        foreach (var id in wild.Skip(1))
        {
            if (LabFrames.Row(frame, id) is { } row)
            {
                throw new AcceptanceException($"wild {id} has a combat row: {row}");
            }
        }
    }
}
