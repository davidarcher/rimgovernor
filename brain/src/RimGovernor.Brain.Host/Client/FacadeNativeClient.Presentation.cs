using System.Text;
using RimGovernor.Brain.Bridge.Contracts;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;

namespace RimGovernor.Brain.Host;

// Go presentation_reads.go (ReadCamera, ReadSelection, ReadColonistRoster, ReadRenderState and their validators),
// presentation_media.go (PresentationMedia.DemandRendering) and notifications.go (ReadNotifications). A failure arm
// throws what Failure builds, as Go returned it as an error alongside the reply. The wire walk of Go's
// notificationsWire (unknown fields, UTF-8, non-finite numbers, protojson encodability) has no counterpart here: a
// record carries no unknown fields, a string is UTF-16, and PresentationRoutes rejects a non-finite number in the
// reply before it serializes anything.
internal sealed partial class FacadeNativeClient
{
    private const int PresentationListingLimit = 4096;

    /// <summary>Go <c>Client.ReadCamera</c>: the camera, observation only.</summary>
    internal async Task<PbPresentation.CameraReply> ReadCameraAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PresentationRequest(request.Identity);
        var reply = await Facade.ReadCameraAsync(request, cancellationToken).ConfigureAwait(false);
        switch (reply.Outcome)
        {
            case PbPresentation.CameraReplyOutcome.FailureCase { Value: var failure }:
                throw Failure(failure);
            case PbPresentation.CameraReplyOutcome.CameraCase { Value: var camera }:
                PresentationValidateCamera(camera, request.Identity);
                return reply;
            default:
                throw Contract("camera outcome required");
        }
    }

    /// <summary>Go <c>Client.ReadSelection</c>: the selection, observation only.</summary>
    internal async Task<PbPresentation.SelectionReply> ReadSelectionAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PresentationRequest(request.Identity);
        var reply = await Facade.ReadSelectionAsync(request, cancellationToken).ConfigureAwait(false);
        switch (reply.Outcome)
        {
            case PbPresentation.SelectionReplyOutcome.FailureCase { Value: var failure }:
                throw Failure(failure);
            case PbPresentation.SelectionReplyOutcome.SelectionCase { Value: var selection }:
                PresentationValidateSelection(selection, request.Identity);
                return reply;
            default:
                throw Contract("selection outcome required");
        }
    }

    /// <summary>Go <c>Client.ReadColonistRoster</c>: the colonist roster, each with its pawn table row when the dossier is asked for.</summary>
    internal async Task<PbPresentation.ColonistRosterReply> ReadColonistRosterAsync(PbPresentation.ColonistRosterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PresentationRequest(request.Identity);
        if (request.CurrentMapOnly is null)
        {
            throw Contract("currentMapOnly presence required");
        }

        var reply = await Facade.ReadColonistsAsync(request, cancellationToken).ConfigureAwait(false);
        switch (reply.Outcome)
        {
            case PbPresentation.ColonistRosterReplyOutcome.FailureCase { Value: var failure }:
                throw Failure(failure);
            case PbPresentation.ColonistRosterReplyOutcome.RosterCase { Value: var roster }:
                PresentationValidateRoster(roster, request);
                if (request.IncludeDossier.GetValueOrDefault())
                {
                    return reply with { Outcome = roster with { Colonists = await JoinDossiersAsync(roster, request.Identity!, cancellationToken).ConfigureAwait(false) } };
                }

                return reply;
            default:
                throw Contract("roster outcome required");
        }
    }

    /// <summary>
    /// Go <c>joinDossiers</c>: attaches each colonist's pawn table row without its settings and animal detail. A colonist
    /// the table does not hold leaves the roster unavailable until a later frame.
    /// </summary>
    private async Task<RimGovernor.Brain.Domain.EquatableArray<PbPresentation.ColonistReference>> JoinDossiersAsync(PbPresentation.ColonistRoster roster, PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var pawns = await FramePawnsAsync(identity, cancellationToken).ConfigureAwait(false);
        var joined = new List<PbPresentation.ColonistReference>(roster.Colonists.Count);
        foreach (var item in roster.Colonists)
        {
            if (!pawns.TryGetValue(item.PawnId ?? "", out var row))
            {
                throw new BridgeUnavailableException($"native observation unavailable: colonist {item.PawnId} is not in the pawn table yet");
            }

            var dossier = row with { Settings = null, AnimalState = null };
            if (!dossier.Colonist.GetValueOrDefault() || dossier.Dead.GetValueOrDefault())
            {
                throw Contract("dossier is not a live colonist");
            }

            joined.Add(item with { Dossier = dossier });
        }

        return [.. joined];
    }

    /// <summary>
    /// Go <c>Client.ReadRenderState</c>: a zero-side-effect observation that never extends or shortens the native rendering
    /// lease. <see cref="DemandRenderingAsync"/> is the only call that changes lease state.
    /// </summary>
    internal async Task<PbPresentation.RenderReply> ReadRenderStateAsync(PbPresentation.ReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PresentationRequest(request.Identity);
        var reply = await Facade.ReadRenderStateAsync(request, cancellationToken).ConfigureAwait(false);
        return PresentationRenderOutcome(reply, request.Identity, "render state outcome required");
    }

    /// <summary>
    /// Go <c>PresentationMedia.DemandRendering</c>: asks the native mod to keep rendering for the lease seconds of real
    /// time; zero only reads status. The viewer carries no authority of its own.
    /// </summary>
    internal async Task<PbPresentation.RenderReply> DemandRenderingAsync(PbPresentation.RenderDemand request, CancellationToken cancellationToken)
    {
        if (request is null || request.Viewer is null)
        {
            throw Contract("render demand viewer required");
        }

        ValidateIdentity(request.Viewer.Identity);
        if (request.Viewer.ViewerId is not null && !IsValidId(request.Viewer.ViewerId))
        {
            throw Contract("invalid viewer id");
        }

        if (request.LeaseSeconds.GetValueOrDefault() > 30)
        {
            throw Contract("render demand lease exceeds 30 seconds");
        }

        var reply = await Facade.DemandRenderingAsync(request, cancellationToken).ConfigureAwait(false);
        return PresentationRenderOutcome(reply, request.Viewer.Identity, "render demand outcome required");
    }

    private static PbPresentation.RenderReply PresentationRenderOutcome(PbPresentation.RenderReply reply, PbCommon.Identity? identity, string missing)
    {
        switch (reply.Outcome)
        {
            case PbPresentation.RenderReplyOutcome.FailureCase { Value: var failure }:
                throw Failure(failure);
            case PbPresentation.RenderReplyOutcome.StatusCase { Value: var status }:
                PresentationContext(status.Context, identity);
                if (status.Unavailable is not null)
                {
                    ValidateUnavailable(status.Unavailable);
                }

                return reply;
            default:
                throw Contract(missing);
        }
    }

    private static void PresentationRequest(PbCommon.Identity? identity) => ValidateIdentity(identity);

    private static void PresentationContext(PbCommon.ObservationContext? actual, PbCommon.Identity? expected)
    {
        ValidateContext(actual);
        if (!SameIdentity(actual!.Identity, expected))
        {
            throw Contract("presentation context mismatch");
        }
    }

    private static bool PresentationText(string? value, int limit) => value is null || Encoding.UTF8.GetByteCount(value) <= limit;

    private static void PresentationId(string? id, HashSet<string> seen)
    {
        if (id is null)
        {
            return;
        }

        if (!IsValidId(id) || !seen.Add(id))
        {
            throw Contract("invalid or duplicate presentation identifier");
        }
    }

    private static void PresentationCell(PbCommon.Cell? cell)
    {
        if (cell is not null && (cell.X.GetValueOrDefault() < 0 || cell.Z.GetValueOrDefault() < 0))
        {
            throw Contract("negative presentation cell");
        }
    }

    private static void PresentationListing(PbPresentation.Listing? listing, int count)
    {
        if (count > PresentationListingLimit)
        {
            throw Contract("presentation listing exceeds limit");
        }

        if (listing is null)
        {
            return;
        }

        if (listing.ReturnedCount is { } returned && returned != (uint)count)
        {
            throw Contract("presentation returned count mismatch");
        }

        if (listing.TotalCount is { } total && total < (uint)count)
        {
            throw Contract("presentation total count mismatch");
        }

        if (listing.Complete.GetValueOrDefault() && (listing.Truncated.GetValueOrDefault() || (listing.TotalCount is { } t && t != (uint)count)))
        {
            throw Contract("inconsistent complete listing");
        }

        if (listing.Truncated.GetValueOrDefault() && listing.TotalCount is { } all && all <= (uint)count)
        {
            throw Contract("inconsistent truncated listing");
        }
    }

    /// <summary>Go <c>validateCamera</c>.</summary>
    private static void PresentationValidateCamera(PbPresentation.CameraState? camera, PbCommon.Identity? identity)
    {
        if (camera is null)
        {
            throw Contract("camera missing");
        }

        PresentationContext(camera.Context, identity);
        static bool Finite(double? value, bool nonnegative) => value is null || (double.IsFinite(value.Value) && (!nonnegative || value.Value >= 0));
        if (camera.MapPosition is { } position && (!Finite(position.X, false) || !Finite(position.Z, false)))
        {
            throw Contract("invalid camera position");
        }

        foreach (var size in new[] { camera.RootSize, camera.ZoomRootSize, camera.MinimumRootSize, camera.MaximumRootSize })
        {
            if (!Finite(size, true))
            {
                throw Contract("invalid camera size");
            }
        }

        if (camera.MinimumRootSize is { } min && camera.MaximumRootSize is { } max && min > max)
        {
            throw Contract("invalid camera size bounds");
        }

        if (!PresentationText(camera.NativeZoomRange, 256))
        {
            throw Contract("oversized camera zoom range");
        }

        if (camera.ViewRect is { } rect
            && ((rect.MinX is { } minX && rect.MaxX is { } maxX && minX > maxX) || (rect.MinZ is { } minZ && rect.MaxZ is { } maxZ && minZ > maxZ)))
        {
            throw Contract("inverted camera view bounds");
        }
    }

    /// <summary>Go <c>validateSelection</c>.</summary>
    private static void PresentationValidateSelection(PbPresentation.SelectionSnapshot? selection, PbCommon.Identity? identity)
    {
        if (selection is null)
        {
            throw Contract("selection missing");
        }

        PresentationContext(selection.Context, identity);
        PresentationListing(selection.Listing, selection.SelectedObjects.Count);
        if (!PresentationText(selection.Fingerprint, 4096))
        {
            throw Contract("oversized selection fingerprint");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in selection.SelectedObjects)
        {
            if (item is null)
            {
                throw Contract("missing selected object");
            }

            PresentationId(item.Id, seen);
            PresentationCell(item.Position);
            if (item.MapId is < 0)
            {
                throw Contract("invalid selected map");
            }

            if (!PresentationText(item.NativeKind, 256) || !PresentationText(item.NativeType, 256) || !PresentationText(item.DefName, 256))
            {
                throw Contract("oversized selected identifier");
            }

            if (!PresentationText(item.Label, 16384) || !PresentationText(item.InspectLabel, 16384) || !PresentationText(item.InspectText, 16384))
            {
                throw Contract("oversized selection text");
            }
        }
    }

    /// <summary>Go <c>validateRoster</c>.</summary>
    private static void PresentationValidateRoster(PbPresentation.ColonistRoster? roster, PbPresentation.ColonistRosterRequest request)
    {
        if (roster is null)
        {
            throw Contract("roster missing");
        }

        PresentationContext(roster.Context, request.Identity);
        PresentationListing(roster.Listing, roster.Colonists.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in roster.Colonists)
        {
            if (item is null)
            {
                throw Contract("missing colonist");
            }

            PresentationId(item.PawnId, seen);
            PresentationCell(item.Position);
            if (!PresentationText(item.Name, 16384))
            {
                throw Contract("oversized colonist name");
            }

            if (item.MapId is { } map && (map < 0 || (request.CurrentMapOnly.GetValueOrDefault() && map != request.Identity!.MapId.GetValueOrDefault())))
            {
                throw Contract("invalid roster map");
            }

            if (item.Dossier is not null)
            {
                throw Contract("native colonist dossier");
            }
        }
    }

    /// <summary>
    /// Go <c>Client.ReadNotifications</c>: only observes. Partial listings and unknown optional facts remain partial or
    /// unknown; this read never acknowledges or dismisses anything.
    /// </summary>
    internal async Task<PbPresentation.NotificationsReply> ReadNotificationsAsync(PbPresentation.NotificationsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.Identity);
        foreach (var limit in new[] { request.LetterLimit, request.MessageLimit, request.AlertLimit })
        {
            if (limit is < 1)
            {
                throw Contract("notification limit below 1");
            }
        }

        var reply = await Facade.ReadNotificationsAsync(request, cancellationToken).ConfigureAwait(false);
        switch (reply.Outcome)
        {
            case PbPresentation.NotificationsReplyOutcome.FailureCase { Value: var failure }:
                throw Failure(failure);
            case PbPresentation.NotificationsReplyOutcome.NotificationsCase { Value: var snapshot }:
                NotificationsValidate(snapshot, request);
                return reply;
            default:
                throw Contract("notification outcome required");
        }
    }

    /// <summary>Go <c>notificationsSnapshot</c>.</summary>
    private static void NotificationsValidate(PbPresentation.NotificationsSnapshot? snapshot, PbPresentation.NotificationsRequest request)
    {
        if (snapshot is null)
        {
            throw Contract("notification snapshot required");
        }

        ValidateContext(snapshot.Context);
        if (!SameIdentity(snapshot.Context!.Identity, request.Identity))
        {
            throw Contract("notification world mismatch");
        }

        static bool Included(bool? flag) => flag is null || flag.Value;
        if ((snapshot.Letters is not null) != Included(request.IncludeLetters)
            || (snapshot.Messages is not null) != Included(request.IncludeMessages)
            || (snapshot.Alerts is not null) != Included(request.IncludeAlerts))
        {
            throw Contract("notification section inclusion mismatch");
        }

        static int Limit(uint? value, int fallback) => value is null ? fallback : (int)Math.Min(value.Value, int.MaxValue);
        if (snapshot.Letters is { } letters)
        {
            switch (letters.Outcome)
            {
                case PbPresentation.LetterSectionOutcome.UnavailableCase { Value: var unavailable }:
                    ValidateUnavailable(unavailable);
                    break;
                case PbPresentation.LetterSectionOutcome.ObservedCase { Value: var observed }:
                    var rows = observed.Items;
                    if (rows.Count > Limit(request.LetterLimit, 40))
                    {
                        throw Contract("letter limit exceeded");
                    }

                    NotificationsListing(observed.Listing, rows.Count);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var row in rows)
                    {
                        if (row is null)
                        {
                            throw Contract("nil letter");
                        }

                        NotificationsId(row.Id, seen);
                        if (row.AgeTicks.GetValueOrDefault() < 0)
                        {
                            throw Contract("negative letter time");
                        }

                        NotificationsTargets(row.LookTargets);
                        NotificationsListing(row.ChoicesListing, row.Choices.Count);
                        var indices = new HashSet<uint>();
                        foreach (var choice in row.Choices)
                        {
                            if (choice is null)
                            {
                                throw Contract("nil letter choice");
                            }

                            if (choice.Index is { } index && (index == 0 || !indices.Add(index)))
                            {
                                throw Contract("invalid or duplicate letter choice index");
                            }
                        }
                    }

                    break;
                default:
                    throw Contract("letter section outcome required");
            }
        }

        if (snapshot.Messages is { } messages)
        {
            switch (messages.Outcome)
            {
                case PbPresentation.MessageSectionOutcome.UnavailableCase { Value: var unavailable }:
                    ValidateUnavailable(unavailable);
                    break;
                case PbPresentation.MessageSectionOutcome.ObservedCase { Value: var observed }:
                    var rows = observed.Items;
                    if (rows.Count > Limit(request.MessageLimit, 12))
                    {
                        throw Contract("message limit exceeded");
                    }

                    NotificationsListing(observed.Listing, rows.Count);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var row in rows)
                    {
                        if (row is null)
                        {
                            throw Contract("nil message");
                        }

                        NotificationsId(row.Id, seen);
                        if (row.AgeTicks.GetValueOrDefault() < 0)
                        {
                            throw Contract("negative message tick age");
                        }

                        NotificationsTargets(row.LookTargets);
                    }

                    break;
                default:
                    throw Contract("message section outcome required");
            }
        }

        if (snapshot.Alerts is { } alerts)
        {
            switch (alerts.Outcome)
            {
                case PbPresentation.AlertSectionOutcome.UnavailableCase { Value: var unavailable }:
                    ValidateUnavailable(unavailable);
                    break;
                case PbPresentation.AlertSectionOutcome.ObservedCase { Value: var observed }:
                    var rows = observed.Items;
                    if (!PresentationText(observed.SnapshotFingerprint, 4096))
                    {
                        throw Contract("invalid notification snapshot fingerprint");
                    }

                    if (rows.Count > Limit(request.AlertLimit, 40))
                    {
                        throw Contract("alert limit exceeded");
                    }

                    NotificationsListing(observed.Listing, rows.Count);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var row in rows)
                    {
                        if (row is null)
                        {
                            throw Contract("nil alert");
                        }

                        NotificationsId(row.Id, seen);
                        NotificationsListing(row.Listing, row.Targets.Count);
                        foreach (var target in row.Targets)
                        {
                            NotificationsTarget(target);
                        }

                        if (row.ReadIssue is not null)
                        {
                            ValidateUnavailable(row.ReadIssue);
                        }
                    }

                    break;
                default:
                    throw Contract("alert section outcome required");
            }
        }
    }

    /// <summary>Go <c>notificationsListing</c>: absent metadata is unknown, never an implicit complete-empty listing.</summary>
    private static void NotificationsListing(PbPresentation.Listing? listing, int count)
    {
        if (listing is null)
        {
            return;
        }

        if ((listing.ReturnedCount is { } returned && returned != (uint)count) || (listing.TotalCount is { } total && total < (uint)count))
        {
            throw Contract("notification listing count mismatch");
        }

        if (listing.Complete.GetValueOrDefault() && (listing.Truncated.GetValueOrDefault() || (listing.TotalCount is { } t && t != (uint)count)))
        {
            throw Contract("notification listing falsely complete");
        }

        if (listing.Truncated.GetValueOrDefault() && listing.TotalCount is { } all && all <= (uint)count)
        {
            throw Contract("notification listing falsely truncated");
        }
    }

    /// <summary>Go <c>notificationsID</c>: a missing native identity stays unavailable for control.</summary>
    private static void NotificationsId(string? id, HashSet<string> seen)
    {
        if (id is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Contains('\0') || !seen.Add(id))
        {
            throw Contract("invalid or duplicate notification identity");
        }
    }

    private static void NotificationsTargets(PbPresentation.LookTargets? targets)
    {
        if (targets is null)
        {
            return;
        }

        NotificationsListing(targets.Listing, targets.Targets.Count);
        if (targets.Primary is not null)
        {
            NotificationsTarget(targets.Primary);
        }

        foreach (var target in targets.Targets)
        {
            NotificationsTarget(target);
        }
    }

    private static void NotificationsTarget(PbPresentation.LookTarget? target)
    {
        if (target is null)
        {
            throw Contract("nil notification target");
        }

        NotificationsId(target.Id, []);
        NotificationsId(target.WorldTileId, []);
        if (target.MapId is < 0)
        {
            throw Contract("invalid notification target map");
        }

        if (target.Position is { } cell && (cell.X is null || cell.Z is null || cell.X < 0 || cell.Z < 0))
        {
            throw Contract("invalid notification target cell");
        }
    }
}
