using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>clearance/retired-blueprints-cancelled</c>: a started room a replan drops leaves its blueprints to clearance
/// (#3742). A wall blueprint stands on the ground the injected layout plan holds as <c>LayoutPlan.RetiredGround</c>;
/// under the served clearance family the blueprint is cancelled (THING_DESIGNATION_CANCEL through
/// <c>Designator_Cancel</c>) and the case ends with no blueprint or frame of ours on the cell, read through the
/// buildings read. The blueprint asks for plasteel, which the lab start does not stock, so no pawn turns it into a
/// frame first. Choosing the cancel is policy (RetiredBlueprintsTests); this case owns the native cancel. The plan also
/// holds one unbuilt storage room east of the retired ground: a plan with no room does not validate (#3681).
/// </summary>
internal static class ClearanceRetiredBlueprints
{
    private const string Blueprint = "blueprint";

    internal static TypedCase Case() => new()
    {
        Name = "clearance/retired-blueprints-cancelled",
        Scope = "Issue #3742: a wall blueprint on the injected plan's RetiredGround is cancelled by the served clearance "
            + "family (THING_DESIGNATION_CANCEL), leaving no blueprint or frame of the colony's on its cell, read through the buildings read.",
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
                Width = 13,
                Height = 7,
                OffsetX = 6,
                Radius = 12,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Heavy, Fx.StageGround.Unroofed],
            },
            Buildings = [new Fx.StageBuilding { Name = Blueprint, Def = "Wall", Stuff = "Plasteel", Cells = [new(3, 0)], State = Fx.StageBuildState.Blueprint }],
        },
        Serve = new ServeSpec { Families = ["clearance"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "clearance-retired-blueprints" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("the stage found no site");
        var blueprint = staged.Buildings[Blueprint];
        var cell = blueprint.Cells[0];
        await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
        {
            NextRoomId = 2,
            Rooms =
            [
                new PlannedRoom
                {
                    Id = 1,
                    Role = PlannedRole.Storage,
                    Region = RegionRules.RectRegion(new Rectangle { X = site.X + 9, Z = site.Z + 2, Width = 3, Height = 3 }),
                    Door = new Cell(site.X + 8, site.Z + 3),
                    DoorRot = Rotation.West,
                },
            ],
            RetiredGround = [RegionRules.RectRegion(new Rectangle { X = site.X, Z = site.Z, Width = 7, Height = 7 })],
        }, cancellationToken).ConfigureAwait(false);
        _ = await s.ServeUntilAsync("retired-blueprints", s.TicksLeft, async ct =>
        {
            var pending = await s.BuildingsAsync("pending", new Observations.ListBuildingsRequest
            {
                Statuses = [Observations.BuildingStatus.Blueprint, Observations.BuildingStatus.Frame],
            }, ct).ConfigureAwait(false);
            var onCell = pending.Where(b => b.Building.Place is Observations.EntityPlace.OnMap { Cell: var p } && p.X == cell.X && p.Z == cell.Z).ToList();
            s.Record("on_cell", string.Join(' ', onCell.Select(b => $"{b.Building.Id}:{b.Status}")));
            var gone = onCell.Count == 0 && !pending.Any(b => blueprint.Ids.Contains(b.Building.Id));
            return (string.Create(CultureInfo.InvariantCulture, $"{onCell.Count} {string.Join(',', onCell.Select(b => b.Building.Id))}"), gone);
        }, cancellationToken).ConfigureAwait(false);
    }
}
