using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

// PreviewZone, the overlay reads and the placement_preview call with its request check.
public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Binds a single native evaluation to the exact immutable action and authority
    /// generation. It neither acquires authority nor reserves resources.
    /// </summary>
    internal async Task<BridgeBuildingPreview> PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var previews = await PreviewBuildingsAsync([action], snapshot, cancellationToken).ConfigureAwait(false);
        return previews[0];
    }

    /// <summary>
    /// Evaluates every action in one native call per <see cref="CombatConstants.PlacementBatchLimit"/>
    /// placements. Previews come back in action order, each bound to its own action; a failed row fails the whole read.
    /// Later chunks of an oversized sweep are separate calls, and each must observe the same native generation.
    /// </summary>
    internal Task<IReadOnlyList<BridgeBuildingPreview>> PreviewBuildingsAsync(IReadOnlyList<DomainAction> actions, GenerationSnapshot snapshot, CancellationToken cancellationToken) =>
        PlacementPreviewBuildingsAsync(actions, snapshot, false, cancellationToken);

    /// <summary>
    /// Previews one action as though natural rock on its footprint were already
    /// mined: a planned building whose excavation the same plan orders first. Dispatch never previews this way; the
    /// executor re-previews plainly once the rock is gone.
    /// </summary>
    internal async Task<BridgeBuildingPreview> PreviewBuildingOverRockAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var previews = await PlacementPreviewBuildingsAsync([action], snapshot, true, cancellationToken).ConfigureAwait(false);
        return previews[0];
    }

    private async Task<IReadOnlyList<BridgeBuildingPreview>> PlacementPreviewBuildingsAsync(IReadOnlyList<DomainAction> actions, GenerationSnapshot snapshot, bool overRock, CancellationToken cancellationToken)
    {
        if (!snapshot.IsValid || snapshot.Native.Value == 0)
        {
            throw BridgeContractException.Contract("building preview snapshot");
        }
        if (actions.Count == 0)
        {
            throw BridgeContractException.Contract("building preview actions");
        }
        var candidates = new List<PbPlacement.PlacementCandidate>(actions.Count);
        foreach (var action in actions)
        {
            var b = action.Building ?? throw BridgeContractException.Contract("building action required");
            candidates.Add(new PbPlacement.PlacementCandidate
            {
                DefName = b.Definition,
                Stuff = b.Stuff,
                X = b.Cell.X,
                Z = b.Cell.Z,
                Rotation = PlacementRequests.WireRotation(b.Rotation),
                IgnoreNaturalRock = overRock,
            });
        }
        var output = new List<BridgeBuildingPreview>(actions.Count);
        var identity = new PbCommon.Identity { ColonyId = snapshot.Colony.Value, LoadToken = snapshot.Load.Value, MapId = snapshot.Map.Value };
        for (var start = 0; start < actions.Count; start += CombatConstants.PlacementBatchLimit)
        {
            var end = Math.Min(start + CombatConstants.PlacementBatchLimit, actions.Count);
            var request = new PbPlacement.PlacementRequest { Identity = identity, Placements = [.. candidates.Skip(start).Take(end - start)] };
            var batch = await PlacementPreviewsAsync(request, cancellationToken).ConfigureAwait(false);
            var context = batch.Context!;
            if (!context.NativeGeneration.IsAt(snapshot.Native.Value))
            {
                throw BridgeContractException.Contract("building preview generation changed or unavailable");
            }
            for (var index = 0; index < batch.Results.Count; index++)
            {
                var row = batch.Results[index];
                if (row.Outcome.Failure is { } rowFailure)
                {
                    throw Failure(rowFailure);
                }
                output.Add(PlacementRequests.BuildingPreviewRow(actions[start + index], snapshot, new Tick(context.Tick), row.Outcome.Evaluated!));
            }
        }
        return output;
    }

    // Validates the request, makes the call and returns the batch the reply carries.
    private async Task<PbPlacement.PlacementBatch> PlacementPreviewsAsync(PbPlacement.PlacementRequest request, CancellationToken cancellationToken)
    {
        PlacementRequests.ValidatePlacementRequest(request);
        var reply = await Facade.PreviewPlacementAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        return reply.Outcome.Batch!;
    }

    /// <summary>A planner's siting preview of one zone; native checks the ground live, so no map census token rides along.</summary>
    internal async Task<PbOperations.ZonePreviewReply> PreviewZoneAsync(PbCommon.Identity identity, ZoneCreate zone, CancellationToken cancellationToken)
    {
        var request = new PbOperations.ZonePreviewRequest { Identity = identity, Zone = Zones.ZoneConfiguration(zone) };
        var reply = await Facade.PreviewZoneAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        // Accepted false is native refusing the ground itself (a littered or occupied cell), an evaluation the caller
        // moves past to its next candidate; a bad configuration arrives as a failure.
        return reply;
    }

    /// <summary>
    /// Replaces the named overlay layer. The native drops the layer's shapes and labels and
    /// draws the overlay's; <paramref name="enabled"/> false only removes the layer. Output only, never read back or persisted.
    /// </summary>
    internal async Task DrawOverlayAsync(PbCommon.Identity identity, string layerId, LayoutOverlay overlay, bool enabled, CancellationToken cancellationToken)
    {
        var shapes = new List<PbPresentation.OverlayShape>();
        var labels = new List<PbPresentation.OverlayLabel>();
        if (enabled)
        {
            (shapes, labels) = PlacementRequests.OverlayRequest(overlay);
        }

        var request = new PbPresentation.OverlayRequest { Identity = identity, LayerId = layerId, Enabled = enabled, Shapes = [.. shapes], Labels = [.. labels] };
        var reply = await Facade.ApplyOverlayAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
    }

    internal static class PlacementRequests
    {
        /// <summary>The cardinal rotations; a previewed action names one.</summary>
        internal static PbPlacement.Rotation WireRotation(Rotation rotation) => Cardinals[(int)rotation];

        private static readonly PbPlacement.Rotation[] Cardinals = [PbPlacement.Rotation.North, PbPlacement.Rotation.East, PbPlacement.Rotation.South, PbPlacement.Rotation.West];

        internal static void ValidatePlacementRequest(PbPlacement.PlacementRequest request)
        {
            if (request.Placements.Count < 1 || request.Placements.Count > CombatConstants.PlacementBatchLimit)
            {
                throw BridgeContractException.Contract("placement count");
            }
            foreach (var v in request.Placements)
            {
                if (v.X < 0 || v.Z < 0)
                {
                    throw BridgeContractException.Contract("placement candidate coordinates");
                }
            }
        }

        /// <summary>Binds one evaluated placement row to its action.</summary>
        internal static BridgeBuildingPreview BuildingPreviewRow(DomainAction action, GenerationSnapshot snapshot, Tick tick, PbPlacement.PlacementEvaluated evaluation)
        {
            var b = action.Building!;
            var orientation = evaluation.Rotations[0];
            var canPlace = evaluation.CanPlace
                && orientation.Accepted
                && evaluation.ResearchFinished
                && evaluation.BuildableByPlayer;
            var safe = true;
            var nativeWorkPending = false;
            foreach (var blocker in orientation.BlockingThings)
            {
                var replaceConduit = b.Definition == "HiddenConduit"
                    && blocker.DefName == "PowerConduit"
                    && blocker.Category == "Building"
                    && !blocker.IsBlueprint
                    && !blocker.IsFrame
                    && !blocker.FrameWouldBeCancelled;
                if ((blocker.WouldBeWiped && !replaceConduit) || blocker.FrameWouldBeCancelled || blocker.IsBlueprint || blocker.IsFrame)
                {
                    safe = false;
                }
                nativeWorkPending = nativeWorkPending || blocker.IsBlueprint || blocker.IsFrame;
            }

            var stocks = new List<Stock>();
            foreach (var material in evaluation.Materials.Rows)
            {
                stocks.Add(new Stock(new Resource(material.DefName), material.Available));
            }
            var costs = new List<Amount>(evaluation.CostList.Count);
            foreach (var cost in evaluation.CostList)
            {
                costs.Add(new Amount(new Resource(cost.DefName), cost.Count));
            }

            // Claim occupied and interaction cells so later placements cannot block the worker's access to this building.
            var footprint = new List<Cell>(orientation.OccupiedCells.Count + orientation.InteractionCells.Count);
            var seenCells = new HashSet<Cell>();
            foreach (var cell in orientation.OccupiedCells.Concat(orientation.InteractionCells))
            {
                var claim = new Cell(cell.X, cell.Z);
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
                    CanPlace = canPlace,
                    Source = new PreviewSource.Native(evaluation, 0),
                    SafeToPlace = safe,
                    Costs = EquatableArray.From(costs),
                    Footprint = EquatableArray.From(footprint),
                },
                Stock = new StockObservation
                {
                    Snapshot = snapshot,
                    Tick = tick,
                    Values = EquatableArray.From(stocks),
                },
                NativeWorkPending = nativeWorkPending,
                Reason = orientation.Reason,
            };
        }

        /// <summary>The wire shapes and labels of a layout overlay.</summary>
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
                        From = new RimGovernor.Brain.Domain.Cell { X = ln.From.X, Z = ln.From.Z },
                        To = new RimGovernor.Brain.Domain.Cell { X = ln.To.X, Z = ln.To.Z },
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
                labels.Add(new PbPresentation.OverlayLabel { Text = l.Text, Cell = new RimGovernor.Brain.Domain.Cell { X = l.Cell.X, Z = l.Cell.Z } });
            }
            return (shapes, labels);
        }
    }
}
