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
    // DeleteZoneIntent on Actions/Apply. Mirrors ZoneCellsTool.Delete's two
    // landmine checks (phantom cells; a stockpile's haul grid pointing
    // elsewhere), which exist because Zone.Delete() loops RemoveCell and
    // RemoveCell's Notify_LostCell Log.Errors -- pausing the sim -- when the
    // grid disagrees with the zone's own cell list. See ZoneCellsTool.cs's
    // header comment for the full case. A zone already gone applies again.
    internal sealed class ZoneDeletionActionHandler : IActionHandler
    {
        private const string Kind = "Zone deletion";

        private static Zone? Resolve(Operations.DeleteZoneIntent? intent, Map map) =>
            intent != null && intent.HasZoneId && ProtoBoundary.IsIdentifier(intent.ZoneId)
                ? map.zoneManager.AllZones.FirstOrDefault(z => z.GetUniqueLoadID() == intent.ZoneId && z.Cells.Count > 0) : null;

        private static ApplyPreconditions Rules(Operations.DeleteZoneIntent? intent, Zone? zone, Map map)
        {
            var slotGroup = (zone as Zone_Stockpile)?.slotGroup;
            return new ApplyPreconditions(Kind)
                .Require(() => intent != null && intent.HasZoneId && ProtoBoundary.IsIdentifier(intent.ZoneId), "deletion requires a zone id")
                .Require(() => zone == null || zone.Cells.All(c => map.zoneManager.ZoneAt(c) == zone), "the zone holds a phantom cell the zone grid does not map to it")
                .Require(() => slotGroup == null || zone!.Cells.All(c => map.haulDestinationManager?.SlotGroupAt(c) == slotGroup), "the stockpile's haul grid no longer matches its cells");
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var rules = Rules(action.DeleteZone, Resolve(action.DeleteZone, map), map);
            return rules.Holds ? null : rules.Failure();
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var zone = Resolve(action.DeleteZone, map);
            var rules = Rules(action.DeleteZone, zone, map);
            if (!rules.Holds) throw new InvalidOperationException("Zone deletion prerequisites changed before apply: " + rules.Reason);
            zone?.Delete(false);
            if (Resolve(action.DeleteZone, map) != null) throw new InvalidOperationException("Native zone deletion readback did not apply.");
            return NativeZoneCreation.Evidence(action.DeleteZone.ZoneId, null, map);
        }
    }
}
