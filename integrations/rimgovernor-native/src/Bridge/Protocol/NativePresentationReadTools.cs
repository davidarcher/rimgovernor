#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Presentation = RimGovernor.Contracts.Wire.Presentation;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativePresentationReadTools
    {
        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Presentation.CameraReply HandleCamera(Presentation.ReadRequest parsed)
        {
            if (!ValidateRead(parsed, out var invalidRequest)) return new Presentation.CameraReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error))
                return new Presentation.CameraReply { Outcome = error };
            try
            {
                var driver = Find.CameraDriver;
                if (HeadlessRim.HeadlessMode.Active || Find.Camera == null || driver?.config == null)
                    return new Presentation.CameraReply { Outcome = Unavailable("Graphical map camera unavailable.") };
                return new Presentation.CameraReply { Outcome = new Presentation.CameraState { Context = context } };
            }
            catch (Exception errorRead) { return new Presentation.CameraReply { Outcome = ReadFailure(errorRead) }; }
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Presentation.SelectionReply HandleSelection(Presentation.ReadRequest parsed)
        {
            if (!ValidateRead(parsed, out var invalidRequest)) return new Presentation.SelectionReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error))
                return new Presentation.SelectionReply { Outcome = error };
            try
            {
                if (HeadlessRim.HeadlessMode.Active || Find.Selector == null)
                    return new Presentation.SelectionReply { Outcome = Unavailable("Graphical native selection unavailable.") };
                return new Presentation.SelectionReply { Outcome = new Presentation.SelectionSnapshot { Context = context } };
            }
            catch (Exception errorRead) { return new Presentation.SelectionReply { Outcome = ReadFailure(errorRead) }; }
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Presentation.ColonistRosterReply HandleColonists(Presentation.ColonistRosterRequest parsed)
        {
            if (!ValidateColonists(parsed, out var invalidRequest)) return new Presentation.ColonistRosterReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var error))
                return new Presentation.ColonistRosterReply { Outcome = error };
            try
            {
                var maps = parsed.CurrentMapOnly.GetValueOrDefault() ? new[] { ProtoBoundary.LoadedMap(context) } : Find.Maps.ToArray();
                var pawns = maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).Distinct().OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                var roster = new Presentation.ColonistRoster { Context = context, Listing = Listing(pawns.Count) };
                foreach (var pawn in pawns)
                {
                    if (!pawn.Spawned || pawn.Map == null || !pawn.Position.InBounds(pawn.Map)) throw new InvalidOperationException("Spawned colonist context unavailable.");
                    var reference = new Presentation.ColonistReference { PawnId = Id(pawn.GetUniqueLoadID()),
                        Name = Diagnostic(pawn.Name?.ToStringShort ?? pawn.LabelShort), MapId = pawn.Map.uniqueID,
                        Spawned = true, Position = Cell(pawn.Position) };
                    roster = roster with { Colonists = [.. roster.Colonists, reference] };
                }
                if (roster.Colonists.Select(p => (p.PawnId ?? "")).Distinct(StringComparer.Ordinal).Count() != roster.Colonists.Count)
                    throw new InvalidOperationException("Native colonist IDs are not unique.");
                return new Presentation.ColonistRosterReply { Outcome = roster };
            }
            catch (Exception errorRead) { return new Presentation.ColonistRosterReply { Outcome = ReadFailure(errorRead) }; }
        }

        internal static bool ValidateRead(Presentation.ReadRequest request, out Common.Failure failure)
        { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity is required."); return request?.Identity != null; }
        internal static bool ValidateColonists(Presentation.ColonistRosterRequest request, out Common.Failure failure)
        { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity is required."); return request?.Identity != null; }

        private static Common.Cell Cell(IntVec3 cell) => new Common.Cell { X = cell.x, Z = cell.z };
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static string Diagnostic(string value) => PlacementPreviewOperation.Diagnostic(value);
        internal static Presentation.Listing Listing(int count) => new Presentation.Listing { TotalCount = checked((uint)count), ReturnedCount = checked((uint)count), Complete = true, Truncated = false };
        private static Common.Failure Unavailable(string detail) => ProtoBoundary.Fail(Common.FailureCode.Unavailable, detail);
        private static Common.Failure ReadFailure(Exception error) => error is NativePawnObservationTools.ReadLimit ? ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted, error.Message) : Unavailable("Native presentation facts could not be read completely.");
    }
}
