using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CombatStateContinuationTests
{
    private static readonly PawnId Guard = new("guard");
    private static readonly PawnId Raider = new("raider");
    private static readonly Cell Home = new(5, 5);

    private static CombatView Fight(bool ranged = true) => CombatPawnFixtures.View() with
    {
        Tick = new(100),
        Defenders = [new() { Id = Guard, Armed = true, RangedEquipped = ranged, ViolenceCapable = true, HealthFraction = 1, Armor = new DefenderArmor.Unmirrored() }],
        Threats = [new() { Id = Raider, Humanlike = true, BodySize = 1, Anomaly = new PawnAnomalyState.Inactive() }],
        Pawns = [CombatPawnFixtures.Pawn(Guard, Home), CombatPawnFixtures.Pawn(Raider, new(30, 5))],
        Orderable = [Guard],
    };

    private static CombatView Siege(string toil) => Fight() with
    {
        Positional = [new() { Id = Raider, Dead = false, Downed = false, Humanlike = true, LordJobClass = "LordJob_Siege", LordToilClass = toil, Position = new(30, 5), NearestColonist = new NearestColonist.None(), Mech = false }],
    };

    [Fact]
    public void SeeingTheCampStartsASortieAndGunnersShootTheConstructionFrame()
    {
        var view = Siege("LordToil_Siege");
        var frame = new PawnId("sandbag_frame");
        view = view with { Pawns = [view.Pawns[0], view.Pawns[1] with { Job = "FinishFrame", Presence = new CombatPresence.Engaged(CombatPawnFixtures.Row() with { Target = frame }) }] };
        var original = new CombatMemory();

        var (orders, ask, memory) = CombatDecideRules.DecideCombat(view, new() { Answered = true }, new(), original);

        Assert.Null(ask);
        Assert.Equal(view.Tick, memory.SiegeCamp);
        Assert.Equal(SiegeMode.Sortie, memory.SiegeMode);
        Assert.Contains(orders, order => order.Pawn == Guard && order.Kind == CombatOrderKind.Attack && order.Target == frame);
        Assert.Equal(default, original.SiegeCamp);
        Assert.Empty(original.Roles);
    }

    [Fact]
    public void TravellingSiegeKeepsHomeGunnersFromWalkingOutToAttack()
    {
        var (orders, ask, memory) = CombatDecideRules.DecideCombat(Siege("LordToil_Travel"), new() { Answered = true }, new(), new());

        Assert.Null(ask);
        Assert.Equal(SiegeMode.Hold, memory.SiegeMode);
        Assert.Equal(default, Assert.Single(memory.Roles).Target);
        Assert.DoesNotContain(orders, order => order.Kind == CombatOrderKind.Attack);
    }

    [Fact]
    public void FirstMortarFrameEndsTheSortieAndRemainsKnownOnTheNextStop()
    {
        var view = Siege("LordToil_Siege");
        view = view with { Pawns = [view.Pawns[0], view.Pawns[1] with { Job = "FinishFrame", Presence = new CombatPresence.Engaged(CombatPawnFixtures.Row() with { TargetMortar = true }) }] };

        var (_, _, memory) = CombatDecideRules.DecideCombat(view, new() { Answered = true }, new(), new());
        var (_, _, later) = CombatDecideRules.DecideCombat(Siege("LordToil_Siege") with { Tick = new(200) }, new() { Answered = true }, new(), memory);

        Assert.True(memory.SiegeMortar);
        Assert.Equal(SiegeMode.Harass, memory.SiegeMode);
        Assert.True(later.SiegeMortar);
        Assert.Equal(new Tick(100), later.SiegeCamp);
        Assert.Equal(SiegeMode.Harass, later.SiegeMode);
    }

    [Fact]
    public void GunnerInterceptsTheDiggerThenReturnsToTheBreachWhenDiggingStops()
    {
        var view = Fight() with
        {
            Pawns = [CombatPawnFixtures.Pawn(Guard, Home) with { WeaponFacts = new() { Ranged = true, Reach = 20 } }, CombatPawnFixtures.Pawn(Raider, new(30, 5)) with { Sapper = true, Job = "Mine" }],
            Rooms = [new() { Interior = new(4, 4, 4, 4) }],
        };

        var (orders, _, memory) = CombatDecideRules.DecideCombat(view, new() { Answered = true }, new(), new());

        Assert.True(memory.Intercept);
        Assert.Equal(CombatDuty.Interceptor, Assert.Single(memory.Roles).Duty);
        Assert.Contains(orders, order => order.Pawn == Guard && order.Kind == CombatOrderKind.Move && order.Cell == new Cell(15, 5));

        var stopped = view with { Tick = new(200), Pawns = [view.Pawns[0], view.Pawns[1] with { Job = "" }] };
        var (_, _, returned) = CombatDecideRules.DecideCombat(stopped, new() { Answered = true }, new(), memory);

        Assert.False(returned.Intercept);
        Assert.Equal(new CombatPost.At(new Cell(5, 5)), Assert.Single(returned.Roles).Post);
        Assert.Equal(CombatDuty.None, Assert.Single(returned.Roles).Duty);
        Assert.True(memory.Intercept);
    }

    [Fact]
    public void BrawlerLeavesTheBreachPostToAttackWhenTheSapperCloses()
    {
        var view = Fight(ranged: false) with
        {
            Pawns = [CombatPawnFixtures.Pawn(Guard, Home), CombatPawnFixtures.Pawn(Raider, new(10, 5)) with { Sapper = true }],
            Rooms = [new() { Interior = new(4, 4, 4, 4) }],
        };

        var (orders, _, memory) = CombatDecideRules.DecideCombat(view, new() { Answered = true }, new(), new());

        Assert.True(memory.Rushing);
        Assert.IsType<CombatPost.Free>(Assert.Single(memory.Roles).Post);
        Assert.Contains(orders, order => order.Pawn == Guard && order.Kind == CombatOrderKind.Attack && order.Target == Raider);
    }

    [Fact]
    public void RocketCarrierShootsTheClumpAndDropsTheGroundAimWhenItDisperses()
    {
        PawnId[] foes = [Raider, new("raider2"), new("raider3")];
        var view = Fight() with
        {
            Pawns = EquatableArray.From(new[] { CombatPawnFixtures.Pawn(Guard, Home) with { WeaponFacts = new() { Ranged = true, Reach = 40, OneUse = true } } }.Concat(foes.Select((id, i) => CombatPawnFixtures.Pawn(id, new Cell(20 + i, 5))))),
            Threats = EquatableArray.From(foes.Select(id => new SquadThreatFacts { Id = id, Humanlike = true, BodySize = 0.25, Anomaly = new PawnAnomalyState.Inactive() })),
        };
        var original = new CombatMemory { Tactic = CombatTactic.Squad, Roles = [new() { Pawn = Guard, Target = Raider, Ranged = true }] };

        var (orders, _, memory) = CombatDecideRules.DecideCombat(view, new() { Answered = true }, new(), original);

        Assert.Contains(orders, order => order.Pawn == Guard && order.Kind == CombatOrderKind.AttackGround && order.Cell == new Cell(20, 5) && order.Reason == CombatOrderReason.RocketClump);
        Assert.Equal(new RoleFire.AtGround(new Cell(20, 5)), Assert.Single(memory.Roles).Fire);
        Assert.IsType<RoleFire.Own>(Assert.Single(original.Roles).Fire);

        var dispersed = view with { Tick = new(200), Pawns = [view.Pawns[0], view.Pawns[1]], Threats = [view.Threats[0]] };
        var (laterOrders, _, later) = CombatDecideRules.DecideCombat(dispersed, new() { Answered = true }, new(), memory);

        Assert.IsType<RoleFire.Own>(Assert.Single(later.Roles).Fire);
        Assert.DoesNotContain(laterOrders, order => order.Kind == CombatOrderKind.AttackGround);
    }
}
