using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3780: the frame's combat rows are whole. A pawn row reads every field it carries; the drafted fire mode, the worn
// shield, the colonists' drug scan, an event's stop kind and a pod landing are named records present only when they hold.
public sealed class CombatRowsWireTests
{
    private static void RoundTrips<T>(T payload)
    {
        var json = OrdersJson.Serialize(payload);
        Assert.Equal(json, OrdersJson.Serialize(OrdersJson.Deserialize<T>(json)));
        Assert.Equal(payload, OrdersJson.Deserialize<T>(json));
    }

    private static RimGovernor.Brain.GameFacts.Mirror.CombatPawn Pawn(RimGovernor.Brain.GameFacts.Mirror.CombatSide side) => new()
    {
        Id = new PawnId("Thing_Human1"),
        Side = side,
        Cell = new RimGovernor.Brain.Domain.Cell { X = 3, Z = 4 },
        Downed = false,
        Dead = false,
        Stance = RimGovernor.Brain.GameFacts.Mirror.CombatStance.Warmup,
        Health = 0.75,
        MoveSpeed = 4.6,
        Armor = 0.35,
        Changed = new RimGovernor.Brain.GameFacts.Mirror.Watermark { Tick = 1200, Seq = 3 },
        MedicalSkill = 0,
        GoJuiceHigh = false,
        LuciferiumAddicted = false,
        StunTicksLeft = 0,
        TargetMortar = false,
    };

    [Fact]
    public void ABarePawnRowRoundTripsExactly() => RoundTrips(Pawn(RimGovernor.Brain.GameFacts.Mirror.CombatSide.WildAnimal));

    [Fact]
    public void ADraftedShieldedColonistRowRoundTripsExactly() =>
        RoundTrips(Pawn(RimGovernor.Brain.GameFacts.Mirror.CombatSide.Colonist) with
        {
            Draft = new RimGovernor.Brain.GameFacts.Mirror.CombatDraft { FireMode = RimGovernor.Brain.GameFacts.Operations.CombatFireMode.Hold },
            Shield = new RimGovernor.Brain.GameFacts.Mirror.CombatShield { Energy = 0.4 },
            CarriedDrugs = new RimGovernor.Brain.GameFacts.Mirror.CarriedDrugs { Defs = ["GoJuice", "Yayo"] },
            Job = "AttackStatic",
            TargetId = "Thing_Raider1",
            Weapon = "Gun_AssaultRifle",
            MedicalSkill = 8,
            StunTicksLeft = 60,
        });

    [Fact]
    public void TheDoorAndMortarRowsRoundTripExactly()
    {
        RoundTrips(new RimGovernor.Brain.GameFacts.Mirror.CombatDoorRow { Cell = new RimGovernor.Brain.Domain.Cell { X = 1, Z = 1 } });
        RoundTrips(new RimGovernor.Brain.GameFacts.Mirror.CombatMortarRow { Id = "Turret_Mortar1", Cell = new RimGovernor.Brain.Domain.Cell { X = 5, Z = 5 }, MinRange = 29.9f, MaxRange = 500f });
        RoundTrips(new RimGovernor.Brain.GameFacts.Mirror.CombatMortarRow { Id = "Turret_Mortar2", Cell = new RimGovernor.Brain.Domain.Cell { X = 6, Z = 5 }, MinRange = 29.9f, MaxRange = 500f, LoadedShell = "Shell_HighExplosive" });
    }

    [Fact]
    public void EventRowsRoundTripExactly()
    {
        var at = new RimGovernor.Brain.GameFacts.Mirror.Watermark { Tick = 900, Seq = 1 };
        RoundTrips(new RimGovernor.Brain.GameFacts.Mirror.CombatEventRow { At = at, Kind = RimGovernor.Brain.GameFacts.Mirror.CombatLogKind.ShotFired, ThingId = "Thing_Human1" });
        RoundTrips(new RimGovernor.Brain.GameFacts.Mirror.CombatEventRow { At = at, Kind = RimGovernor.Brain.GameFacts.Mirror.CombatLogKind.Downed, Stop = new RimGovernor.Brain.GameFacts.Mirror.CombatStop { Kind = RimGovernor.Brain.GameFacts.Clock.CombatEvent.Downed }, ThingId = "Thing_Raider1" });
        RoundTrips(new RimGovernor.Brain.GameFacts.Mirror.CombatEventRow
        {
            At = at,
            Kind = RimGovernor.Brain.GameFacts.Mirror.CombatLogKind.HostileArrived,
            RaidStrategy = "pods",
            Pods = new RimGovernor.Brain.GameFacts.Mirror.PodLanding { OpenTick = 1100, LandingCells = [new RimGovernor.Brain.Domain.Cell { X = 8, Z = 8 }, new RimGovernor.Brain.Domain.Cell { X = 9, Z = 8 }] },
        });
    }

    [Fact]
    public void ARowWithoutARequiredFieldIsRefused() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Mirror.CombatEventRow>("{\"kind\":\"COMBAT_LOG_KIND_DOWNED\"}"));
}
