#nullable enable
using System;
using System.Linq;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // HomeIntent on Actions/Apply (#940): Home over the missing cells of one
    // exact facility's bounded batch (the building plus connected enclosed
    // roofed rooms, or a stockpile's cells), recomputed live at apply. A batch
    // already all Home applies again. Never removes Home, paints terrain or
    // touches allowed areas.
    internal sealed class HomeActionHandler : IActionHandler
    {
        private const string Kind = "Home extension";

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.Home;
            var map = ProtoBoundary.LoadedMap(context);
            var rules = new ApplyPreconditions(Kind)
                .Require(() => intent != null && intent.HasTargetId && ProtoBoundary.IsIdentifier(intent.TargetId), "extension requires a facility identity")
                .Present(() => HomeCoverage.Scope(map, intent!.TargetId) != null, "bounded visible native facility geometry is unavailable");
            return rules.Holds ? null : rules.Failure();
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var target = action.Home.TargetId;
            var cells = HomeCoverage.Scope(map, target) ?? throw new InvalidOperationException("Home extension geometry changed before apply.");
            var missing = cells.Where(c => !map.areaManager.Home[c]).ToList();
            foreach (var c in missing) map.areaManager.Home[c] = true;
            var shape = HomeCoverage.Shape(target, cells);
            return new Receipts.EffectEvidence { Home = new Receipts.HomeEffect { ShapeToken = shape, Revision = HomeCoverage.State(map).Revision,
                ChangedCells = missing.Count, Covered = cells.All(c => map.areaManager.Home[c]),
                Snapshot = new Receipts.SnapshotEvidence { EntityId = target, BeforeToken = shape, AfterToken = shape } } };
        }
    }
}
