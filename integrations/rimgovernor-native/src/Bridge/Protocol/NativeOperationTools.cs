using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace RimGovernor.Native
{
    internal static class NativeOperationTools
    {

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static RimGovernor.Brain.GameFacts.Operations.ZonePreviewReply HandlePreviewZone(RimGovernor.Brain.GameOrders.Operations.ZonePreviewRequest parsed)
        {
            if (!GameThread.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                return new RimGovernor.Brain.GameFacts.Operations.ZonePreviewReply { Outcome = invalid };
            if (parsed.Zone == null)
                return new RimGovernor.Brain.GameFacts.Operations.ZonePreviewReply { Outcome = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A zone is required.") };
            return parsed.Zone.Kind == RimGovernor.Brain.GameOrders.Operations.ZoneType.Stockpile
                ? StockpilePlacementActionHandler.Preview(parsed.Zone, context) : NativeZoneCreation.Preview(parsed.Zone, context);
        }
    }
}
