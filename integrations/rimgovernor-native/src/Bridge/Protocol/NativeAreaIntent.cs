#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    // AreaIntent on Actions/Apply: create, edit or delete one
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
            if (intent == null || intent.Operation is null || intent.Operation.GetValueOrDefault() == Operations.AreaOperation.Unspecified)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Area requires an operation.");
            var home = intent.Home is not null && intent.Home.GetValueOrDefault();
            var pollution = intent.PollutionClear is not null && intent.PollutionClear.GetValueOrDefault();
            var fixedArea = home || pollution;
            if (home && pollution || fixedArea == (intent.Key is not null) || (!fixedArea && !ProtoBoundary.IsIdentifier(intent.Key)))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Area requires exactly one of home, pollution_clear or a bot area key.");
            var op = intent.Operation.GetValueOrDefault();
            if (fixedArea && (op == Operations.AreaOperation.Create || op == Operations.AreaOperation.Delete))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The home and pollution-clear areas are never created or deleted.");
            if (op == Operations.AreaOperation.Delete && intent.Rects.Count > 0)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Area delete takes no cells.");
            if ((op == Operations.AreaOperation.SetCells || op == Operations.AreaOperation.ClearCells) && intent.Rects.Count == 0)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Area cell edits need cells.");
            map = ProtoBoundary.LoadedMap(context);
            foreach (var r in intent.Rects)
            {
                if (r.MinX is null || r.MinZ is null || r.MaxX is null || r.MaxZ is null || r.MinX.GetValueOrDefault() > r.MaxX.GetValueOrDefault() || r.MinZ.GetValueOrDefault() > r.MaxZ.GetValueOrDefault()
                    || !new IntVec3(r.MinX.GetValueOrDefault(), 0, r.MinZ.GetValueOrDefault()).InBounds(map) || !new IntVec3(r.MaxX.GetValueOrDefault(), 0, r.MaxZ.GetValueOrDefault()).InBounds(map))
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Area rectangle is out of bounds.");
                for (var z = r.MinZ.GetValueOrDefault(); z <= r.MaxZ.GetValueOrDefault(); z++)
                    for (var x = r.MinX.GetValueOrDefault(); x <= r.MaxX.GetValueOrDefault(); x++)
                        cells.Add(new IntVec3(x, 0, z));
            }
            // The pollution-clear area is the game's own (Biotech only).
            if (pollution && !ModsConfig.BiotechActive)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "The pollution-clear area needs Biotech.");
            area = pollution ? map.areaManager.PollutionClear : home ? map.areaManager.Home : BotArea(map, (intent.Key ?? ""));
            if (pollution && area == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "The map has no pollution-clear area.");
            if (area == null && op == Operations.AreaOperation.Create && !map.areaManager.CanMakeNewAllowed())
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The map cannot make another allowed area.");
            if (area == null && (op == Operations.AreaOperation.SetCells || op == Operations.AreaOperation.ClearCells))
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Bot area " + (intent.Key ?? "") + " does not exist.");
            return null;
        }

        internal static Common.Failure? Validate(Operations.AreaIntent? intent, Common.ObservationContext context) =>
            Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.AreaIntent? intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var map, out var area, out var cells);
            if (failure != null) throw new ApplyRefusedException(failure);
            intent ??= new Operations.AreaIntent();
            switch (intent.Operation.GetValueOrDefault())
            {
                case Operations.AreaOperation.Create:
                    if (area == null)
                    {
                        area = new Area_Allowed(map!.areaManager, (intent.Key ?? ""));
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
            if (intent.Home is not null && intent.Home.GetValueOrDefault()) effect = effect with { Home = true };
            else if (intent.PollutionClear is not null && intent.PollutionClear.GetValueOrDefault()) effect = effect with { PollutionClear = true };
            else effect = effect with { Key = intent.Key };
            if (area != null) effect = effect with { AreaId = area.GetUniqueLoadID() };
            return new Receipts.EffectEvidence { Effect = effect };
        }
    }

    internal sealed class AreaActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeAreaIntent.Validate(action.Intent?.Area, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeAreaIntent.Apply(action.Intent?.Area, context);
    }
}
