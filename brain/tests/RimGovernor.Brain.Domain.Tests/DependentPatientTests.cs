using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.Policy;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class DependentPatientTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
    private static readonly C.Identity Identity = new() { ColonyId = "colony", MapId = 1, LoadToken = "load" };
    private static O.StatusSnapshot Status(params O.DependentPatientObservation[] patients) => new()
    {
        Context = new() { Identity = Identity, Tick = 100 }, Threats = new(), Colonists = [new() { Id = "doctor" }],
        DependentPatients = new() { Patients = EquatableArray.From(patients) }
    };
    private static O.PawnState Pawn(string id, bool injured = false) => new()
    {
        Pawn = new() { Id = id }, Dead = false, Downed = injured, InBed = false,
        Health = new() { Bleeding = injured, NeedsTend = injured }
    };
    private static O.DependentPatientObservation Patient(string id, DependentPatientGroup group, bool rescue, bool tend) =>
        new() { Pawn = new() { Id = id }, Group = group, Rescue = rescue, Tend = tend };
    private static EmergencyFacts Decode(O.StatusSnapshot status, params O.PawnState[] rows) =>
        FacadeNativeClient.EmergencyDecode.DecodeEmergencyStatus(status, Pawns.Of(rows), Identity).Facts;
    private static EmergencySnapshot Snapshot(EmergencyFacts facts) => EmergencySnapshot.New(World, new(100), facts);

    [Fact]
    public void DependentPatientsWakeExistingMedicalIncidentWithoutEnteringColonistRoster()
    {
        var facts = Decode(Status(Patient("guest", DependentPatientGroup.HostedGuest, true, true),
            Patient("refugee", DependentPatientGroup.RescueObjective, true, false),
            Patient("entity", DependentPatientGroup.HeldEntity, false, true)),
            Pawn("doctor"), Pawn("guest", true), Pawn("refugee", true), Pawn("entity", true));
        Assert.Equal("doctor", Assert.Single(facts.Colonists).Id.Value);
        Assert.Equal(["doctor", "guest", "refugee"], EmergencyRescuePatients(facts).Select(p => p.Value));
        Assert.Equal(["doctor", "guest", "entity"], EmergencyTendPatients(facts).Select(p => p.Value));
        var snapshot = Snapshot(facts);
        Assert.Equal(3, EmergencyNeeds(snapshot, World, new(100)).Patients.Value);
        Assert.Equal(3, UrgentPatients(snapshot, World, new(100)).Value);
        Assert.Equal(3, EvaluateEmergency(snapshot, World, new(100)).Holds.Count(h => h.Reason == EmergencyReason.CriticalMedical));
    }

    [Fact]
    public void MissingCensusOrHealthOrCapabilityCannotBecomeKnownEmptyCare()
    {
        var patient = Patient("guest", DependentPatientGroup.HostedGuest, true, true);
        var missing = Decode(Status() with { DependentPatients = null }, Pawn("doctor"));
        var unresolved = Decode(Status(patient), Pawn("doctor"));
        var unknownCapability = Decode(Status(patient with { Tend = null }), Pawn("doctor"), Pawn("guest", true));
        foreach (var facts in new[] { missing, unresolved, unknownCapability })
        {
            Assert.False(EmergencyNeeds(Snapshot(facts), World, new(100)).Patients.IsKnown);
            Assert.Contains(EvaluateEmergency(Snapshot(facts), World, new(100)).Holds, h => h.Reason == EmergencyReason.UnknownFacts);
        }
        var empty = Decode(Status(), Pawn("doctor"));
        Assert.True(EvaluateEmergency(Snapshot(empty), World, new(100)).Clear);
        Assert.Equal(0, EmergencyNeeds(Snapshot(empty), World, new(100)).Patients.Value);
    }

    [Fact]
    public void DuplicateOrUnknownPatientMembershipIsRejected()
    {
        var guest = Patient("guest", DependentPatientGroup.HostedGuest, true, true);
        Assert.Throws<BridgeContractException>(() => Decode(Status(guest, guest), Pawn("doctor"), Pawn("guest")));
        Assert.Throws<BridgeContractException>(() => Decode(Status(guest with { Pawn = new() { Id = "doctor" } }), Pawn("doctor")));
        Assert.Throws<BridgeContractException>(() => Decode(Status(guest with { Group = (DependentPatientGroup)999 }), Pawn("doctor"), Pawn("guest")));
    }

    [Fact]
    public void RecoveredDependentDoesNotKeepEmergencyOpen()
    {
        var facts = Decode(Status(Patient("guest", DependentPatientGroup.HostedGuest, true, true)), Pawn("doctor"), Pawn("guest"));
        Assert.True(EvaluateEmergency(Snapshot(facts), World, new(100)).Clear);
        Assert.Equal(0, EmergencyNeeds(Snapshot(facts), World, new(100)).Patients.Value);
        var unknown = facts with { DependentPatients = default };
        Assert.False(EmergencyNeeds(Snapshot(unknown), World, new(100)).Patients.IsKnown);
    }
}
