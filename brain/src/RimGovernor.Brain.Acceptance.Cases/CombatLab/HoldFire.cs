using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/holdfire</c> (#861): fire_mode hold stops a drafted pawn shooting. On lab-ranged two drafted riflemen,
/// one stopped and held, the other attacking: the attacker fires while the held one fires nothing for at least
/// <see cref="HoldTicks"/>; then at_will and the formerly held one fires.
/// </summary>
internal static class CombatLabHoldFire
{
    /// <summary>The least the held rifleman must stay silent while the attacker fires.</summary>
    private const int HoldTicks = 600;

    /// <summary>Bounds a phase: a drafted rifleman 25 cells from a raider fires well inside it.</summary>
    private const int PhaseTicks = 1200 + CombatLabKit.ProbeTicks;

    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-ranged");

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/holdfire",
        Scope = "fire_mode hold stops a drafted pawn shooting (#861), vanilla behaviour the #850 contract only reads back as a flag: on lab-ranged "
            + "two drafted riflemen, one ordered stop and fire_mode hold, the other ordered to attack; the frames' combat events show the attacker "
            + "firing and no shot from the held one over at least 600 ticks, then fire_mode at_will and the formerly held one fires.",
        Ticks = 2 * PhaseTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var colonists = lab.Colonists();
        var hostiles = lab.Hostiles();
        var held = colonists[0];
        var shooter = colonists[1];
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        await CombatLabKit.DraftAsync(s, [held, shooter], cancellationToken).ConfigureAwait(false);
        var results = await CombatLabKit.OrdersAsync(
            s,
            "hold",
            cancellationToken,
            CombatLabKit.Order(held, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.StopCase()),
            CombatLabKit.Order(held, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.FireModeCase(RimGovernor.Brain.GameFacts.Operations.CombatFireMode.Hold)),
            CombatLabKit.Order(shooter, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AttackCase(CombatLabKit.Pawn(hostiles[1])))).ConfigureAwait(false);
        foreach (var r in results)
        {
            CombatLabKit.Applied("hold", r);
        }

        var frames = new LabFrames(s);
        var start = await CombatLabKit.TickAsync(s, "hold-start", cancellationToken).ConfigureAwait(false);
        _ = await frames.UntilAsync("held-phase", PhaseTicks, start + HoldTicks, _ => Shots(frames, start).GetValueOrDefault(shooter) > 0, cancellationToken).ConfigureAwait(false);
        var heldPhase = Shots(frames, start);
        s.Record("held_phase", Show(heldPhase));
        if (heldPhase.GetValueOrDefault(held) != 0)
        {
            throw new AcceptanceException($"held rifleman fired {heldPhase[held]} shots under hold fire");
        }

        CombatLabKit.Applied("at-will", await CombatLabKit.OrderAsync(s, "at-will", held, new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.FireModeCase(RimGovernor.Brain.GameFacts.Operations.CombatFireMode.AtWill), cancellationToken).ConfigureAwait(false));
        var release = await CombatLabKit.TickAsync(s, "at-will-start", cancellationToken).ConfigureAwait(false);
        _ = await frames.UntilAsync("at-will-phase", PhaseTicks, release, _ => Shots(frames, release).GetValueOrDefault(held) > 0, cancellationToken).ConfigureAwait(false);
        s.Record("at_will_phase", Show(Shots(frames, release)));
    }

    // The shot_fired events from tick from on, counted by shooter.
    private static SortedDictionary<string, int> Shots(LabFrames frames, long from)
    {
        var shots = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in frames.Events.Where(e => e.Kind == RimGovernor.Brain.GameFacts.Mirror.CombatLogKind.ShotFired && e.At.Tick >= from && e.ThingId is not null))
        {
            shots[e.ThingId!] = shots.GetValueOrDefault(e.ThingId!) + 1;
        }

        return shots;
    }

    private static JsonObject Show(SortedDictionary<string, int> shots) => new([.. shots.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value))]);
}
