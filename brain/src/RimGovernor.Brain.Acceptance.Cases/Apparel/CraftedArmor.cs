using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>apparel/crafted_armor</c> (#3621 of #2467): the forecast picks a crafted garment's stuff by the armor the harm ledger
/// prices. One soldier wears a cloth duster and a short bow beside a hand tailoring bench with cloth and devilstrand cloth stocked,
/// under Cassandra on day 25 (the stage of <c>apparel/harm_armor_direction</c>, which reaches a calibrated exposure cell). The
/// soldier already wears a garment, so the clothing family competes with swaps; a coupled selection crafts the devilstrand duster
/// (the selected commitment's id names the stuff), and its harm term is below the baseline's: the armor gain is priced from the
/// delivery.
/// </summary>
internal static class CraftedArmor
{
    private const string Cloth = "Cloth";
    private const string Devilstrand = "DevilstrandCloth";
    private const string Duster = "Apparel_Duster";

    internal static TypedCase Case() => new()
    {
        Name = "apparel/crafted_armor",
        Scope = "Issue #3621 (epic #2467): a soldier who wears a cloth duster, beside a tailoring bench with both cloth and devilstrand "
            + "cloth stocked, has a coupled selection craft the devilstrand duster, and that selection's harm term is below the "
            + "baseline's. A unit test cannot observe the native bill options, stocks, raid points and defense capacity the "
            + "clothing family and the harm term read together.",
        Ticks = TypedCase.StagedTickCap, // a hang guard: the decisive review lands within the first minute of game time
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Crew = new Crew { Size = 1 },
        Stage = new Fx.StageSpec
        {
            Paused = true,
            World = new Fx.StageWorld { Storyteller = "Cassandra", DaysPassed = 25 },
            Research = [new Fx.StageResearch { Def = "ComplexClothing" }],
            Site = new Fx.StageSite
            {
                Width = 6,
                Height = 5,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 6, 5), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 6, 5), Terrain = "Soil", Home = true }],
            Buildings = [new Fx.StageBuilding { Name = "tailor", Def = "HandTailoringBench", Cells = [new(1, 3)] }],
            Zones =
            [
                new Fx.StageZone
                {
                    Name = "stock",
                    Kind = Fx.StageZoneKind.Stockpile,
                    Allow = [Cloth, Devilstrand],
                    Cells = [new(0, 0), new(1, 0), new(2, 0), new(3, 0)],
                },
            ],
            // A duster takes 80 stuff; two stacks of each.
            Things =
            [
                new Fx.StageThing { Name = "cloth0", Def = Cloth, Count = 75, Cell = new(0, 0) },
                new Fx.StageThing { Name = "cloth1", Def = Cloth, Count = 75, Cell = new(1, 0) },
                new Fx.StageThing { Name = "devilstrand0", Def = Devilstrand, Count = 75, Cell = new(2, 0) },
                new Fx.StageThing { Name = "devilstrand1", Def = Devilstrand, Count = 75, Cell = new(3, 0) },
            ],
            Pawns =
            [
                new Fx.StagePawn
                {
                    Name = "soldier",
                    Colonist = 0,
                    Cell = new(4, 2),
                    SkillLevel = 0,
                    Gear = new Fx.StageGear { Weapon = "Bow_Short", Apparel = [Duster], ApparelStuff = Cloth, Strip = true },
                },
            ],
        },
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "crafted-armor" },
        Run = RunAsync,
    };

    private static bool CraftsDevilstrand(CoupledSelectionRow row) =>
        row.SelectedIds.Any(id => id.StartsWith("clothing-", StringComparison.Ordinal) && id.EndsWith("-" + Devilstrand, StringComparison.Ordinal));

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        IReadOnlyList<CoupledSelectionRow> rows = [];
        _ = await s.ServeUntilAsync("crafted-armor", s.TicksLeft, ct =>
        {
            rows = CoupledSelections.Of(s, before);
            return Task.FromResult((string.Create(CultureInfo.InvariantCulture, $"{rows.Count} {rows.Count(CraftsDevilstrand)}"), rows.Any(CraftsDevilstrand)));
        }, cancellationToken).ConfigureAwait(false);

        var chosen = rows.FirstOrDefault(CraftsDevilstrand)
            ?? throw new AcceptanceException($"no coupled selection crafted the devilstrand duster in {rows.Count} rows: "
                + string.Join("; ", rows.Select(r => $"[{string.Join(' ', r.SelectedIds)}]")));
        s.Record("selection", string.Create(CultureInfo.InvariantCulture,
            $"{chosen.Verdict} ids=[{string.Join(' ', chosen.SelectedIds)}] harm={chosen.Selected.Harm} baseline_harm={chosen.Baseline.Harm} unknown=[{string.Join(' ', chosen.Selected.Unknown ?? [])}]"));
        if (chosen.Selected.Harm is not { } harm || chosen.Baseline.Harm is not { } worn)
        {
            throw new AcceptanceException($"the harm term is unknown: selected {chosen.Selected.Harm}, baseline {chosen.Baseline.Harm}, unknown [{string.Join(' ', chosen.Selected.Unknown ?? [])}]");
        }

        if (!(harm < worn))
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture,
                $"the crafted devilstrand duster did not lower the harm: {harm} against the worn outfit's {worn}"));
        }
    }
}
