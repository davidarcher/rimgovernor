using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
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

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.AreaIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context,
            out Map? map, out Area? area, out List<IntVec3> cells)
        {
            map = null; area = null; cells = new List<IntVec3>();
            if (intent == null || !Enum.IsDefined(intent.Operation))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Area requires an operation.");
            var home = intent.Home;
            var pollution = intent.PollutionClear;
            var fixedArea = home || pollution;
            if (home && pollution || fixedArea == (intent.Key is not null) || (!fixedArea && !GameThread.IsIdentifier(intent.Key)))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Area requires exactly one of home, pollution_clear or a bot area key.");
            var op = intent.Operation;
            if (fixedArea && (op == RimGovernor.Brain.GameOrders.Operations.AreaOperation.Create || op == RimGovernor.Brain.GameOrders.Operations.AreaOperation.Delete))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The home and pollution-clear areas are never created or deleted.");
            if (op == RimGovernor.Brain.GameOrders.Operations.AreaOperation.Delete && intent.Rects.Count > 0)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Area delete takes no cells.");
            if ((op == RimGovernor.Brain.GameOrders.Operations.AreaOperation.SetCells || op == RimGovernor.Brain.GameOrders.Operations.AreaOperation.ClearCells) && intent.Rects.Count == 0)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Area cell edits need cells.");
            map = GameThread.LoadedMap(context);
            foreach (var r in intent.Rects)
            {
                if (r.MinX > r.MaxX || r.MinZ > r.MaxZ
                    || !new IntVec3(r.MinX, 0, r.MinZ).InBounds(map) || !new IntVec3(r.MaxX, 0, r.MaxZ).InBounds(map))
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Area rectangle is out of bounds.");
                for (var z = r.MinZ; z <= r.MaxZ; z++)
                    for (var x = r.MinX; x <= r.MaxX; x++)
                        cells.Add(new IntVec3(x, 0, z));
            }
            // The pollution-clear area is the game's own (Biotech only).
            if (pollution && !ModsConfig.BiotechActive)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "The pollution-clear area needs Biotech.");
            area = pollution ? map.areaManager.PollutionClear : home ? map.areaManager.Home : BotArea(map, (intent.Key ?? ""));
            if (pollution && area == null)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "The map has no pollution-clear area.");
            if (area == null && op == RimGovernor.Brain.GameOrders.Operations.AreaOperation.Create && !map.areaManager.CanMakeNewAllowed())
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The map cannot make another allowed area.");
            if (area == null && (op == RimGovernor.Brain.GameOrders.Operations.AreaOperation.SetCells || op == RimGovernor.Brain.GameOrders.Operations.AreaOperation.ClearCells))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Bot area " + (intent.Key ?? "") + " does not exist.");
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.AreaIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) =>
            Resolve(intent, context, out _, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.AreaIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var map, out var area, out var cells);
            if (failure != null) throw new ApplyRefusedException(failure);
            if (intent == null) throw new InvalidOperationException("Area intent missing");
            switch (intent.Operation)
            {
                case RimGovernor.Brain.GameOrders.Operations.AreaOperation.Create:
                    if (area == null)
                    {
                        area = new Area_Allowed((map ?? throw new System.InvalidOperationException("Required native value is unavailable: map.")).areaManager, (intent.Key ?? ""));
                        map.areaManager.AllAreas.Add(area);
                    }
                    foreach (var cell in cells) area[cell] = true;
                    break;
                case RimGovernor.Brain.GameOrders.Operations.AreaOperation.SetCells:
                    foreach (var cell in cells) (area ?? throw new System.InvalidOperationException("Required native value is unavailable: area."))[cell] = true;
                    break;
                case RimGovernor.Brain.GameOrders.Operations.AreaOperation.ClearCells:
                    foreach (var cell in cells) (area ?? throw new System.InvalidOperationException("Required native value is unavailable: area."))[cell] = false;
                    break;
                case RimGovernor.Brain.GameOrders.Operations.AreaOperation.Delete:
                    area?.Delete();
                    area = null;
                    break;
            }
            var effect = new RimGovernor.Brain.GameFacts.Receipts.AreaEffect { Home = intent.Home, Present = area != null, CellCount = area?.TrueCount ?? 0 };
            if (area != null) effect = effect with { AreaId = area.GetUniqueLoadID() };
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = effect };
        }
    }

    internal sealed class AreaActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeAreaIntent.Validate(action.Intent?.Area, context);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeAreaIntent.Apply(action.Intent?.Area, context);
    }
}
