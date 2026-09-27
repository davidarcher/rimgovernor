#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Presentation = RimGovernor.Protocol.Presentation;

namespace HomeBridge.BridgeTools
{
    // The controller's in-game status strip (#823), drawn by
    // GovernorStatusStrip. Output only; nothing reads it back.
    public sealed class ProtoStatusStripTools
    {
        private const string ToolName = "rimgovernor/presentation_status_strip";
        private const int MaxRows = 64;
        private const int MaxKey = 32;
        private const int MaxText = 120;

        [Tool(ToolName, Title = "Set the status strip",
            Description = "Replace every status strip row. enabled=false clears the strip.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.presentation.v1.StatusStripReply.", Always = true)]
        public async Task<object> Set(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON StatusStripRequest string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Presentation.StatusStripRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Presentation.StatusStripReply { Failure = failure });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => Apply(parsed), cancellationToken).ConfigureAwait(false);
        }

        // Call only on the game thread.
        internal static Presentation.StatusStripReply Apply(Presentation.StatusStripRequest request)
        {
            if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out var context, out var invalid))
                return new Presentation.StatusStripReply { Failure = invalid };
            if (!request.Enabled)
            {
                GovernorStatusStrip.Clear();
                return Applied(context);
            }
            if (request.Rows.Count > MaxRows) return Invalid($"More than {MaxRows} status rows.");
            var rows = new List<StatusStripRow>(request.Rows.Count);
            foreach (var row in request.Rows)
            {
                var key = row.HasKey ? row.Key : "";
                if (key.Length == 0 || key.Length > MaxKey || key.Any(ch => ch < 0x21 || ch > 0x7e))
                    return Invalid($"Status row key must be 1-{MaxKey} printable ASCII characters.");
                var text = row.HasText ? row.Text : "";
                if (text.Length > MaxText) return Invalid($"Status row {key} text exceeds {MaxText} characters.");
                IntVec3? target = null;
                if (row.Target != null)
                {
                    var cell = new IntVec3(row.Target.X, 0, row.Target.Z);
                    if (cell.InBounds(map)) target = cell;
                }
                var severity = row.Severity switch
                {
                    Presentation.StatusSeverity.Critical => StatusStripSeverity.Critical,
                    Presentation.StatusSeverity.Warning => StatusStripSeverity.Warning,
                    _ => StatusStripSeverity.Info,
                };
                rows.Add(new StatusStripRow(key, text, severity, target, row.Detail));
            }
            GovernorStatusStrip.Replace(map, rows);
            return Applied(context);
        }

        private static Presentation.StatusStripReply Applied(Common.ObservationContext context) =>
            new Presentation.StatusStripReply { Applied = new Presentation.StatusStripApplied { Context = context, Rows = (uint)GovernorStatusStrip.Count } };

        private static Presentation.StatusStripReply Invalid(string detail) =>
            new Presentation.StatusStripReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, detail) };
    }
}
