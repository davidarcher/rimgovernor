#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativeOperationTools
    {

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Operations.ZonePreviewReply HandlePreviewZone(Operations.ZonePreviewRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                return new Operations.ZonePreviewReply { Outcome = invalid };
            if (parsed.Zone == null)
                return new Operations.ZonePreviewReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A zone is required.") };
            return parsed.Zone.Kind.GetValueOrDefault() == Operations.ZoneType.Stockpile
                ? StockpilePlacementActionHandler.Preview(parsed.Zone, context) : NativeZoneCreation.Preview(parsed.Zone, context);
        }
    }
}
