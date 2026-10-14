using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/wild</c> (Go <c>wild.go</c>).</summary>
internal static class CombatLabWild
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/wild",
        Scope = "The combat mirror's wild-animal side (#1116) on lab-ranged with three calm wild animals: a thrumbo 4 cells past the raiders is a "
            + "COMBAT_SIDE_WILD_ANIMAL row; a hare beside them (small, no predator) and a warg 40+ cells away are no row.",
        Start = new LabStart { Colonists = 4 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        await RunWindowsAsync(s, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunWindowsAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var staged = await CombatStage.StageAsync(h, "lab-ranged", cancellationToken, (f, cx, cz) =>
        {
            f.Pawns.Add(new LabPawn { Side = CombatFixtures.Wild, Kind = "Thrumbo", X = cx + 7, Z = cz + 18 });
            f.Pawns.Add(new LabPawn { Side = CombatFixtures.Wild, Kind = "Hare", X = cx - 7, Z = cz + 18 });
            f.Pawns.Add(new LabPawn { Side = CombatFixtures.Wild, Kind = "Warg", X = cx + 30, Z = cz - 30 });
        }).ConfigureAwait(false);
        var wild = staged.Ids(CombatFixtures.Wild);
        var frames = await CombatFrames.OpenAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        var (_, tick) = await CombatStage.TickAsync(h, CombatLabMirror.StepTicks, cancellationToken).ConfigureAwait(false);
        var state = await frames.NextAsync(tick, cancellationToken).ConfigureAwait(false);
        var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in state.Pawns)
        {
            rows[row["id"].AsString()] = row;
        }

        s.Report["frameRows"] = rows.Count;
        if (!rows.TryGetValue(wild[0], out var thrumbo) || thrumbo["side"].AsString() != "COMBAT_SIDE_WILD_ANIMAL")
        {
            throw new AcceptanceException($"thrumbo {wild[0]} near the raiders: row {(thrumbo is null ? "<nil>" : thrumbo.ToJson())}, want side wild_animal");
        }

        foreach (var id in wild.Skip(1))
        {
            if (rows.TryGetValue(id, out var row))
            {
                throw new AcceptanceException($"wild {id} has a combat row: {row.ToJson()}");
            }
        }
    }
}
