using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.GameAccess;
using UnityEngine;
using Verse;

namespace RimGovernor.Native
{
    internal static class NativePresentationReadTools
    {
        // On the game thread: the camera read of the in-process facade. A refusal throws.
        internal static RimGovernor.Brain.GameFacts.Presentation.CameraState HandleCamera(RimGovernor.Brain.GameOrders.Presentation.ReadRequest parsed)
        {
            if (!ValidateRead(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!GameThread.ValidateViewedIdentity(parsed.Identity, out var context, out var error)) throw NativeFaults.Refused(error);
            RimGovernor.Brain.GameFacts.Common.Failure refusal;
            try
            {
                var driver = Find.CameraDriver;
                if (!HeadlessRim.HeadlessMode.Active && Find.Camera != null && driver?.config != null)
                    return new RimGovernor.Brain.GameFacts.Presentation.CameraState { Context = context };
                refusal = Unavailable("Graphical map camera unavailable.");
            }
            catch (Exception errorRead) { refusal = ReadFailure(errorRead); }
            throw NativeFaults.Refused(refusal);
        }

        // On the game thread: the selection read of the in-process facade. A refusal throws.
        internal static RimGovernor.Brain.GameFacts.Presentation.SelectionSnapshot HandleSelection(RimGovernor.Brain.GameOrders.Presentation.ReadRequest parsed)
        {
            if (!ValidateRead(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!GameThread.ValidateViewedIdentity(parsed.Identity, out var context, out var error)) throw NativeFaults.Refused(error);
            RimGovernor.Brain.GameFacts.Common.Failure refusal;
            try
            {
                if (!HeadlessRim.HeadlessMode.Active && Find.Selector != null)
                    return new RimGovernor.Brain.GameFacts.Presentation.SelectionSnapshot { Context = context };
                refusal = Unavailable("Graphical native selection unavailable.");
            }
            catch (Exception errorRead) { refusal = ReadFailure(errorRead); }
            throw NativeFaults.Refused(refusal);
        }

        // On the game thread: the colonist roster read of the in-process facade, every spawned free colonist with its map.
        // A refusal throws.
        internal static RimGovernor.Brain.GameFacts.Presentation.ColonistRoster HandleColonists(RimGovernor.Brain.GameOrders.Presentation.ColonistRosterRequest parsed)
        {
            if (!ValidateColonists(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!GameThread.ValidateIdentity(parsed.Identity, out var context, out var error)) throw NativeFaults.Refused(error);
            RimGovernor.Brain.GameFacts.Common.Failure refusal;
            try
            {
                var maps = parsed.CurrentMapOnly ? new[] { GameThread.LoadedMap(context) } : Find.Maps.ToArray();
                var pawns = maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).Distinct().OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                var colonists = new List<RimGovernor.Brain.GameFacts.Presentation.ColonistReference>(pawns.Count);
                foreach (var pawn in pawns)
                {
                    if (!pawn.Spawned || pawn.Map == null || !pawn.Position.InBounds(pawn.Map)) throw new InvalidOperationException("Spawned colonist context unavailable.");
                    colonists.Add(new RimGovernor.Brain.GameFacts.Presentation.ColonistReference { PawnId = new RimGovernor.Brain.Domain.PawnId(Id(pawn.GetUniqueLoadID())), MapId = pawn.Map.uniqueID });
                }
                if (colonists.Select(p => p.PawnId).Distinct().Count() != colonists.Count)
                    throw new InvalidOperationException("Native colonist IDs are not unique.");
                return new RimGovernor.Brain.GameFacts.Presentation.ColonistRoster { Context = context, Colonists = [.. colonists] };
            }
            catch (Exception errorRead) { refusal = ReadFailure(errorRead); }
            throw NativeFaults.Refused(refusal);
        }

        internal static bool ValidateRead(RimGovernor.Brain.GameOrders.Presentation.ReadRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        { failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Exact identity is required."); return request?.Identity != null; }
        internal static bool ValidateColonists(RimGovernor.Brain.GameOrders.Presentation.ColonistRosterRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        { failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Exact identity is required."); return request?.Identity != null; }

        private static RimGovernor.Brain.Domain.Cell Cell(IntVec3 cell) => new RimGovernor.Brain.Domain.Cell { X = cell.x, Z = cell.z };
        private static string Id(string value) => GameThread.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static string Diagnostic(string value) => PlacementPreviewOperation.Diagnostic(value);
        private static RimGovernor.Brain.GameFacts.Common.Failure Unavailable(string detail) => GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, detail);
        private static RimGovernor.Brain.GameFacts.Common.Failure ReadFailure(Exception error) => error is NativePawnObservationTools.ReadLimit ? GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.CapacityExhausted, error.Message) : Unavailable("Native presentation facts could not be read completely.");
    }
}
