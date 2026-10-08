#nullable enable
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The one zone write (#1353). Its shape selects the edit (operations.proto
    // ZoneIntent): delete, create (kind set), cells (add or remove cells) or
    // settings (a stockpile body alone); each shape's handler refuses a field
    // that belongs to another.
    internal static class NativeZoneIntent
    {
        // Id is the referenced zone (or storage building) id, null when the
        // intent names none.
        internal static string? Id(Operations.ZoneIntent? intent) =>
            intent?.Zone != null && intent.Zone.HasId && ProtoBoundary.IsIdentifier(intent.Zone.Id) ? intent.Zone.Id : null;

        internal static bool Adding(Operations.ZoneIntent intent) => intent.AddCells != null;

        internal static Operations.Cells? EditCells(Operations.ZoneIntent intent) => intent.AddCells ?? intent.RemoveCells;

        // DeleteOnly is a delete naming its zone and nothing else.
        internal static bool DeleteOnly(Operations.ZoneIntent? intent) =>
            Id(intent) != null && intent!.Delete && !intent.HasKind && !intent.HasLabel && intent.AddCells == null && intent.RemoveCells == null
            && intent.Stockpile == null && intent.Growing == null && intent.Fishing == null && !intent.RequireCoveredEmpty;
    }

    internal sealed class ZoneActionHandler : IActionHandler
    {
        private static readonly IActionHandler Creation = new ZoneCreationActionHandler();
        private static readonly IActionHandler StockpilePlacement = new StockpilePlacementActionHandler();
        private static readonly IActionHandler Deletion = new ZoneDeletionActionHandler();
        private static readonly IActionHandler CellEdit = new ZoneCellsActionHandler();
        private static readonly IActionHandler Settings = new StockpileActionHandler();

        private static IActionHandler Shape(Operations.ZoneIntent? intent)
        {
            if (intent == null) return Creation;
            if (intent.HasDelete) return Deletion;
            if (intent.HasKind) return intent.Kind == Operations.ZoneType.Stockpile ? StockpilePlacement : Creation;
            if (intent.AddCells != null || intent.RemoveCells != null) return CellEdit;
            return Settings;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Shape(action.Zone).Validate(action, context);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => Shape(action.Zone).Apply(action, context);
    }
}
