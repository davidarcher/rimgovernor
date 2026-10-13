#nullable enable
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;

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
            if (!ProtoBoundary.TryParse(ctx, AttachName, request, out Operations.RulesAttachRequest? parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.RulesAttachReply { Outcome = new Operations.RulesAttachReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleAttach(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Operations.RulesAttachReply HandleAttach(Operations.RulesAttachRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                return new Operations.RulesAttachReply { Outcome = invalid };
            if (parsed.ExpiresAtTick is null || parsed.ExpiresAtTick.GetValueOrDefault() <= context.Tick.GetValueOrDefault())
                return new Operations.RulesAttachReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The rules lease must end after the current tick.") };
            if (!NativeRuleRuntime.EnsureHooks())
                return new Operations.RulesAttachReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native rule hooks are unavailable.") };
            return new Operations.RulesAttachReply { Outcome = NativeRuleRuntime.Attach(context, parsed.Rules, parsed.ExpiresAtTick.GetValueOrDefault()) };
        }

        [Tool(ClearName, Title = "Clear native rules", Description = "Deactivate every attached native rule. Needs no authority.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.operations.v1.RulesClearReply.", Always = true)]
        public async Task<object> Clear(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON RulesClearRequest.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ClearName, request, out Operations.RulesClearRequest? parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.RulesClearReply { Outcome = new Operations.RulesClearReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleClear(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Operations.RulesClearReply HandleClear(Operations.RulesClearRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                return new Operations.RulesClearReply { Outcome = invalid };
            return new Operations.RulesClearReply { Outcome = new Operations.RulesCleared { Context = context, Cleared = NativeRuleRuntime.Clear(Current.Game) } };
        }

        [Tool(StatusName, Title = "Read native rules", Description = "Read the active native rules: lease remaining, each rule's firing count and last firing.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.operations.v1.RulesStatusReply.", Always = true)]
        public async Task<object> ReadStatus(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON RulesStatusRequest.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, StatusName, request, out Operations.RulesStatusRequest? parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.RulesStatusReply { Outcome = new Operations.RulesStatusReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleReadStatus(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Operations.RulesStatusReply HandleReadStatus(Operations.RulesStatusRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                return new Operations.RulesStatusReply { Outcome = invalid };
            return new Operations.RulesStatusReply { Outcome = NativeRuleRuntime.Status(context, Current.Game) };
        }
    }
}
