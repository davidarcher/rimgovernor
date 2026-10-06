#nullable enable
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;

namespace HomeBridge.BridgeTools
{
    // The rules ops (operations.proto Rules): attach replaces every rule and sets the lease, clear removes
    // them, status reads them. The rules themselves run in NativeRuleRuntime.
    public sealed class NativeRuleTools
    {
        private const string AttachName = "rimgovernor/rules_attach";
        private const string ClearName = "rimgovernor/rules_clear";
        private const string StatusName = "rimgovernor/rules_read_status";

        [Tool(AttachName, Title = "Attach native rules", Description = "Replace the active native rules and set their lease tick; rules fire only under Auto authority, journal each firing on the clock ring, and deactivate at the lease tick. Idempotent by rule id; refused rules come back with a reason.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.operations.v1.RulesAttachReply.", Always = true)]
        public async Task<object> Attach(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON RulesAttachRequest.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, AttachName, request, Operations.RulesAttachRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.RulesAttachReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                    return ProtoBoundary.Encode(new Operations.RulesAttachReply { Failure = invalid });
                if (!parsed.HasExpiresAtTick || parsed.ExpiresAtTick <= context.Tick)
                    return ProtoBoundary.Encode(new Operations.RulesAttachReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The rules lease must end after the current tick.") });
                if (!NativeRuleRuntime.EnsureHooks())
                    return ProtoBoundary.Encode(new Operations.RulesAttachReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native rule hooks are unavailable.") });
                return ProtoBoundary.Encode(new Operations.RulesAttachReply { Attached = NativeRuleRuntime.Attach(context, parsed.Rules, parsed.ExpiresAtTick) });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool(ClearName, Title = "Clear native rules", Description = "Deactivate every attached native rule. Needs no authority.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.operations.v1.RulesClearReply.", Always = true)]
        public async Task<object> Clear(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON RulesClearRequest.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ClearName, request, Operations.RulesClearRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.RulesClearReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                    return ProtoBoundary.Encode(new Operations.RulesClearReply { Failure = invalid });
                return ProtoBoundary.Encode(new Operations.RulesClearReply { Cleared = new Operations.RulesCleared { Context = context, Cleared = NativeRuleRuntime.Clear(Current.Game) } });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool(StatusName, Title = "Read native rules", Description = "Read the active native rules: lease remaining, each rule's firing count and last firing.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.operations.v1.RulesStatusReply.", Always = true)]
        public async Task<object> ReadStatus(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON RulesStatusRequest.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, StatusName, request, Operations.RulesStatusRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.RulesStatusReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                    return ProtoBoundary.Encode(new Operations.RulesStatusReply { Failure = invalid });
                return ProtoBoundary.Encode(new Operations.RulesStatusReply { Status = NativeRuleRuntime.Status(context, Current.Game) });
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
