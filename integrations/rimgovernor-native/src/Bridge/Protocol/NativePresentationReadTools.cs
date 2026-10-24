#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Bridge.Contracts;
using UnityEngine;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Presentation = RimGovernor.Contracts.Wire.Presentation;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativePresentationReadTools
    {
        // On the game thread: the camera read of the in-process facade. A refusal throws.
        internal static Presentation.CameraState HandleCamera(Presentation.ReadRequest parsed)
        {
            if (!ValidateRead(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error)) throw NativeFaults.Refused(error);
            Common.Failure refusal;
            try
            {
                var driver = Find.CameraDriver;
                if (!HeadlessRim.HeadlessMode.Active && Find.Camera != null && driver?.config != null)
                    return new Presentation.CameraState { Context = context };
                refusal = Unavailable("Graphical map camera unavailable.");
            }
            catch (Exception errorRead) { refusal = ReadFailure(errorRead); }
            throw NativeFaults.Refused(refusal);
        }

        // On the game thread: the selection read of the in-process facade. A refusal throws.
        internal static Presentation.SelectionSnapshot HandleSelection(Presentation.ReadRequest parsed)
        {
            if (!ValidateRead(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error)) throw NativeFaults.Refused(error);
            Common.Failure refusal;
            try
            {
                if (!HeadlessRim.HeadlessMode.Active && Find.Selector != null)
                    return new Presentation.SelectionSnapshot { Context = context };
                refusal = Unavailable("Graphical native selection unavailable.");
            }
            catch (Exception errorRead) { refusal = ReadFailure(errorRead); }
            throw NativeFaults.Refused(refusal);
        }

        // On the game thread: the colonist roster read of the in-process facade, every spawned free colonist with its map.
        // A refusal throws.
        internal static Presentation.ColonistRoster HandleColonists(Presentation.ColonistRosterRequest parsed)
        {
            if (!ValidateColonists(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var error)) throw NativeFaults.Refused(error);
            Common.Failure refusal;
            try
            {
                var maps = parsed.CurrentMapOnly ? new[] { ProtoBoundary.LoadedMap(context) } : Find.Maps.ToArray();
                var pawns = maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).Distinct().OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                var colonists = new List<Presentation.ColonistReference>(pawns.Count);
                foreach (var pawn in pawns)
                {
                    if (!pawn.Spawned || pawn.Map == null || !pawn.Position.InBounds(pawn.Map)) throw new InvalidOperationException("Spawned colonist context unavailable.");
                    colonists.Add(new Presentation.ColonistReference { PawnId = new RimGovernor.Brain.Domain.PawnId(Id(pawn.GetUniqueLoadID())), MapId = pawn.Map.uniqueID });
                }
                if (colonists.Select(p => p.PawnId).Distinct().Count() != colonists.Count)
                    throw new InvalidOperationException("Native colonist IDs are not unique.");
                return new Presentation.ColonistRoster { Context = context, Colonists = [.. colonists] };
            }
            catch (Exception errorRead) { refusal = ReadFailure(errorRead); }
            throw NativeFaults.Refused(refusal);
        }

        internal static bool ValidateRead(Presentation.ReadRequest request, out Common.Failure failure)
        { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity is required."); return request?.Identity != null; }
        internal static bool ValidateColonists(Presentation.ColonistRosterRequest request, out Common.Failure failure)
        { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity is required."); return request?.Identity != null; }

        private static Common.Cell Cell(IntVec3 cell) => new Common.Cell { X = cell.x, Z = cell.z };
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static string Diagnostic(string value) => PlacementPreviewOperation.Diagnostic(value);
        private static Common.Failure Unavailable(string detail) => ProtoBoundary.Fail(Common.FailureCode.Unavailable, detail);
        private static Common.Failure ReadFailure(Exception error) => error is NativePawnObservationTools.ReadLimit ? ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted, error.Message) : Unavailable("Native presentation facts could not be read completely.");
    }
}
