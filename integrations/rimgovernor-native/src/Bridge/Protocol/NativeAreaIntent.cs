#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // AreaIntent on Actions/Apply (#1321): create, edit or delete one
    // bot-owned Area_Allowed, or edit the home area. A bot area is the
    // Area_Allowed labelled with its key (combat animal areas are
    // "Combat <id>"); a player area is never resolved, so never touched.
    // create refuses when the map cannot make another allowed area and
    // applies again on an existing bot area; delete of a missing bot area
    // applies again. Evidence is the area's load id and cell count after the
    // edit.
    internal static class NativeAreaIntent
    {
        internal static Area_Allowed? BotArea(Map map, string key) =>
            map.areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault(a => a.Label == key);

        private static Common.Failure? Resolve(Operations.AreaIntent? intent, Common.ObservationContext context,
            out Map? map, out Area? area, out List<IntVec3> cells)
        {
            map = null; area = null; cells = new List<IntVec3>();
            if (intent == null || !intent.HasOperation || intent.Operation == Operations.AreaOperation.Unspecified)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Area requires an operation.");
            var home = intent.HasHome && intent.Home;
            if (home == intent.HasKey || (!home && !ProtoBoundary.IsIdentifier(intent.Key)))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Area requires exactly one of home or a bot area key.");
            var op = intent.Operation;
            if (home && (op == Operations.AreaOperation.Create || op == Operations.AreaOperation.Delete))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The home area is never created or deleted.");
            if (op == Operations.AreaOperation.Delete && intent.Cells.Count > 0)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Area delete takes no cells.");
            if ((op == Operations.AreaOperation.SetCells || op == Operations.AreaOperation.ClearCells) && intent.Cells.Count == 0)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Area cell edits need cells.");
            map = ProtoBoundary.LoadedMap(context);
            foreach (var c in intent.Cells)
            {
                var cell = new IntVec3(c.X, 0, c.Z);
                if (!c.HasX || !c.HasZ || !cell.InBounds(map))
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Area cell is out of bounds.");
                cells.Add(cell);
            }
            area = home ? map.areaManager.Home : BotArea(map, intent.Key);
            if (area == null && op == Operations.AreaOperation.Create && !map.areaManager.CanMakeNewAllowed())
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The map cannot make another allowed area.");
            if (area == null && (op == Operations.AreaOperation.SetCells || op == Operations.AreaOperation.ClearCells))
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Bot area " + intent.Key + " does not exist.");
            return null;
        }

        internal static Common.Failure? Validate(Operations.AreaIntent? intent, Common.ObservationContext context) =>
            Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.AreaIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var map, out var area, out var cells);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            switch (intent.Operation)
            {
                case Operations.AreaOperation.Create:
                    if (area == null)
                    {
                        area = new Area_Allowed(map!.areaManager, intent.Key);
                        map.areaManager.AllAreas.Add(area);
                    }
                    foreach (var cell in cells) area[cell] = true;
                    break;
                case Operations.AreaOperation.SetCells:
                    foreach (var cell in cells) area![cell] = true;
                    break;
                case Operations.AreaOperation.ClearCells:
                    foreach (var cell in cells) area![cell] = false;
                    break;
                case Operations.AreaOperation.Delete:
                    area?.Delete();
                    area = null;
                    break;
            }
            var effect = new Receipts.AreaEffect { Present = area != null, CellCount = area?.TrueCount ?? 0 };
            if (intent.HasHome && intent.Home) effect.Home = true;
            else effect.Key = intent.Key;
            if (area != null) effect.AreaId = area.GetUniqueLoadID();
            return new Receipts.EffectEvidence { AreaEdit = effect };
        }
    }

    internal sealed class AreaActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeAreaIntent.Validate(action.Area, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeAreaIntent.Apply(action.Area, context);
    }
}
