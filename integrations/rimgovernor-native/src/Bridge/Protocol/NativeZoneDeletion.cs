#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The zone intent's delete shape. Mirrors ZoneCellsTool.Delete's two
    // landmine checks (phantom cells; a stockpile's haul grid pointing
    // elsewhere), which exist because Zone.Delete() loops RemoveCell and
    // RemoveCell's Notify_LostCell Log.Errors -- pausing the sim -- when the
    // grid disagrees with the zone's own cell list. See ZoneCellsTool.cs's
    // header comment for the full case. A zone already gone applies again.
    internal sealed class ZoneDeletionActionHandler : IActionHandler
    {
        private const string Kind = "Zone deletion";

        private static Zone? Resolve(Operations.ZoneIntent? intent, Map map) =>
            NativeZoneIntent.Id(intent) != null
                ? RefIndex.Zone(map, intent!.Zone.Id) is Zone z && z.Cells.Count > 0 ? z : null : null;

        private static ApplyPreconditions Rules(Operations.ZoneIntent? intent, Zone? zone, Map map)
        {
            var slotGroup = (zone as Zone_Stockpile)?.slotGroup;
            return new ApplyPreconditions(Kind)
                .Require(() => NativeZoneIntent.DeleteOnly(intent), "deletion requires a zone id and no other edit")
                .Require(() => zone == null || zone.Cells.All(c => map.zoneManager.ZoneAt(c) == zone), "the zone holds a phantom cell the zone grid does not map to it")
                .Require(() => slotGroup == null || zone!.Cells.All(c => map.haulDestinationManager?.SlotGroupAt(c) == slotGroup), "the stockpile's haul grid no longer matches its cells");
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var rules = Rules(action.Zone, Resolve(action.Zone, map), map);
            return rules.Holds ? null : rules.Failure();
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var zone = Resolve(action.Zone, map);
            var rules = Rules(action.Zone, zone, map);
            if (!rules.Holds) throw new InvalidOperationException("Zone deletion prerequisites changed before apply: " + rules.Reason);
            zone?.Delete(false);
            if (Resolve(action.Zone, map) != null) throw new InvalidOperationException("Native zone deletion readback did not apply.");
            return NativeZoneCreation.Evidence(NativeZoneIntent.Id(action.Zone)!, null, map);
        }
    }
}
