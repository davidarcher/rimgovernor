using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using BridgeBuildingPreview = RimGovernor.Brain.Bridge.Contracts.BuildingPreview;

namespace RimGovernor.Brain.Host;

// Go building_preview.go, zone.go (PreviewZone), overlay.go and the placement_preview call with its reply checks
// (placement.go validatePlacementRequest, validatePlacementBatch and validateEvaluated).
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go's <c>PreviewBuilding</c>: binds a single native evaluation to the exact immutable action and authority
    /// generation. It neither acquires authority nor reserves resources.
    /// </summary>
    internal async Task<(BridgeBuildingPreview Preview, BridgeResult Receipt)> PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var (previews, receipt) = await PreviewBuildingsAsync([action], snapshot, cancellationToken).ConfigureAwait(false);
        return (previews[0], receipt);
    }

    /// <summary>
    /// Go's <c>PreviewBuildings</c>: evaluates every action in one native call per <see cref="BridgeConstants.PlacementBatchLimit"/>
    /// placements. Previews come back in action order, each bound to its own action; a failed row fails the whole read.
    /// Later chunks of an oversized sweep are separate calls, and each must observe the same native generation.
    /// </summary>
    internal Task<(IReadOnlyList<BridgeBuildingPreview> Previews, BridgeResult Receipt)> PreviewBuildingsAsync(IReadOnlyList<DomainAction> actions, GenerationSnapshot snapshot, CancellationToken cancellationToken) =>
        PlacementPreviewBuildingsAsync(actions, snapshot, false, cancellationToken);

    /// <summary>
    /// Go's <c>PreviewBuildingOverRock</c>: previews one action as though natural rock on its footprint were already
    /// mined: a planned building whose excavation the same plan orders first. Dispatch never previews this way; the
    /// executor re-previews plainly once the rock is gone.
    /// </summary>
    internal async Task<(BridgeBuildingPreview Preview, BridgeResult Receipt)> PreviewBuildingOverRockAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var (previews, receipt) = await PlacementPreviewBuildingsAsync([action], snapshot, true, cancellationToken).ConfigureAwait(false);
        return (previews[0], receipt);
    }

    private async Task<(IReadOnlyList<BridgeBuildingPreview> Previews, BridgeResult Receipt)> PlacementPreviewBuildingsAsync(IReadOnlyList<DomainAction> actions, GenerationSnapshot snapshot, bool overRock, CancellationToken cancellationToken)
    {
        if (!snapshot.IsValid || snapshot.Native.Value == 0)
        {
            throw BridgeFunctions.Contract("building preview snapshot");
        }
        if (actions.Count == 0)
        {
            throw BridgeFunctions.Contract("building preview actions");
        }
        var candidates = new List<PbPlacement.PlacementCandidate>(actions.Count);
        foreach (var action in actions)
        {
            var b = action.Building ?? throw BridgeFunctions.Contract("building action required");
            candidates.Add(new PbPlacement.PlacementCandidate
            {
                DefName = b.Definition,
                Stuff = b.Stuff,
                X = b.Cell.X,
                Z = b.Cell.Z,
                Rotation = PlacementReads.WireRotation(b.Rotation),
                IgnoreNaturalRock = overRock ? true : null,
            });
        }
        var output = new List<BridgeBuildingPreview>(actions.Count);
        var identity = new PbCommon.Identity { ColonyId = snapshot.Colony.Value, LoadToken = snapshot.Load.Value, MapId = snapshot.Map.Value };
        for (var start = 0; start < actions.Count; start += BridgeConstants.PlacementBatchLimit)
        {
            var end = Math.Min(start + BridgeConstants.PlacementBatchLimit, actions.Count);
            var request = new PbPlacement.PlacementRequest { Identity = identity, Placements = [.. candidates.Skip(start).Take(end - start)] };
            var batch = await PlacementPreviewsAsync(request, cancellationToken).ConfigureAwait(false);
            if (batch.Context?.NativeGeneration is null || batch.Context.NativeGeneration.GetValueOrDefault() != snapshot.Native.Value)
            {
                throw BridgeFunctions.Contract("building preview generation changed or unavailable");
            }
            for (var index = 0; index < batch.Results.Count; index++)
            {
                var row = batch.Results[index];
                if (row.Outcome?.Failure is { } rowFailure)
                {
                    throw Failure(rowFailure);
                }
                output.Add(PlacementReads.BuildingPreviewRow(actions[start + index], snapshot, new Tick(batch.Context.Tick.GetValueOrDefault()), row.Outcome?.Evaluated!));
            }
        }
        return (output, NoReceipt);
    }

    // Go PlacementPreviews: validates the request, makes the call and checks the batch the reply carries.
    private async Task<PbPlacement.PlacementBatch> PlacementPreviewsAsync(PbPlacement.PlacementRequest request, CancellationToken cancellationToken)
    {
        PlacementReads.ValidatePlacementRequest(request);
        var reply = await Facade.PreviewPlacementAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Batch is not { } batch)
        {
            throw BridgeFunctions.Contract("placement outcome missing");
        }
        PlacementReads.ValidatePlacementBatch(request, batch);
        return batch;
    }

    /// <summary>Go's <c>PreviewZone</c>: a planner's siting preview of one zone; native checks the ground live, so no map census token rides along.</summary>
    internal async Task<(PbOperations.ZonePreviewReply Reply, BridgeResult Receipt)> PreviewZoneAsync(PbCommon.Identity identity, ZoneCreate zone, CancellationToken cancellationToken)
    {
        if (!PlacementReads.Passes(() => BridgeFunctions.ValidateIdentity(identity)))
        {
            throw BridgeFunctions.Contract("invalid zone preview");
        }
        var request = new PbOperations.ZonePreviewRequest { Identity = identity, Zone = BridgeFunctions.ZoneConfiguration(zone) };
        var reply = await Facade.PreviewZoneAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply is null)
        {
            throw BridgeFunctions.Contract("unknown zone preview fields");
        }
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        // Accepted false is native refusing the ground itself (a littered or occupied cell), an evaluation the caller
        // moves past to its next candidate; a bad configuration arrives as a failure.
        var v = reply.Outcome?.Evaluated;
        if (v is null || v.Accepted is null || !PlacementReads.Passes(() => PlacementReads.BuildingContext(v.Context, identity, 0, false)))
        {
            throw BridgeFunctions.Contract("invalid zone preview evidence");
        }
        if (zone.Kind == Domain.ZoneKind.Stockpile && v.Accepted.GetValueOrDefault())
        {
            var rows = new List<CreatedZone>();
            for (var i = 0; i < v.Components.Count; i++)
            {
                var cells = new List<Cell>();
                foreach (var cell in v.Components[i].Cells)
                {
                    if (cell is null || cell.X is null || cell.Z is null)
                    {
                        throw BridgeFunctions.Contract("missing preview cell");
                    }
                    cells.Add(new Cell(cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault()));
                }
                rows.Add(new CreatedZone($"component_{i}", cells));
            }
            try
            {
                _ = new CreatedZones(zone, rows);
            }
            catch (DomainException error)
            {
                throw BridgeFunctions.Contract($"invalid stockpile preview components: {error.Message}");
            }
        }
        else if (v.Components.Count > 0)
        {
            throw BridgeFunctions.Contract("unexpected zone preview components");
        }
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go's <c>DrawOverlay</c>: replaces the named overlay layer. The native drops the layer's shapes and labels and
    /// draws the overlay's; <paramref name="enabled"/> false only removes the layer. Output only, never read back or persisted.
    /// </summary>
    internal async Task<(PbPresentation.OverlayApplied Applied, BridgeResult Receipt)> DrawOverlayAsync(PbCommon.Identity identity, string layerId, LayoutOverlay overlay, bool enabled, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        var request = new PbPresentation.OverlayRequest { Identity = identity, LayerId = layerId, Enabled = enabled };
        if (enabled)
        {
            var (shapes, labels) = PlacementReads.OverlayRequest(overlay);
            request = request with { Shapes = [.. shapes], Labels = [.. labels] };
        }
        var reply = await Facade.ApplyOverlayAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Applied is { } applied)
        {
            if (applied.Context is null)
            {
                throw BridgeFunctions.Contract("overlay context required");
            }
            return (applied, NoReceipt);
        }
        throw BridgeFunctions.Contract("overlay outcome required");
    }

    internal static partial class PlacementReads
    {
        /// <summary>Go's rotation table of <c>previewBuildings</c>: the cardinal rotations; anything else is unspecified and refused by the request check.</summary>
        internal static PbPlacement.Rotation WireRotation(Rotation rotation) =>
            rotation switch
            {
                Rotation.North => PbPlacement.Rotation.North,
                Rotation.East => PbPlacement.Rotation.East,
                Rotation.South => PbPlacement.Rotation.South,
                Rotation.West => PbPlacement.Rotation.West,
                _ => PbPlacement.Rotation.Unspecified,
            };

        /// <summary>Go's <c>validatePlacementRequest</c>.</summary>
        internal static void ValidatePlacementRequest(PbPlacement.PlacementRequest? request)
        {
            if (request is null || request.Placements.Count < 1 || request.Placements.Count > BridgeConstants.PlacementBatchLimit)
            {
                throw BridgeFunctions.Contract("placement count");
            }
            BridgeFunctions.ValidateIdentity(request.Identity);
            foreach (var v in request.Placements)
            {
                if (v is null
                    || v.DefName is null
                    || v.X is null
                    || v.Z is null
                    || v.Rotation is null
                    || v.X.GetValueOrDefault() < 0
                    || v.Z.GetValueOrDefault() < 0
                    || v.Rotation.GetValueOrDefault() < PbPlacement.Rotation.North
                    || v.Rotation.GetValueOrDefault() > PbPlacement.Rotation.All)
                {
                    throw BridgeFunctions.Contract("placement candidate presence/coordinates/rotation");
                }
                BridgeFunctions.ValidId(v.DefName);
                if (!string.IsNullOrEmpty(v.Stuff))
                {
                    BridgeFunctions.ValidId(v.Stuff);
                }
            }
        }

        /// <summary>Go's <c>validatePlacementBatch</c>.</summary>
        internal static void ValidatePlacementBatch(PbPlacement.PlacementRequest request, PbPlacement.PlacementBatch? batch)
        {
            if (batch is null)
            {
                throw BridgeFunctions.Contract("placement batch missing");
            }
            BridgeFunctions.ValidateContext(batch.Context);
            if (!BridgeFunctions.SameIdentity(batch.Context!.Identity, request.Identity))
            {
                throw BridgeFunctions.Contract("placement context mismatch");
            }
            if (batch.Results.Count != request.Placements.Count)
            {
                throw BridgeFunctions.Contract("placement result count");
            }
            for (var index = 0; index < batch.Results.Count; index++)
            {
                var row = batch.Results[index];
                if (row is null)
                {
                    throw BridgeFunctions.Contract("nil candidate result");
                }
                if (row.Outcome?.Failure is { } rowFailure)
                {
                    // A well-formed native refusal is a row outcome the caller turns into a failure; a malformed one is a contract error.
                    BridgeFunctions.ValidateFailure(rowFailure);
                }
                else if (row.Outcome?.Evaluated is { } evaluated)
                {
                    ValidateEvaluated(request.Placements[index], evaluated);
                }
                else
                {
                    throw BridgeFunctions.Contract("candidate outcome missing");
                }
            }
        }

        // Go validateEvaluated: one evaluated candidate's facts against the candidate that asked.
        private static void ValidateEvaluated(PbPlacement.PlacementCandidate request, PbPlacement.PlacementEvaluated? v)
        {
            if (v is null
                || v.CanPlace is null
                || v.MadeFromStuff is null
                || v.Passability is null
                || v.IsDoor is null
                || v.ResearchFinished is null
                || v.BuildableByPlayer is null
                || (int)v.Passability.GetValueOrDefault() < 1
                || (int)v.Passability.GetValueOrDefault() > 3)
            {
                throw BridgeFunctions.Contract("evaluated facts missing");
            }
            var costs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cost in v.CostList)
            {
                if (cost is null || cost.DefName is null || cost.Count is null || cost.Count.GetValueOrDefault() < 0 || !BridgeFunctions.IsValidId(cost.DefName) || !costs.Add(cost.DefName))
                {
                    throw BridgeFunctions.Contract("invalid cost");
                }
            }
            if (v.Materials is null)
            {
                throw BridgeFunctions.Contract("materials missing");
            }
            if (v.Materials.Availability?.Known is { } known)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in known.Rows)
                {
                    if (row is null || row.DefName is null || !BridgeFunctions.IsValidId(row.DefName) || !seen.Add(row.DefName) || (row.Available is not null && row.Available.GetValueOrDefault() < 0))
                    {
                        throw BridgeFunctions.Contract("invalid material stock");
                    }
                }
            }
            else if (v.Materials.Availability?.Unavailable is { } unavailable)
            {
                BridgeFunctions.ValidateUnavailable(unavailable);
            }
            else
            {
                throw BridgeFunctions.Contract("materials availability missing");
            }
            var want = request.Rotation.GetValueOrDefault() == PbPlacement.Rotation.All ? 4 : 1;
            if (v.Rotations.Count != want)
            {
                throw BridgeFunctions.Contract("rotation result count");
            }
            var rotations = new HashSet<PbPlacement.Rotation>();
            foreach (var rotation in v.Rotations)
            {
                if (rotation is null
                    || rotation.Rotation is null
                    || rotation.Accepted is null
                    || rotation.Rotation.GetValueOrDefault() < PbPlacement.Rotation.North
                    || rotation.Rotation.GetValueOrDefault() > PbPlacement.Rotation.West
                    || !rotations.Add(rotation.Rotation.GetValueOrDefault())
                    || !BridgeFunctions.Diagnostic(rotation.Reason is not null, rotation.Reason ?? ""))
                {
                    throw BridgeFunctions.Contract("rotation facts invalid");
                }
                if (want == 1 && rotation.Rotation.GetValueOrDefault() != request.Rotation.GetValueOrDefault())
                {
                    throw BridgeFunctions.Contract("rotation mismatch");
                }
                if (rotation.OccupiedCells.Count == 0)
                {
                    throw BridgeFunctions.Contract("geometry completeness/limit");
                }
                var cells = new HashSet<(int, int)>();
                foreach (var cell in rotation.OccupiedCells)
                {
                    ValidCell(cell);
                    if (!cells.Add((cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault())))
                    {
                        throw BridgeFunctions.Contract("duplicate footprint cell");
                    }
                }
                foreach (var block in rotation.BlockingThings)
                {
                    if (block is null
                        || block.Category is null
                        || !BridgeFunctions.IsValidId(block.Category)
                        || block.IsBlueprint is null
                        || block.IsFrame is null
                        || block.WouldBeWiped is null
                        || block.FrameWouldBeCancelled is null)
                    {
                        throw BridgeFunctions.Contract("blocker facts missing");
                    }
                    if (block.DefName is not null && !BridgeFunctions.IsValidId(block.DefName))
                    {
                        throw BridgeFunctions.Contract("invalid blocker definition");
                    }
                }
            }
        }

        /// <summary>Go's <c>buildingPreviewRow</c>: binds one evaluated placement row to its action.</summary>
        internal static BridgeBuildingPreview BuildingPreviewRow(DomainAction action, GenerationSnapshot snapshot, Tick tick, PbPlacement.PlacementEvaluated evaluation)
        {
            var b = action.Building!;
            var orientation = evaluation.Rotations[0];
            var canPlace = evaluation.CanPlace.GetValueOrDefault()
                && orientation.Accepted.GetValueOrDefault()
                && evaluation.ResearchFinished.GetValueOrDefault()
                && evaluation.BuildableByPlayer.GetValueOrDefault();
            var watch = orientation.WatchCellsAccessible is { } accessible ? Fact.Known(accessible) : Fact.Unknown<bool>();
            var wind = orientation.WindBlockedCells is { } blocked ? Fact.Known((int)Math.Min(blocked, 4096u)) : Fact.Unknown<int>();
            var safe = true;
            var nativeWorkPending = false;
            var blockers = new List<PlacementBlocker>();
            foreach (var blocker in orientation.BlockingThings)
            {
                var replaceConduit = b.Definition == "HiddenConduit"
                    && (blocker.DefName ?? "") == "PowerConduit"
                    && (blocker.Category ?? "") == "Building"
                    && !blocker.IsBlueprint.GetValueOrDefault()
                    && !blocker.IsFrame.GetValueOrDefault()
                    && !blocker.FrameWouldBeCancelled.GetValueOrDefault();
                if ((blocker.WouldBeWiped.GetValueOrDefault() && !replaceConduit) || blocker.FrameWouldBeCancelled.GetValueOrDefault() || blocker.IsBlueprint.GetValueOrDefault() || blocker.IsFrame.GetValueOrDefault())
                {
                    safe = false;
                }
                nativeWorkPending = nativeWorkPending || blocker.IsBlueprint.GetValueOrDefault() || blocker.IsFrame.GetValueOrDefault();
                blockers.Add(new PlacementBlocker
                {
                    Category = blocker.Category ?? "",
                    DefName = blocker.DefName ?? "",
                    Wiped = blocker.WouldBeWiped.GetValueOrDefault(),
                    Blueprint = blocker.IsBlueprint.GetValueOrDefault(),
                    Frame = blocker.IsFrame.GetValueOrDefault(),
                    Cancelled = blocker.FrameWouldBeCancelled.GetValueOrDefault(),
                });
            }

            // Even a zero-cost evaluation cannot turn an unreadable material scan into authorization. The next
            // complete preview can release this unknown hold.
            var nativeConstruction = false;
            var safeToPlace = Fact.Unknown<bool>();
            var stocks = new List<Stock>();
            if (evaluation.Materials?.Availability?.Known is { } known)
            {
                nativeConstruction = true;
                safeToPlace = Fact.Known(safe);
                foreach (var material in known.Rows)
                {
                    stocks.Add(new Stock(new Resource(material.DefName ?? ""), material.Available is { } available ? Fact.Known((long)available) : Fact.Unknown<long>()));
                }
            }
            else if (!safe)
            {
                safeToPlace = Fact.Known(false);
            }
            var costs = new List<Amount>(evaluation.CostList.Count);
            foreach (var cost in evaluation.CostList)
            {
                costs.Add(new Amount(new Resource(cost.DefName ?? ""), cost.Count.GetValueOrDefault()));
            }

            // Claim occupied and interaction cells so later placements cannot block the worker's access to this building.
            var footprint = new List<Cell>(orientation.OccupiedCells.Count + orientation.InteractionCells.Count);
            var seenCells = new HashSet<Cell>();
            foreach (var cell in orientation.OccupiedCells.Concat(orientation.InteractionCells))
            {
                var claim = new Cell(cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault());
                if (seenCells.Add(claim))
                {
                    footprint.Add(claim);
                }
            }
            return new BridgeBuildingPreview
            {
                Preview = new Preview
                {
                    Action = action,
                    Snapshot = snapshot,
                    Tick = tick,
                    CanPlace = Fact.Known(canPlace),
                    MadeFromStuff = Fact.Known(evaluation.MadeFromStuff.GetValueOrDefault()),
                    WatchCellsAccessible = watch,
                    WindBlockedCells = wind,
                    SafeToPlace = safeToPlace,
                    Blockers = EquatableArray.From(blockers),
                    Costs = Fact.Known(EquatableArray.From(costs)),
                    Footprint = Fact.Known(EquatableArray.From(footprint)),
                },
                Stock = new StockObservation
                {
                    Snapshot = snapshot,
                    Tick = tick,
                    NativeConstruction = nativeConstruction,
                    Values = EquatableArray.From(stocks),
                },
                NativeWorkPending = nativeWorkPending,
                Reason = orientation.Reason ?? "",
            };
        }

        /// <summary>Go's <c>overlayRequest</c>: the wire shapes and labels of a layout overlay.</summary>
        internal static (List<PbPresentation.OverlayShape> Shapes, List<PbPresentation.OverlayLabel> Labels) OverlayRequest(LayoutOverlay overlay)
        {
            var shapes = new List<PbPresentation.OverlayShape>(overlay.Layers.Count);
            foreach (var l in overlay.Layers)
            {
                var rects = new List<PbPresentation.MapRect>(l.Rects.Count);
                foreach (var r in l.Rects)
                {
                    rects.Add(new PbPresentation.MapRect { MinX = r.X, MinZ = r.Z, MaxX = r.X + r.Width - 1, MaxZ = r.Z + r.Height - 1 });
                }
                var runs = new List<PbPresentation.OverlayRun>(l.Runs.Count);
                foreach (var r in l.Runs)
                {
                    runs.Add(new PbPresentation.OverlayRun { X = r.X, Z = r.Z, Length = r.Length });
                }
                var lines = new List<PbPresentation.OverlayLine>(l.Lines.Count);
                foreach (var ln in l.Lines)
                {
                    lines.Add(new PbPresentation.OverlayLine
                    {
                        From = new PbCommon.Cell { X = ln.From.X, Z = ln.From.Z },
                        To = new PbCommon.Cell { X = ln.To.X, Z = ln.To.Z },
                        Arrow = ln.Arrow,
                    });
                }
                shapes.Add(new PbPresentation.OverlayShape
                {
                    Color = new PbPresentation.OverlayColor { R = l.Color.R, G = l.Color.G, B = l.Color.B, A = l.Color.A },
                    Style = l.Style == OverlayStyle.Outline ? PbPresentation.OverlayStyle.Outline : PbPresentation.OverlayStyle.Fill,
                    Rects = [.. rects],
                    Runs = [.. runs],
                    Lines = [.. lines],
                });
            }
            var labels = new List<PbPresentation.OverlayLabel>(overlay.Labels.Count);
            foreach (var l in overlay.Labels)
            {
                labels.Add(new PbPresentation.OverlayLabel { Text = l.Text, Cell = new PbCommon.Cell { X = l.Cell.X, Z = l.Cell.Z } });
            }
            return (shapes, labels);
        }
    }
}
