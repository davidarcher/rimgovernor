namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/drugs</c> (#1056): enemy drug facts. On lab-open the first raider is staged on go-juice and the second
/// luciferium-addicted, colonist 0 carries go-juice and steel; the first live combat frame's rows carry exactly those
/// flags, and the carried-drugs read lists only the go-juice.
/// </summary>
internal static class CombatLabDrugs
{
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-open", (f, _, _) =>
    {
        var drugs = new Queue<string>(["GoJuiceHigh", "LuciferiumAddiction"]);
        foreach (var p in f.Pawns)
        {
            if (p.Side == CombatFixtures.Colonist && p.Index == 0)
            {
                p.Inventory = ["GoJuice", "Steel"];
            }

            if (p.Side == CombatFixtures.Hostile && drugs.Count > 0)
            {
                p.Hediffs = [drugs.Dequeue()];
            }
        }
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/drugs",
        Scope = "Enemy drug facts (#1056), a native read no unit test reaches: on lab-open with the first raider staged on go-juice (GoJuiceHigh) and the "
            + "second luciferium-addicted, the first live snapshot frame's combat_pawns rows carry go_juice_high on the first, luciferium_addicted on the "
            + "second and neither on the third raider or any colonist; colonist 0 carries go-juice and steel, and carried_drugs lists only GoJuice for it "
            + "and an empty drug inventory for the other colonists.",
        Ticks = CombatLabKit.ProbeTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var hostiles = lab.Hostiles();
        var colonists = lab.Colonists();
        var frame = await new LabFrames(s).CombatAsync("frame", cancellationToken).ConfigureAwait(false);
        var want = new Dictionary<string, (bool Juice, bool Luci)>(StringComparer.Ordinal)
        {
            [hostiles[0]] = (true, false),
            [hostiles[1]] = (false, true),
            [hostiles[2]] = (false, false),
        };
        foreach (var id in colonists)
        {
            want[id] = (false, false);
        }

        s.RecordWireRows("rows", frame.CombatPawns);
        foreach (var (id, w) in want.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var row = LabFrames.Row(frame, id) ?? throw new AcceptanceException($"combat_pawns has no row for {id}");
            var got = (row.GoJuiceHigh == true, row.LuciferiumAddicted == true);
            if (got != w)
            {
                throw new AcceptanceException($"combat_pawns {id}: [go_juice_high luciferium_addicted] = [{got.Item1} {got.Item2}], want [{w.Juice} {w.Luci}]");
            }

            var index = colonists.IndexOf(id);
            if (index < 0)
            {
                continue;
            }

            var defs = row.CarriedDrugs?.Defs.ToList() ?? throw new AcceptanceException($"colonist {id} has unknown carried drugs: {row}");
            List<string> expected = index == 0 ? ["GoJuice"] : [];
            if (!defs.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new AcceptanceException($"colonist {id} carried drugs = [{string.Join(' ', defs)}], want [{string.Join(' ', expected)}]");
            }
        }
    }
}
