using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CombatWaitTemperatureTests
{
    [Fact]
    public void AWeatherWaitEndsAtTheCatalogRaidWindow()
    {
        var view = Raid(new OutdoorTemperature.Measured(-20)) with { RaidWait = new RaidWaitWindow.Measured(10) };
        var (_, _, waiting) = CombatDecideRules.DecideCombat(view, new() { Answered = true }, new(), Fighting());
        Assert.True(waiting.Wait);
        var (_, _, finished) = CombatDecideRules.DecideCombat(view with { Tick = new(110) }, new() { Answered = true }, new(), waiting);
        Assert.False(finished.Wait);
        Assert.Throws<RequiredFactException>(() => CombatDecideRules.DecideCombat(view with { RaidWait = new RaidWaitWindow.Unread() }, new() { Answered = true }, new(), Fighting()));
    }

    private static CombatView Raid(OutdoorTemperature temperature) => CombatPawnFixtures.View() with
    {
        Tick = new(100),
        OutdoorTemperature = temperature,
        Defenders = [new() { Id = new("guard"), Armed = true, ViolenceCapable = true, HealthFraction = 1, Armor = new DefenderArmor.Unmirrored() }],
        Threats = [new() { Id = new("raider"), Humanlike = true, BodySize = 1, Anomaly = new PawnAnomalyState.Inactive() }],
        Pawns = [CombatPawnFixtures.Pawn(new("guard"), new(5, 5)), CombatPawnFixtures.Pawn(new("raider"), new(15, 15))],
        Orderable = [new("guard")],
        Rooms = [new() { Interior = new(4, 4, 3, 3), Doors = [new(5, 3)], Roofed = true }],
    };

    private static CombatMemory Fighting() => new()
    {
        Tactic = CombatTactic.Squad,
        Roles = [new() { Pawn = new("guard"), Target = new("raider") }],
    };

    [Theory]
    [InlineData(-20.01, true)]
    [InlineData(-20, true)]
    [InlineData(-19.99, false)]
    [InlineData(44.99, false)]
    [InlineData(45, true)]
    [InlineData(45.01, true)]
    public void AnEvenRaidWaitsOnlyAtTheMeasuredExtremeWeatherBounds(double temperature, bool waits)
    {
        var (orders, ask, memory) = CombatDecideRules.DecideCombat(Raid(new OutdoorTemperature.Measured(temperature)), new() { Answered = true }, new(), Fighting());

        Assert.Null(ask);
        Assert.Equal(waits, memory.Wait);
        if (waits)
        {
            Assert.Equal(new Tick(100), memory.WaitSince);
            Assert.Equal(default, Assert.Single(memory.Roles).Target);
            Assert.Contains(memory.WaitDoors, door => door.Mode == DoorMode.Forbid);
            Assert.DoesNotContain(orders, Offensive);
        }
    }

    private static bool Offensive(CombatOrder order) => order.Kind is CombatOrderKind.Attack or CombatOrderKind.AttackGround
        or CombatOrderKind.ManMortar or CombatOrderKind.Cast or CombatOrderKind.Permit or CombatOrderKind.Release
        || order.Kind == CombatOrderKind.MortarFire && !order.Clear
        || order.Kind == CombatOrderKind.FireMode && order.FireMode == CombatFireRules.FireAtWill;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWeatherWaitStopsAnActiveAttackAndKeepsAnimalsInside(bool ranged)
    {
        var view = Raid(new OutdoorTemperature.Measured(-20));
        view = view with
        {
            Defenders = [view.Defenders[0] with { RangedEquipped = ranged }],
            Pawns = [view.Pawns[0] with
            {
                Job = "Wait_Combat",
                Presence = new CombatPresence.Engaged(CombatPawnFixtures.Row() with
                {
                    Target = new("raider"), Stance = CombatStance.Warmup, FireMode = DraftFireMode.FireAtWill,
                }),
            }, view.Pawns[1], CombatPawnFixtures.Pawn(new("animal"), new(5, 3)) with { Animal = true }],
        };
        var input = Fighting();
        input.Roles = [input.Roles[0] with { Ranged = ranged, Fire = new RoleFire.AtGround(new(15, 15)) }];
        var (orders, ask, memory) = CombatDecideRules.DecideCombat(view, new() { Answered = true }, new(), input);

        Assert.Null(ask);
        Assert.True(memory.Wait);
        var role = Assert.Single(memory.Roles);
        Assert.Equal(default, role.Target);
        Assert.IsType<RoleFire.Own>(role.Fire);
        Assert.DoesNotContain(orders, Offensive);
        Assert.Contains(orders, o => o.Pawn == new PawnId("guard") && o.Kind == CombatOrderKind.Stop);
        if (ranged) Assert.Contains(orders, o => o.Pawn == new PawnId("guard") && o.Kind == CombatOrderKind.FireMode && o.FireMode == CombatFireRules.HoldFire);
        Assert.Contains(orders, o => o.Pawn == new PawnId("animal") && o.Kind == CombatOrderKind.AnimalArea && o.Cell == new Cell(5, 4));
        Assert.Equal(new PawnId("raider"), Assert.Single(input.Roles).Target);
    }

    [Fact]
    public void AFrameWithoutColonyFactsCannotStartAWeatherWait()
    {
        var (_, _, memory) = CombatDecideRules.DecideCombat(Raid(new OutdoorTemperature.NoColonyFacts()), new() { Answered = true }, new(), Fighting());

        Assert.False(memory.Wait);
    }
}
