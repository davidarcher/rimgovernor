using Mirror = RimGovernor.Contracts.Wire.Mirror;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/pods</c> (#870): lab-pods' centre drop arrives as a combat_events hostile_arrived row with strategy pods,
/// its landing cells and an open tick after the arrival, and by that tick every raider is out of its pod.
/// </summary>
internal static class CombatLabPods
{
    private const string PodsStrategy = "pods";

    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-pods");

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/pods",
        Scope = "The drop-pod arrival fact (#870): lab-pods' centre drop arrives as a combat_events hostile_arrived row with strategy pods, its "
            + "landing cells and an open tick after the arrival; a frame past the open tick has every raider out of its pod (a combat_pawns row, "
            + "or a downing or death already logged).",
        Ticks = 3 * CombatLabKit.ProbeTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var raiders = new StagedLab(Fixture, s.Staged!).Hostiles();
        var frames = new LabFrames(s);
        Mirror.CombatEventRow? arrival = null;
        _ = await frames.UntilAsync("arrival", CombatLabKit.ProbeTicks, 0, _ =>
        {
            arrival = frames.Events.FirstOrDefault(e => e.Kind == Mirror.CombatLogKind.HostileArrived && e.RaidStrategy == PodsStrategy);
            return arrival is not null;
        }, cancellationToken).ConfigureAwait(false);
        s.RecordWire("arrival", arrival);
        if (arrival!.OpenTick is not { } open || open <= arrival.At?.Tick || arrival.LandingCells.Count == 0)
        {
            throw new AcceptanceException($"lab-pods row without an open tick after its arrival or landing cells: {arrival}");
        }

        // Past the open tick every raider is out: a row in the frame, or (shot by the riflemen meanwhile) a logged downing or death.
        var gone = new[] { Mirror.CombatLogKind.Downed, Mirror.CombatLogKind.Killed };
        var frame = await frames.UntilAsync("opened", 2 * CombatLabKit.ProbeTicks, open + 1, _ => true, cancellationToken).ConfigureAwait(false);
        var inPod = raiders.Where(id => LabFrames.Row(frame, id) is null && !frames.Events.Any(e => e.ThingId == id && gone.Contains(e.Kind ?? default))).ToList();
        if (inPod.Count != 0)
        {
            throw new AcceptanceException($"lab-pods raiders [{string.Join(", ", inPod)}] still in their pods at tick {frame.Context?.Tick} (open tick {open})");
        }
    }
}
