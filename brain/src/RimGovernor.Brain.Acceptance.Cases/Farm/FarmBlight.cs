using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>farm/blight</c> (#245): vanilla never cuts a blighted plant undesignated, so the served blight family is what
/// clears a blighted crop. test/blight_prepare sows a 4x4 rice zone on a plant-free soil buffer and blights a few plants;
/// under the served brain every blighted plant leaves the ground and no plant is cut while healthy. The old case also read
/// the plan journal and waited for a second field to be sown: planner choices, dropped.
/// </summary>
internal static class FarmBlight
{
    // Vanilla blight spreads to crops within this radius; the fixture cleared every plant within it of the zone.
    private const int SpreadRadius = 4;

    private static readonly FixtureOp<NoArgs, Fx.BlightPrepared> Prepare = new(
        "test/blight_prepare",
        _ => ServedOps.Args(("blighted", 3)),
        ServedOps.Reply<Fx.BlightPrepared>("test/blight_prepare"));

    internal static TypedCase Case() => new()
    {
        Name = "farm/blight",
        Scope = "On the lab test/blight_prepare sows a 4x4 rice zone and blights three plants: the cells read around the zone "
            + "shows exactly those plants blighted, and under the served blight, work and field families every blighted plant "
            + "leaves the ground while no plant vanishes without first being read blighted (#245).",
        Ticks = 20000,
        Tier = CaseTier.Nightly,
        Ops = [Prepare.Tool],
        Crew = new Crew { Size = 3 },

        // "work" rides along so the colony's work priorities keep plant cutting on the fixture's cutter.
        Serve = new ServeSpec { Families = ["blight", "work", "field"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "blight-accept" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("prepare", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var fixture = prepared.Plants.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (!prepared.Success || fixture.Count == 0 || prepared.ZoneCells.Count == 0)
        {
            throw new AcceptanceException($"the fixture blighted no plants: {fixture.Count} plants, {prepared.ZoneCells.Count} zone cells");
        }

        var rect = await RectAsync(s, prepared.ZoneCells, cancellationToken).ConfigureAwait(false);
        var before = await CensusAsync(s, "census-before", rect, cancellationToken).ConfigureAwait(false);
        s.Record("blighted_before", before.Evidence());
        if (!before.Blighted.SetEquals(fixture))
        {
            throw new AcceptanceException($"census-before: blighted [{string.Join(", ", before.Blighted.Order(StringComparer.Ordinal))}], the fixture blighted [{string.Join(", ", fixture.Order(StringComparer.Ordinal))}]");
        }

        var everBlighted = new HashSet<string>(before.Blighted, StringComparer.Ordinal);
        var standing = new HashSet<string>(before.Plants, StringComparer.Ordinal);
        var census = before;
        _ = await s.ServeUntilAsync("cleared", s.TicksLeft, async ct =>
        {
            census = await CensusAsync(s, "census", rect, ct).ConfigureAwait(false);
            everBlighted.UnionWith(census.Blighted);
            if (standing.Except(census.Plants).FirstOrDefault(p => !everBlighted.Contains(p)) is { } healthy)
            {
                throw new AcceptanceException($"plant {healthy} left the ground without ever being read blighted");
            }

            standing.IntersectWith(census.Plants);
            return (Wait.Signature(census.Blighted.Count, census.Plants.Count), census.Blighted.Count == 0);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("blighted_after", census.Evidence());
    }

    /// <summary>The rice plant ids on the read rectangle and those of them blighted.</summary>
    private sealed record Census(HashSet<string> Plants, HashSet<string> Blighted)
    {
        /// <summary>The blighted ids, sorted.</summary>
        public JsonArray Evidence() => new([.. Blighted.Order(StringComparer.Ordinal).Select(id => (JsonNode?)id)]);
    }

    /// <summary>The zone's bounding rectangle widened by the spread radius, clamped to the map.</summary>
    private static async Task<Observations.Rectangle> RectAsync(TypedSession s, IReadOnlyList<Fx.StageCell> zone, CancellationToken cancellationToken)
    {
        var anchor = await ReadAsync(s, "map-size", Rect(0, 0, 0, 0), cancellationToken).ConfigureAwait(false);
        var width = checked((int)anchor.MapSize.Width);
        var height = checked((int)anchor.MapSize.Height);
        return Rect(
            Math.Max(0, zone.Min(c => c.X) - SpreadRadius),
            Math.Max(0, zone.Min(c => c.Z) - SpreadRadius),
            Math.Min(width - 1, zone.Max(c => c.X) + SpreadRadius),
            Math.Min(height - 1, zone.Max(c => c.Z) + SpreadRadius));
    }

    private static Observations.Rectangle Rect(int minX, int minZ, int maxX, int maxZ) => new()
    {
        Minimum = new RimGovernor.Brain.Domain.Cell { X = minX, Z = minZ },
        Maximum = new RimGovernor.Brain.Domain.Cell { X = maxX, Z = maxZ },
    };

    private static async Task<Observations.CellsSnapshot> ReadAsync(TypedSession s, string label, Observations.Rectangle rect, CancellationToken cancellationToken)
    {
        return await s.CallAsync(label, NativeOps.GetCells, new Observations.GetCellsRequest { Scope = s.Scope, Rectangle = rect }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Census> CensusAsync(TypedSession s, string label, Observations.Rectangle rect, CancellationToken cancellationToken)
    {
        var grid = MapCellGrid.Of((await ReadAsync(s, label, rect, cancellationToken).ConfigureAwait(false)).Grid);
        var plants = grid.Things().SelectMany(t => t).Where(t => t.Def == "Plant_Rice").ToList();
        return new Census(
            plants.Select(t => t.LoadId()).ToHashSet(StringComparer.Ordinal),
            plants.Where(t => t.Plant.Blighted).Select(t => t.LoadId()).ToHashSet(StringComparer.Ordinal));
    }
}
