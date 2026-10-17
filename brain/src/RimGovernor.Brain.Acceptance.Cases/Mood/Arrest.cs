using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mood/arrest</c>: vanilla Arrest custody through the Arrest GiveJobIntent on Actions/Apply. Each row stages an armed
/// Social-20 warden, a target and a roofed room holding one prisoner bed; the drafted warden is ordered to arrest, and the
/// target ends a living colony prisoner, out of any mental state, lying in that exact bed (the production pawn read).
/// First the native guards refuse an undrafted warden, an unarmed drafted arrester, a calm colonist target and an
/// ordinary bed; after the legal order a resend applies again on the running job.
/// </summary>
internal static class Arrest
{
    /// <summary>One arrestable target: a sad-wandering colonist, or a standing neutral pawn of the ancients.</summary>
    private sealed record Row(string Name, Fx.StageFaction Faction, string? MentalState);

    private static readonly Row[] Rows =
    [
        new("sad-wander", Fx.StageFaction.Player, "Wander_Sad"),
        new("ancient", Fx.StageFaction.Ancients, null),
    ];

    internal static IEnumerable<TypedCase> All() => Rows.Select(Case);

    private static TypedCase Case(Row row) => new()
    {
        Name = row.Name == "sad-wander" ? "mood/arrest" : "mood/ancient-arrest",
        Scope = row.Name == "sad-wander"
            ? "The Arrest GiveJobIntent on Actions/Apply takes a sad-wandering colonist into custody: the drafted, armed warden's "
                + "arrest ends with the target a living prisoner, its mental state ended, in the exact staged prisoner bed. Before it "
                + "the native guards refuse an undrafted warden, an unarmed arrester, a calm target and an ordinary bed."
            : "The Arrest GiveJobIntent on Actions/Apply takes a standing neutral Faction.OfAncients pawn into custody: the "
                + "drafted, armed warden's arrest ends with the target a living prisoner in the exact staged prisoner bed, after the "
                + "same native guard refusals.",
        Ticks = 6000,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec(row),
        Crew = new Crew { Size = 1 },
        Keep = ["Rest"],
        QuietWorld = true,
        Run = RunAsync,
    };

    // A 5x5 roofed wooden room (a 3x3 interior) with the prisoner bed inside and its door east; the warden and the target
    // stand outside it.
    private static Fx.StageSpec Spec(Row row) => new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 10,
            Height = 6,
            OffsetX = 4,
            Radius = 12,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Heavy, Fx.StageGround.Unroofed],
        },
        Rooms = [new Fx.StageRoom { Name = "cell", Rect = new(0, 0, 5, 5), Stuff = "WoodLog", Doors = [new(4, 2)], Roof = true }],
        Buildings =
        [
            new Fx.StageBuilding { Name = "bed", Def = "Bed", Stuff = "WoodLog", Cells = [new(2, 2)], ForPrisoners = true },
            new Fx.StageBuilding { Name = "plain", Def = "Bed", Stuff = "WoodLog", Cells = [new(9, 0)] },
        ],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = "warden",
                Kind = "Colonist",
                SkillLevel = 20,
                ClearTraits = true,
                Gear = new Fx.StageGear { Weapon = "MeleeWeapon_Knife", WeaponStuff = "Steel" },
                Cell = new(7, 2),
            },
            new Fx.StagePawn { Name = "brawler", Kind = "Colonist", ClearTraits = true, Gear = new Fx.StageGear(), Cell = new(6, 4) },
            new Fx.StagePawn
            {
                Name = "target",
                Kind = "Colonist",
                Faction = row.Faction,
                ClearTraits = true,
                Gear = new Fx.StageGear(),
                Cell = new(8, 3),
                Needs = new Dictionary<string, float> { ["Rest"] = 0.1f },
                MentalState = row.MentalState,
            },
        ],
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var warden = staged.Pawns["warden"].Id;
        var target = staged.Pawns["target"].Id;
        var bed = staged.Buildings["bed"].Ids.Single();
        var brawler = staged.Pawns["brawler"].Id;
        var plain = staged.Buildings["plain"].Ids.Single();
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        await TypedPawnOps.RefusedAsync(s, "undrafted", TypedPawnOps.GiveJob(warden, "Arrest", target, bed), "drafted pawn", cancellationToken).ConfigureAwait(false);
        _ = await TypedPawnOps.ApplyAsync(s, "draft-warden", TypedPawnOps.Draft(warden), cancellationToken).ConfigureAwait(false);
        _ = await TypedPawnOps.ApplyAsync(s, "draft-brawler", TypedPawnOps.Draft(brawler), cancellationToken).ConfigureAwait(false);
        await TypedPawnOps.RefusedAsync(s, "unarmed", TypedPawnOps.GiveJob(brawler, "Arrest", target, bed), "unarmed", cancellationToken).ConfigureAwait(false);
        await TypedPawnOps.RefusedAsync(s, "calm-target", TypedPawnOps.GiveJob(warden, "Arrest", brawler, bed), "mental state", cancellationToken).ConfigureAwait(false);
        await TypedPawnOps.RefusedAsync(s, "plain-bed", TypedPawnOps.GiveJob(warden, "Arrest", target, plain), "prisoner bed", cancellationToken).ConfigureAwait(false);
        _ = await TypedPawnOps.ApplyAsync(s, "arrest", TypedPawnOps.GiveJob(warden, "Arrest", target, bed), cancellationToken).ConfigureAwait(false);
        _ = await TypedPawnOps.ApplyAsync(s, "arrest-resend", TypedPawnOps.GiveJob(warden, "Arrest", target, bed), cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("arrest-custody", s.TicksLeft, async ct =>
        {
            var row = await TypedPawnOps.PawnAsync(s, "custody", target, ct).ConfigureAwait(false);
            if (row is null || row.Dead)
            {
                throw new AcceptanceException($"the arrest cost the target's life: {row}");
            }

            var mental = row.MentalState?.DefName ?? "";
            var inBed = row.Health?.BedId ?? "";
            s.RecordWire("custody", row);
            return (string.Create(CultureInfo.InvariantCulture, $"{row.Prisoner} {mental} {inBed} {row.Pawn.Position}"), row.Prisoner && mental.Length == 0 && inBed == bed);
        }, cancellationToken).ConfigureAwait(false);
    }
}
