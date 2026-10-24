using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Placement = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>refrigeration/build</c>: warm meat in an enclosed, powered room with no cooler drives the served refrigeration family
/// to place one Cooler on the room's wall with its cold side inside and its hot side outdoors; colonists build it and the
/// meat chills to the policy's exit threshold. The old case then measured CompRottable stopping under 0 C, vanilla
/// physics past the cap, dropped with the seeded rotting stack.
/// </summary>
internal static class RefrigerationBuild
{
    private static readonly FixtureOp<NoArgs, Fx.RefrigerationPrepared> Prepare = new(
        "test/refrigeration_prepare",
        _ => ServedOps.Args(
            ("existingCooler", false),
            ("disconnected", false),
            ("roomTemperatureC", 30),
            ("season", false),
            ("foodDef", "Meat_Muffalo"),
            ("rotStacks", 0),
            ("roomWidth", 6),
            ("roomHeight", 4)),
        ServedOps.Reply<Fx.RefrigerationPrepared>("test/refrigeration_prepare"));

    private static readonly FixtureOp<string, Fx.MeatProbe> Probe = new(
        "test/refrigeration_rot",
        id => ServedOps.Args(("id", id)),
        ServedOps.Reply<Fx.MeatProbe>("test/refrigeration_rot"));

    internal static TypedCase Case() => new()
    {
        Name = "refrigeration/build",
        Scope = "On the lab test/refrigeration_prepare builds an enclosed roofed 6x4 room of warm meat under a heat wave, with a "
            + "fuelled generator, wall-ring conduits, Cooler stock and research and no cooler: under the served refrigeration and "
            + "work families exactly one built Cooler stands on a room wall cell with its cold side inside and hot side outdoors, "
            + "set at or under the freezer target, and the warmest meat stack reads at or under the policy's chilled exit threshold.",
        Ticks = 30000,
        Tier = CaseTier.Nightly,
        Ops = [Prepare.Tool, Probe.Tool],
        Crew = new Crew { Size = 3 },

        // "work" rides along: a building method's builder check requires the colony's work priorities to match the
        // controller's own assignment, which only the work family applies.
        Serve = new ServeSpec { Families = ["refrigeration", "work"], Prefix = "refrigeration-accept" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var policy = FoodStorageUpkeepRules.DefaultFoodStoragePolicy();
        var room = await s.FixtureAsync("prepare", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        if (!room.Success || room.Meat.Count == 0 || room.Walls.Count == 0)
        {
            throw new AcceptanceException($"the fixture built no meat room: {room.Meat.Count} stacks, {room.Walls.Count} wall cells");
        }

        var warm = await WarmestAsync(s, room, "meat-before", cancellationToken).ConfigureAwait(false);
        s.Record("meat_before_c", warm);
        if (warm is not { } before || before <= policy.ChilledExitC)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"meat-before: the meat reads {warm} C, not warm above the exit threshold {policy.ChilledExitC} C"));
        }

        IReadOnlyList<Observations.BuildingState> coolers = [];
        _ = await s.ServeUntilAsync("chilled", s.TicksLeft, async ct =>
        {
            coolers = await s.BuildingsAsync("coolers", new Observations.ListBuildingsRequest { DefNames = ["Cooler"], Statuses = [Observations.BuildingStatus.Built] }, ct).ConfigureAwait(false);
            warm = await WarmestAsync(s, room, "meat", ct).ConfigureAwait(false);
            return (Wait.Signature(coolers.Count, warm), coolers.Count > 0 && warm <= policy.ChilledExitC);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("coolers_built", coolers.Count);
        s.Record("meat_after_c", warm);
        if (coolers.ToList() is not [var cooler])
        {
            throw new AcceptanceException($"want exactly one built Cooler, got {coolers.Count}");
        }

        s.RecordWire("cooler", cooler);

        var at = (cooler.Building.Place as RimGovernor.Contracts.Wire.Observations.EntityPlace.OnMap)?.Cell ?? throw new AcceptanceException("the cooler has no position");
        var cell = new Cell(at.X, at.Z);
        if (!room.Walls.Contains(new Fx.StageCell(cell.X, cell.Z)) || cooler.Rotation == Placement.Rotation.Unspecified)
        {
            throw new AcceptanceException($"cooler at {cell} facing {cooler.Rotation} is not on a fixture wall cell");
        }

        var rotation = (Rotation)((int)cooler.Rotation - 1);
        var (cold, hot) = (RefrigerationRules.ColdSide(cell, rotation), RefrigerationRules.HotSide(cell, rotation));
        if (!Inside(room.Interior, cold) || Inside(room.Interior, hot) || room.Walls.Contains(new Fx.StageCell(hot.X, hot.Z)))
        {
            throw new AcceptanceException($"cooler at {cell} facing {cooler.Rotation} has cold side {cold} and hot side {hot}; want cold inside and hot outdoors");
        }

        if (cooler.Settings.Control is not Observations.BuildingControl.Temperature { TargetC: var target } || target > policy.FreezerTargetC)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"cooler target {(cooler.Settings.Control as Observations.BuildingControl.Temperature)?.TargetC} C is not at or under the freezer target {policy.FreezerTargetC} C"));
        }
    }

    private static bool Inside(Fx.RoomInterior r, Cell c) => c.X >= r.MinX && c.X <= r.MaxX && c.Z >= r.MinZ && c.Z <= r.MaxZ;

    /// <summary>The warmest standing meat stack's ambient temperature; null when every stack is gone.</summary>
    private static async Task<double?> WarmestAsync(TypedSession s, Fx.RefrigerationPrepared room, string label, CancellationToken cancellationToken)
    {
        double? warmest = null;
        foreach (var id in room.Meat)
        {
            var stack = await s.FixtureAsync(label, Probe, id, cancellationToken).ConfigureAwait(false);
            if (!stack.Destroyed && stack.TemperatureC is { } c && (warmest is null || c > warmest))
            {
                warmest = c;
            }
        }

        return warmest;
    }
}
