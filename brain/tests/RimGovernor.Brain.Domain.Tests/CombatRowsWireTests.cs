using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using K = RimGovernor.Contracts.Wire.Clock;
using M = RimGovernor.Contracts.Wire.Mirror;
using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Domain.Tests;

// #3780: the frame's combat rows are whole. A pawn row reads every field it carries; the drafted fire mode, the worn
// shield, the colonists' drug scan, an event's stop kind and a pod landing are named records present only when they hold.
public sealed class CombatRowsWireTests
{
    private static void RoundTrips<T>(T payload)
    {
        var json = WireJson.Serialize(payload);
        Assert.Equal(json, WireJson.Serialize(WireJson.Deserialize<T>(json)));
        Assert.Equal(payload, WireJson.Deserialize<T>(json));
    }

    private static M.CombatPawn Pawn(M.CombatSide side) => new()
    {
        Id = new PawnId("Thing_Human1"),
        Side = side,
        Cell = new C.Cell { X = 3, Z = 4 },
        Downed = false,
        Dead = false,
        Stance = M.CombatStance.Warmup,
        Health = 0.75,
        MoveSpeed = 4.6,
        Armor = 0.35,
        MeleePower = 2.1,
        Changed = new M.Watermark { Tick = 1200, Seq = 3 },
        MedicalSkill = 0,
        GoJuiceHigh = false,
        LuciferiumAddicted = false,
        StunTicksLeft = 0,
        TargetMortar = false,
    };

    [Fact]
    public void ABarePawnRowRoundTripsExactly() => RoundTrips(Pawn(M.CombatSide.WildAnimal));

    [Fact]
    public void ADraftedShieldedColonistRowRoundTripsExactly() =>
        RoundTrips(Pawn(M.CombatSide.Colonist) with
        {
            Draft = new M.CombatDraft { FireMode = Op.CombatFireMode.Hold },
            Shield = new M.CombatShield { Energy = 0.4 },
            CarriedDrugs = new M.CarriedDrugs { Defs = ["GoJuice", "Yayo"] },
            Job = "AttackStatic",
            TargetId = "Thing_Raider1",
            Weapon = "Gun_AssaultRifle",
            MedicalSkill = 8,
            StunTicksLeft = 60,
        });

    [Fact]
    public void TheDoorAndMortarRowsRoundTripExactly()
    {
        RoundTrips(new M.CombatDoorRow { Cell = new C.Cell { X = 1, Z = 1 } });
        RoundTrips(new M.CombatMortarRow { Id = "Turret_Mortar1", Cell = new C.Cell { X = 5, Z = 5 }, MinRange = 29.9f, MaxRange = 500f });
        RoundTrips(new M.CombatMortarRow { Id = "Turret_Mortar2", Cell = new C.Cell { X = 6, Z = 5 }, MinRange = 29.9f, MaxRange = 500f, LoadedShell = "Shell_HighExplosive" });
    }

    [Fact]
    public void EventRowsRoundTripExactly()
    {
        var at = new M.Watermark { Tick = 900, Seq = 1 };
        RoundTrips(new M.CombatEventRow { At = at, Kind = M.CombatLogKind.ShotFired, ThingId = "Thing_Human1" });
        RoundTrips(new M.CombatEventRow { At = at, Kind = M.CombatLogKind.Downed, Stop = new M.CombatStop { Kind = K.CombatEvent.Downed }, ThingId = "Thing_Raider1" });
        RoundTrips(new M.CombatEventRow
        {
            At = at,
            Kind = M.CombatLogKind.HostileArrived,
            RaidStrategy = "pods",
            Pods = new M.PodLanding { OpenTick = 1100, LandingCells = [new C.Cell { X = 8, Z = 8 }, new C.Cell { X = 9, Z = 8 }] },
        });
    }

    [Fact]
    public void ARowWithoutARequiredFieldIsRefused() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<M.CombatEventRow>("{\"kind\":\"COMBAT_LOG_KIND_DOWNED\"}"));
}
