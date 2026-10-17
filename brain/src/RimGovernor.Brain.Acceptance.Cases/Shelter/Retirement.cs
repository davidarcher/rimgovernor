using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>shelter/retirement</c>: the shelter lifecycle's last act on a real game. A retired shelter hut stands on the lab with
/// a sleeping spot, a crafting spot and a butcher spot inside, and the injected layout plan holds its footprint as
/// <c>LayoutPlan.RetiredGround</c>; under the served clearance family pawns deconstruct the hut's walls and door and remove
/// its spots until nothing of ours stands on that ground (the production buildings read). Deciding to retire the shelter
/// is policy (ClearanceGround's RetiredGroundOf and RetiredGroundDone); this case owns the native clearance.
/// </summary>
internal static class ShelterRetirement
{
    private static readonly string[] Spots = ["SleepingSpot", "CraftingSpot", "ButcherSpot"];

    internal static TypedCase Case() => new()
    {
        Name = "shelter/retirement",
        Scope = "Issue #2076: a retired shelter hut (its footprint the injected plan's RetiredGround) with a sleeping, crafting and "
            + "butcher spot inside is cleared by the served clearance family: pawns deconstruct its walls and door and remove its "
            + "spots until nothing of the colony's stands on the retired ground, read through the production buildings read.",
        Ticks = TypedCase.StagedTickCap,
        Tier = CaseTier.Nightly,
        Start = new LabStart { Colonists = 3 },
        Crew = new Crew { Size = 3 },
        Keep = ["Food"],
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            World = new Fx.StageWorld { Hour = 7 },
            Site = new Fx.StageSite
            {
                Width = 7,
                Height = 7,
                OffsetX = 6,
                Radius = 12,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Heavy, Fx.StageGround.Unroofed],
            },
            Rooms = [new Fx.StageRoom { Name = "hut", Rect = new(0, 0, 7, 7), Stuff = "WoodLog", Doors = [new(3, 0)], Roof = true }],
            Buildings = [.. Spots.Select((def, i) => new Fx.StageBuilding { Name = def, Def = def, Cells = [new(1 + (2 * i), 4)] })],
        },
        Serve = new ServeSpec { Families = ["clearance"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "shelter-retirement" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("the stage found no site");
        var hut = staged.Rooms["hut"];
        List<string> ours = [.. hut.Walls, .. hut.Doors, .. Spots.SelectMany(def => staged.Buildings[def].Ids)];
        await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
        {
            NextRoomId = 1,
            RetiredGround = [RegionRules.RectRegion(new Rectangle { X = site.X, Z = site.Z, Width = 7, Height = 7 })],
        }, cancellationToken).ConfigureAwait(false);
        _ = await s.ServeUntilAsync("retired-ground", s.TicksLeft, async ct =>
        {
            var standing = await s.BuildingsAsync("standing", new Observations.ListBuildingsRequest { Ids = [.. ours] }, ct).ConfigureAwait(false);
            s.Record("standing", string.Join(' ', standing.Select(b => b.Building.DefName)));
            return (string.Create(CultureInfo.InvariantCulture, $"{standing.Count} {string.Join(',', standing.Select(b => b.Building.Id))}"), standing.Count == 0);
        }, cancellationToken).ConfigureAwait(false);
    }
}
