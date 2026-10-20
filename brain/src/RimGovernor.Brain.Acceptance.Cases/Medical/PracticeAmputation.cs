using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>medical/practice-amputation</c> (#3639 of #3344): a practice slot made by surgery, priced by the slot's tier-2 counterfactual. A
/// Medicine-3 doctor under Cassandra on day 25 (the stage of <c>apparel/crafted_armor</c>, which reaches a calibrated exposure cell)
/// beside a prison cell holding one unskilled, traitless pirate prisoner with every limb, in its prison bed, with wood logs for a peg
/// leg and herbal medicine stocked. A coupled selection opens a slot on one of the prisoner's limbs (a practice surgery on that part),
/// and a later one runs a different practice-cycle step on the same part: the limb was taken and its cycle followed. PracticeSurgeryTests
/// prove the pricing; a unit test cannot run the game's surgery, the operations read after it or the review that prices the threat.
/// </summary>
internal static class PracticeAmputation
{
    private const string Prefix = "practice/surgery/";

    internal static TypedCase Case() => new()
    {
        Name = "medical/practice-amputation",
        Scope = "Issue #3639 (epic #3344): a Medicine-3 doctor under a calibrated raid threat, with one non-recruit prisoner with every "
            + "limb, peg-leg wood and herbal medicine, has a coupled selection take one limb by practice surgery, then a later selection run "
            + "a different practice-cycle step on that same part. A unit test cannot run the native surgery or the review that prices it.",
        Ticks = TypedCase.StagedTickCap, // a hang guard: the removal and the next selection land within a few thousand ticks
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Crew = new Crew { Size = 1 },
        Stage = new Fx.StageSpec
        {
            Paused = true,
            World = new Fx.StageWorld { Storyteller = "Cassandra", DaysPassed = 25 },
            Site = new Fx.StageSite
            {
                Width = 10,
                Height = 6,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Heavy, Fx.StageGround.Unroofed],
            },
            Rooms = [new Fx.StageRoom { Name = "cell", Rect = new(0, 0, 5, 5), Stuff = "WoodLog", Doors = [new(4, 2)], Roof = true }],
            Buildings = [new Fx.StageBuilding { Name = "bed", Def = "Bed", Stuff = "WoodLog", Cells = [new(2, 2)], ForPrisoners = true }],
            Zones = [new Fx.StageZone { Name = "stock", Kind = Fx.StageZoneKind.Stockpile, Allow = ["WoodLog", "MedicineHerbal"], Cells = [new(7, 0), new(8, 0)] }],
            Things =
            [
                new Fx.StageThing { Name = "wood", Def = "WoodLog", Count = 75, Cell = new(7, 0) },
                new Fx.StageThing { Name = "medicine", Def = "MedicineHerbal", Count = 20, Cell = new(8, 0) },
            ],
            Pawns =
            [
                new Fx.StagePawn { Name = "doctor", Colonist = 0, Cell = new(7, 3), SkillLevel = 0, Skills = new Dictionary<string, int> { ["Medicine"] = 3 } },
                new Fx.StagePawn
                {
                    Name = "prisoner",
                    Kind = "Colonist",
                    Faction = Fx.StageFaction.Hostile,
                    Prisoner = true,
                    SkillLevel = 0,
                    ClearTraits = true,
                    Gear = new Fx.StageGear { Strip = true },
                    Cell = new(2, 2),
                    Bed = "bed",
                },
            ],
        },
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "practice-amputation" },
        Run = RunAsync,
    };

    /// <summary>The (recipe, part) of each practice surgery on the prisoner a row selects.</summary>
    private static IEnumerable<(string Recipe, string Part)> Surgeries(CoupledSelectionRow row, string prisoner) =>
        row.SelectedIds.Where(id => id.StartsWith(Prefix, StringComparison.Ordinal)).Select(id => id[Prefix.Length..].Split('/'))
            .Where(p => p.Length == 5 && p[1] == prisoner).Select(p => (p[2], p[3]));

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var prisoner = s.Staged!.Pawns["prisoner"].Id;
        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        IReadOnlyList<CoupledSelectionRow> rows = [];
        (string Recipe, string Part)? opened = null, cycled = null;
        _ = await s.ServeUntilAsync("practice-amputation", s.TicksLeft, ct =>
        {
            rows = CoupledSelections.Of(s, before);
            var steps = rows.SelectMany(r => Surgeries(r, prisoner)).ToList();
            opened = steps.Count == 0 ? null : steps[0];
            cycled = opened is { } o && steps.FirstOrDefault(x => x.Part == o.Part && x.Recipe != o.Recipe) is { Recipe: not null } c ? c : null;
            return Task.FromResult((string.Create(CultureInfo.InvariantCulture, $"{rows.Count} {opened} {cycled}"), cycled is not null));
        }, cancellationToken).ConfigureAwait(false);

        s.Record("steps", string.Join("; ", rows.Select(r => $"[{string.Join(' ', r.SelectedIds.Where(id => id.StartsWith(Prefix, StringComparison.Ordinal)))}]")));
        if (opened is null || cycled is null)
        {
            throw new AcceptanceException($"want a practice surgery opening a slot on {prisoner} and a later cycle step on the same part; got {opened} then {cycled} in {rows.Count} rows: "
                + string.Join("; ", rows.Select(r => $"{r.Verdict} [{string.Join(' ', r.SelectedIds)}] rejected [{string.Join(' ', r.RejectedIds)}]")));
        }

        // One limb: every practice surgery the run selected on the prisoner is on that part.
        var parts = rows.SelectMany(r => Surgeries(r, prisoner)).Select(x => x.Part).Distinct().ToArray();
        if (parts.Length != 1)
        {
            throw new AcceptanceException($"want practice surgeries on one part of {prisoner}, got parts [{string.Join(' ', parts)}]");
        }
    }
}
