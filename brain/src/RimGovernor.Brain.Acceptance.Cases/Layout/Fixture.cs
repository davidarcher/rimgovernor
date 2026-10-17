using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The layout grid fixture hut (<c>test/layout_grid_prepare</c>) the shelter and sleeping cases start on, and its shell read.
/// </summary>
internal static class LayoutFixture
{
    internal const string GridPrepare = "test/layout_grid_prepare";

    /// <summary>A room-boundary piece: a finished player wall, door, autodoor or embrasure, or one planned (blueprint or frame) with the stuff it becomes.</summary>
    internal sealed record Shell(string Def, string Stuff, Cell Cell, bool Built);

    /// <summary>Every player room-boundary piece, standing or planned, from <c>observations_list_buildings</c>.</summary>
    internal static async Task<IReadOnlyList<Shell>> ShellsAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        var rows = await h.BuildingsAsync(label, new Observations.ListBuildingsRequest
        {
            Scope = TypedReads.Scope(identity),
            DefNames = ["Wall", "Door", "Autodoor", "Embrasure"],
        }, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(row => new Shell(
            row.BuildDefName ?? row.Building.DefName,
            row.Stuff ?? "",
            TypedReads.CellOf(row.Building.Position, label),
            row.Status == Observations.BuildingStatus.Built))];
    }
}
