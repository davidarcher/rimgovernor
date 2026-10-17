using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Placement = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>apply/refusal</c> (Go <c>refusal.go</c>): executes one routine write per kind after the fixture has moved the world
/// under the target it was read for, and asserts the refusal names the fact that moved (the reason strings in
/// docs/developers/contracts/action-contracts.md). Every refusal is the action's refused result, never a receipt.
/// test/apply_refusal_prepare stages the targets and test/apply_refusal_move moves the world under one (a wall on a cell,
/// a deleted zone, a drafted colonist, a hunt designation, a destroyed plant pot), which the primitives cannot express.
/// </summary>
internal static class ApplyRefusal
{
    private const string PrepareTool = "test/apply_refusal_prepare";
    private const string MoveTool = "test/apply_refusal_move";

    private static readonly FixtureOp<NoArgs, Fx.ApplyRefusalPrepared> Prepare = new(
        PrepareTool,
        _ => [],
        ServedOps.Succeeded<Fx.ApplyRefusalPrepared>(PrepareTool, r => r is { Success: true, ZoneCells.Count: > 0, FreeCells.Count: > 0, BuildCell: not null }));

    private static readonly FixtureOp<(string Action, string Id, Common.Cell? Cell), Fx.FixtureDone> Move = new(
        MoveTool,
        m => ServedOps.Args(("action", m.Action), ("id", m.Id), ("x", m.Cell?.X ?? -1), ("z", m.Cell?.Z ?? -1)),
        ServedOps.Done(MoveTool));

    internal static TypedCase Case() => new()
    {
        Name = "apply/refusal",
        Scope = "Apply-time precondition refusals (#242, #252): for zone cell edit, stockpile patch, zone creation, "
            + "haul, work settings, bills, build, tame and grower crop, a write planned against the read world is executed "
            + "after the fixture moved the world and is refused with the documented reason naming the moved fact.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Ops = [PrepareTool, MoveTool],
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("prepare", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("prepared", ServedOps.Json(prepared));
        static Common.Cell Cell(Fx.FixtureCell c) => new() { X = c.X, Z = c.Z };
        var free = Cell(prepared.FreeCells[0]);
        var zoneCell = Cell(prepared.ZoneCells[0]);
        var buildCell = Cell(prepared.BuildCell!);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        Task MoveAsync(string label, string action, string id = "", Common.Cell? cell = null) =>
            s.FixtureAsync(label, Move, (action, id, cell), cancellationToken);

        async Task RefusedAsync(string label, Operations.ActionIntent intent, Common.FailureCode code, string reason)
        {
            var refusal = await TypedApply.RefusedAsync(s, "refusal-" + label, intent, cancellationToken).ConfigureAwait(false);
            if (refusal.Code != code || refusal.Reason?.Contains(reason, StringComparison.Ordinal) != true)
            {
                throw new AcceptanceException($"{label}: expected {code} \"{reason}\", got {refusal}");
            }

            s.Record(label.Replace('-', '_'), refusal.Reason);
        }

        // Build: the open build cell is walled over after the placement preview accepted it; the building intent is
        // refused with the re-planned placement's own reason. A Campfire, not a wall: the fixture's player wall would
        // otherwise be applied as it stands.
        var placement = new Placement.PlacementCandidate { DefName = "Campfire", X = buildCell.X, Z = buildCell.Z, Rotation = Placement.Rotation.North };

        async Task<bool> CanPlaceAsync(string label)
        {
            var reply = await s.CallAsync("placement-" + label, NativeOps.PreviewPlacement, new Placement.PlacementRequest { Identity = s.Identity, Placements = [placement] }, cancellationToken).ConfigureAwait(false);
            return reply.Outcome?.Batch?.Results.ToList() is [{ Outcome.Evaluated: { } evaluated }]
                ? evaluated.CanPlace == true
                : throw new AcceptanceException($"{label}: expected one evaluated placement, got {reply.Outcome}");
        }

        if (!await CanPlaceAsync("build-open").ConfigureAwait(false))
        {
            throw new AcceptanceException("build-open: expected the open build cell to be placeable");
        }

        await MoveAsync("fill-build-cell", "fill_cell", cell: buildCell).ConfigureAwait(false);
        if (await CanPlaceAsync("build-blocked").ConfigureAwait(false))
        {
            throw new AcceptanceException("build-blocked: expected the walled build cell to be unplaceable");
        }

        var build = await TypedApply.RefusedAsync(s, "apply-refusal-build", new Operations.BuildingIntent { Placement = placement }, cancellationToken).ConfigureAwait(false);
        if (build.Code != Common.FailureCode.InvalidRequest || string.IsNullOrEmpty(build.Reason))
        {
            throw new AcceptanceException($"build: expected an INVALID_REQUEST refusal with a reason, got {build}");
        }

        s.Record("build", build.Reason);

        // Zone cell edit and zone creation: the free roofed cell is walled over after the read.
        var zone = new Common.Ref { Id = prepared.ZoneId };
        await MoveAsync("fill-free-cell", "fill_cell", cell: free).ConfigureAwait(false);
        await RefusedAsync(
            "zone-edit-add",
            new Operations.ZoneIntent { Zone = zone, AddCells = new Operations.Cells { Selection = new Operations.CellList { Cells = [free] } } },
            Common.FailureCode.InvalidRequest,
            $"Zone cell edit refused: cell ({free.X}, {free.Z}) is not free zoneable ground").ConfigureAwait(false);
        await RefusedAsync(
            "zone-create",
            new Operations.ZoneIntent
            {
                Label = "RimGovernor apply refusal",
                Kind = Operations.ZoneType.Stockpile,
                AddCells = new Operations.Cells { Selection = new Operations.Rectangle { Origin = free, Width = 1, Height = 1 } },
                Stockpile = new Operations.StockpileSettings { Priority = Operations.StoragePriority.Normal, Preset = Operations.FilterPreset.Nothing },
            },
            Common.FailureCode.InvalidRequest,
            "Rectangle has no free vanilla-zoneable stockpile cells").ConfigureAwait(false);

        // Stockpile patch and a cell removal: the zone is gone. (A deletion of the gone zone is its effect already
        // holding, so it applies.)
        await MoveAsync("delete-zone", "delete_zone", prepared.ZoneId).ConfigureAwait(false);
        await RefusedAsync(
            "stockpile-patch",
            new Operations.ZoneIntent { Zone = zone, Stockpile = new Operations.StockpileSettings { Priority = Operations.StoragePriority.Important } },
            Common.FailureCode.NotFound,
            "Stockpile patch refused: the exact stockpile zone or storage building no longer exists on this map").ConfigureAwait(false);
        await RefusedAsync(
            "zone-edit-remove",
            new Operations.ZoneIntent { Zone = zone, RemoveCells = new Operations.Cells { Selection = new Operations.CellList { Cells = [zoneCell] } } },
            Common.FailureCode.NotFound,
            "Zone cell edit refused: the exact zone no longer exists on this map").ConfigureAwait(false);

        // Haul and work settings: the colonist was drafted after the read.
        await MoveAsync("draft-pawn", "draft_pawn", prepared.PawnId).ConfigureAwait(false);
        await RefusedAsync(
            "haul",
            new Operations.HaulIntent { PawnId = prepared.PawnId, ThingId = prepared.HaulItemId },
            Common.FailureCode.InvalidRequest,
            "Haul refused: the pawn is drafted").ConfigureAwait(false);
        await RefusedAsync(
            "work-settings",
            new Operations.WorkSettingsIntent { PawnId = prepared.PawnId, Work = [new Operations.WorkPriority { WorkTypeDef = "Hauling", Priority = 1 }] },
            Common.FailureCode.InvalidRequest,
            "Work settings refused: the pawn is drafted").ConfigureAwait(false);
        await MoveAsync("undraft-pawn", "undraft_pawn", prepared.PawnId).ConfigureAwait(false);

        // Bills: a bill on a bench that is not on the map.
        await RefusedAsync(
            "bill",
            new Operations.ProductionBillIntent
            {
                BenchId = "Building_missing_bench",
                RecipeDef = "ButcherCorpseFlesh",
                Settings = new Operations.BillSettings
                {
                    RepeatMode = Operations.RepeatMode.Forever,
                    Suspended = false,
                    IngredientSearchRadius = 40,
                    Store = new Operations.BillStore { Destination = new Operations.BillStoreDestination.ModeCase(Operations.StoreMode.DropOnFloor) },
                    CorpseClass = Common.CorpseClass.Animal,
                },
            },
            Common.FailureCode.NotFound,
            "Production bill refused: bench Building_missing_bench is not a loaded bill giver").ConfigureAwait(false);

        // Tame: the hare was designated for hunting after the read; native tame eligibility refuses it. (A tame
        // designation would apply as it stands.)
        await MoveAsync("designate-tame-prey", "designate_hunt", prepared.TameId).ConfigureAwait(false);
        await RefusedAsync(
            "tame",
            new Operations.HusbandryIntent { AnimalId = prepared.TameId, Order = Operations.HusbandryOrder.Tame },
            Common.FailureCode.InvalidRequest,
            "Husbandry refused: native tame eligibility refused the animal or it is designated for hunting").ConfigureAwait(false);

        // Grower crop: the plant pot is gone.
        await MoveAsync("destroy-grower", "destroy_thing", prepared.GrowerId).ConfigureAwait(false);
        await RefusedAsync(
            "grower-crop",
            new Operations.BuildingPatchIntent { ThingId = prepared.GrowerId, Change = new Operations.BuildingPatchIntentChange.PlantDefCase(prepared.GrowerCrop) },
            Common.FailureCode.NotFound,
            "Exact plant grower is unavailable.").ConfigureAwait(false);
    }
}
