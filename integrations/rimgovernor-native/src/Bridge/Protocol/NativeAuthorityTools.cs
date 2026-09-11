#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeAuthorityTools
    {
        private const string ToolName = "rimgovernor/authority_read_status";

        [Tool(ToolName, Title = "Read native control authority", Description = "Read current native authority without acquiring or renewing control. request is official ProtoJSON for rimgovernor.authority.v1.StatusRequest.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.authority.v1.StatusReply.", Always = true)]
        public async Task<object> ReadStatus(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON StatusRequest with exact colony/load/map identity.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Authority.StatusRequest.Parser,
                out var parsed, out var failure))
                return ProtoBoundary.Encode(new Authority.StatusReply { Failure = failure });

            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, Find.CurrentMap, out var context, out var invalid))
                    return ProtoBoundary.Encode(new Authority.StatusReply { Failure = invalid });
                var game = Current.Game;
                if (game == null)
                    return ProtoBoundary.Encode(new Authority.StatusReply { Failure = new Common.Failure
                    {
                        Code = Common.FailureCode.Unavailable, Detail = "No game is loaded."
                    } });
                if (!NativeControlAuthority.TryGetForGame(game, out var state) || state == null)
                    return ProtoBoundary.Encode(new Authority.StatusReply { Status = new Authority.Status
                    {
                        Context = context,
                        Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NotObserved,
                            Detail = "Native control hooks and trusted admission have not been initialized." }
                    } });
                var snapshot = state.Status();
                return ProtoBoundary.Encode(new Authority.StatusReply { Status = Project(snapshot, context) });
            }, cancellationToken);
        }

        // This projection does not initialize hooks, grant authority or disclose the lease secret.
        internal static Authority.Status Project(NativeControlSnapshot snapshot, Common.ObservationContext context)
        {
            var status = new Authority.Status { Context = context.Clone() };
            status.Context.NativeGeneration = snapshot.Generation;
            if (!snapshot.Available)
            {
                status.Unavailable = Unavailable(snapshot);
                return status;
            }
            if (snapshot.Lease != null)
            {
                status.Active = new Authority.ActiveAuthority
                {
                    Owner = new Authority.Owner
                    {
                        ControllerSessionId = snapshot.Lease.ControllerSessionId,
                        PlayerDirection = snapshot.Lease.PlayerDirection
                    },
                    RemainingLeaseMs = checked((uint)snapshot.RemainingLeaseMs)
                };
                return status;
            }
            if (TryReason(snapshot.Reason, out var reason))
                status.Inactive = new Authority.InactiveAuthority { Reason = reason };
            else
                status.Unavailable = new Common.Unavailable
                {
                    Reason = Common.UnavailableReason.ReadFailed,
                    Detail = "Native authority status could not be represented."
                };
            return status;
        }

        private static Common.Unavailable Unavailable(NativeControlSnapshot snapshot)
        {
            if (snapshot.Identity == null)
                return new Common.Unavailable { Reason = Common.UnavailableReason.NotLoaded, Detail = "No current native authority context is loaded." };
            switch (snapshot.Reason)
            {
                case NativeControlRevocationReason.GenerationExhausted:
                    return new Common.Unavailable { Reason = Common.UnavailableReason.LimitExceeded, Detail = "Native authority generation is exhausted." };
                case NativeControlRevocationReason.ClockUnavailable:
                    return new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "The native monotonic authority clock is unavailable." };
                default:
                    return new Common.Unavailable { Reason = Common.UnavailableReason.NotObserved, Detail = "Native control hooks and trusted admission have not been verified." };
            }
        }

        private static bool TryReason(NativeControlRevocationReason source, out Authority.RevocationReason result)
        {
            switch (source)
            {
                case NativeControlRevocationReason.None: result = Authority.RevocationReason.None; return true;
                case NativeControlRevocationReason.Manual: result = Authority.RevocationReason.Manual; return true;
                case NativeControlRevocationReason.PlayerDirection: result = Authority.RevocationReason.PlayerDirection; return true;
                case NativeControlRevocationReason.ExternalOrder: result = Authority.RevocationReason.ExternalOrder; return true;
                case NativeControlRevocationReason.PlayerControl: result = Authority.RevocationReason.PlayerControl; return true;
                case NativeControlRevocationReason.LeaseExpired: result = Authority.RevocationReason.LeaseExpired; return true;
                case NativeControlRevocationReason.IdentityChanged: result = Authority.RevocationReason.IdentityChanged; return true;
                case NativeControlRevocationReason.Disconnect: result = Authority.RevocationReason.Disconnect; return true;
                case NativeControlRevocationReason.Shutdown: result = Authority.RevocationReason.Shutdown; return true;
                case NativeControlRevocationReason.HooksUnavailable: result = Authority.RevocationReason.HooksUnavailable; return true;
                case NativeControlRevocationReason.GenerationExhausted: result = Authority.RevocationReason.GenerationExhausted; return true;
                default: result = Authority.RevocationReason.Unspecified; return false;
            }
        }
    }
}
