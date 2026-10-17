using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The tiered colony layout cases' shared fixture constants and plan check, staged on the layout grid fixture hut
/// (<c>test/layout_grid_prepare</c>) (Go <c>fixture.go</c>).
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
            Statuses = ["all"],
            PlayerOnly = true,
        }, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(row => new Shell(
            row.BuildDefName ?? row.Building?.DefName ?? "",
            row.Stuff ?? "",
            TypedReads.CellOf(row.Building?.Position, label),
            row.Status == Observations.BuildingStatus.Built))];
    }

    /// <summary>
    /// The sleeping spots the hut is furnished with: fewer than the eight colonists the baseline houses, so the
    /// capacity goal has a deficit to plan a second ring against from the first review.
    /// </summary>
    internal const int Bunks = 5;

    /// <summary>The stone blocks dropped beside the hut door: a module ring is 48 cells, and a stone wall costs five blocks.</summary>
    internal const int Blocks = 400;

    /// <summary>
    /// Reports a plan of the sampled goal, active or retired, whose method matches: with <paramref name="complete"/>
    /// set, every one of its actions has completed, otherwise it need only hold actions.
    /// </summary>
    internal static bool Planned(JsonObject? sample, Func<string, bool> match, bool complete)
    {
        var plans = sample?["plans"].AsSlice() ?? [];
        var retired = sample?["retired_plans"].AsSlice() ?? [];
        foreach (var raw in plans.Concat(retired))
        {
            var plan = raw.AsMap() ?? [];
            var method = plan["method"].AsString();
            var actions = (int)plan["actions"].AsNumber();
            var completed = (int)(plan["stages"].AsMap()?["completed"].AsNumber() ?? 0);
            if (!match(method) || actions == 0)
            {
                continue;
            }

            if (!complete || completed == actions)
            {
                return true;
            }
        }

        return false;
    }
}
