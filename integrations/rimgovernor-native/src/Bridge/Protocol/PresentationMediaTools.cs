using System;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.GameAccess;
using UnityEngine;
using Verse;

namespace RimGovernor.Native
{
    // Typed-proto surface for PresentationMedia's DemandRendering and
    // PresentationReads' RenderState, both over RenderDemandDriver.
    internal static class PresentationMediaTools
    {
        // On the game thread: the render-state read of the in-process facade. A refusal throws.
        internal static RimGovernor.Brain.GameFacts.Presentation.RenderStatus HandleRenderState(RimGovernor.Brain.GameOrders.Presentation.ReadRequest parsed)
        {
            if (!NativePresentationReadTools.ValidateRead(parsed, out var failure0)) throw NativeFaults.Refused(failure0);
            if (!GameThread.ValidateViewedIdentity(parsed.Identity, out var context, out var error)) throw NativeFaults.Refused(error);
            return Status(context, RenderDemandDriver.Peek());
        }

        // On the game thread: the typed core of the tool.
        internal static RimGovernor.Brain.GameFacts.Presentation.RenderReply HandleDemandRendering(RimGovernor.Brain.GameOrders.Presentation.RenderDemand parsed)
        {
            if (!ValidateDemand(parsed, out var failure0)) return new RimGovernor.Brain.GameFacts.Presentation.RenderReply { Outcome = new RimGovernor.Brain.GameFacts.Presentation.RenderReplyOutcome.FailureCase(failure0) };
            if (!GameThread.ValidateViewedIdentity(parsed.Viewer.Identity, out var context, out var error))
                return new RimGovernor.Brain.GameFacts.Presentation.RenderReply { Outcome = error };
            var status = RenderDemandDriver.Lease((int)parsed.LeaseSeconds);
            return new RimGovernor.Brain.GameFacts.Presentation.RenderReply { Outcome = Status(context, status) };
        }

        // An unsupported renderer (headless) reports Suspended with no window and no lease; the driver's status already says so.
        private static RimGovernor.Brain.GameFacts.Presentation.RenderStatus Status(RimGovernor.Brain.GameFacts.Common.ObservationContext context, RenderDemandStatus status) => new RimGovernor.Brain.GameFacts.Presentation.RenderStatus
        {
            Context = context,
            Supported = status.Supported,
            Suspended = status.Suspended,
            WindowVisible = status.WindowVisible,
            RemainingLeaseMs = (uint)Math.Max(0, Mathf.RoundToInt(status.RemainingSeconds * 1000f)),
        };

        private static bool ValidateDemand(RimGovernor.Brain.GameOrders.Presentation.RenderDemand request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Exact viewer identity and a lease of 0-30 seconds are required.");
            if (request?.Viewer?.Identity == null) return false;
            if (request.LeaseSeconds > 30) return false;
            if (request.Viewer.ViewerId is not null && !GameThread.IsIdentifier(request.Viewer.ViewerId)) return false;
            return true;
        }
    }
}
