#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Presentation = RimGovernor.Contracts.Wire.Presentation;

namespace HomeBridge.BridgeTools
{
    // Typed-proto surface for PresentationMedia's DemandRendering and
    // PresentationReads' RenderState, both over RenderDemandDriver.
    internal static class ProtoPresentationMediaTools
    {
        // On the game thread: the typed core of the tool.
        internal static Presentation.RenderReply HandleRenderState(Presentation.ReadRequest parsed)
        {
            if (!NativePresentationReadTools.ValidateRead(parsed, out var failure0)) return new Presentation.RenderReply { Outcome = new Presentation.RenderReplyOutcome.FailureCase(failure0) };
            if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error))
                return new Presentation.RenderReply { Outcome = error };
            return new Presentation.RenderReply { Outcome = Status(context, RenderDemandDriver.Peek()) };
        }

        // On the game thread: the typed core of the tool.
        internal static Presentation.RenderReply HandleDemandRendering(Presentation.RenderDemand parsed)
        {
            if (!ValidateDemand(parsed, out var failure0)) return new Presentation.RenderReply { Outcome = new Presentation.RenderReplyOutcome.FailureCase(failure0) };
            if (!ProtoBoundary.ValidateViewedIdentity(parsed?.Viewer?.Identity, out var context, out var error))
                return new Presentation.RenderReply { Outcome = error };
            var status = RenderDemandDriver.Lease((int)(parsed?.LeaseSeconds).GetValueOrDefault());
            return new Presentation.RenderReply { Outcome = Status(context, status) };
        }

        private static Presentation.RenderStatus Status(Common.ObservationContext context, RenderDemandStatus status)
        {
            var result = new Presentation.RenderStatus { Context = context, Supported = status.Supported };
            if (!status.Supported)
            {
                result = result with { Suspended = true };
                result = result with { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.Unsupported,
                    Detail = string.IsNullOrEmpty(status.UnavailableDetail) ? "Native rendering is unsupported." : status.UnavailableDetail } };
                return result;
            }
            result = result with { Suspended = status.Suspended };
            result = result with { WindowVisible = status.WindowVisible };
            result = result with { RemainingLeaseMs = (uint)Math.Max(0, Mathf.RoundToInt(status.RemainingSeconds * 1000f)) };
            return result;
        }

        private static bool ValidateDemand(Presentation.RenderDemand request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact viewer identity and a lease of 0-30 seconds are required.");
            if (request?.Viewer?.Identity == null) return false;
            if (request.LeaseSeconds.GetValueOrDefault() > 30) return false;
            if (request.Viewer.ViewerId is not null && !ProtoBoundary.IsIdentifier(request.Viewer.ViewerId)) return false;
            return true;
        }
    }
}
