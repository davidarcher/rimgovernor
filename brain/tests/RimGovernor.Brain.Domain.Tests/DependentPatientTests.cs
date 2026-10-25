using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class DependentPatientTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
    private static O.StatusSnapshot Status(params O.DependentPatientObservation[] patients) =>
        EmergencyFixtures.Status("doctor") with { DependentPatients = new() { Patients = EquatableArray.From(patients) } };
    private static O.PawnState Pawn(string id, bool injured = false) => EmergencyFixtures.Placed(id) with
    {
        Downed = injured,
        Health = PawnFixtures.Healthy with { Bleeding = injured, NeedsTend = injured },
    };
    private static O.DependentPatientObservation Patient(string id, DependentPatientGroup group, bool rescue, bool tend) =>
        new() { Pawn = new() { Id = id }, Group = group, Rescue = rescue, Tend = tend };
    private static EmergencyFacts Decode(O.StatusSnapshot status, params O.PawnState[] rows) =>
        FacadeNativeClient.EmergencyDecode.DecodeEmergencyStatus(status, Pawns.Of(rows)).Facts;
    private static EmergencySnapshot Snapshot(EmergencyFacts facts) => EmergencySnapshot.New(World, new(100), facts);

    [Fact]
    public void DependentPatientsWakeExistingMedicalIncidentWithoutEnteringColonistRoster()
    {
        var facts = Decode(Status(Patient("guest", DependentPatientGroup.HostedGuest, true, true),
            Patient("refugee", DependentPatientGroup.RescueObjective, true, false),
            Patient("entity", DependentPatientGroup.HeldEntity, false, true)),
            Pawn("doctor"), Pawn("guest", true), Pawn("refugee", true), Pawn("entity", true));
        Assert.Equal("doctor", Assert.Single(facts.Colonists).Id.Value);
        Assert.Equal(["doctor", "guest", "refugee"], EmergencyPatientsRules.EmergencyRescuePatients(facts).Select(p => p.Value));
        Assert.Equal(["doctor", "guest", "entity"], EmergencyPatientsRules.EmergencyTendPatients(facts).Select(p => p.Value));
        var snapshot = Snapshot(facts);
        Assert.Equal(3, EmergencyNeedsRules.EmergencyNeeds(snapshot, World, new(100)).Patients);
        Assert.Equal(3, EmergencyNeedsRules.UrgentPatients(snapshot, World, new(100)));
        Assert.Equal((3L, 3L), EmergencyNeedsRules.PatientCounts(facts, []));
        Assert.Equal(3, EmergencyRules.EvaluateEmergency(snapshot, World, new(100)).Holds.Count(h => h.Reason == EmergencyReason.CriticalMedical));
    }

    [Fact]
    public void AnUnseenPatientCannotBecomeKnownEmptyCare()
    {
        var patient = Patient("guest", DependentPatientGroup.HostedGuest, true, true);
        var unresolved = Decode(Status(patient), Pawn("doctor"));
        Assert.Throws<RequiredFactException>(() => EmergencyNeedsRules.PatientCounts(unresolved, []));
        Assert.Equal(["guest"], unresolved.Unseen.Select(p => p.Value));
        Assert.Throws<RequiredFactException>(() => EmergencyNeedsRules.EmergencyNeeds(Snapshot(unresolved), World, new(100)));
        Assert.Contains(EmergencyRules.EvaluateEmergency(Snapshot(unresolved), World, new(100)).Holds,
            h => h.Reason == EmergencyReason.StaleFacts && h.Pawn.Value == "guest");
        var empty = Decode(Status(), Pawn("doctor"));
        Assert.True(EmergencyRules.EvaluateEmergency(Snapshot(empty), World, new(100)).Clear);
        Assert.Equal(0, EmergencyNeedsRules.EmergencyNeeds(Snapshot(empty), World, new(100)).Patients);
    }

    [Fact]
    public void RecoveredDependentDoesNotKeepEmergencyOpen()
    {
        var facts = Decode(Status(Patient("guest", DependentPatientGroup.HostedGuest, true, true)), Pawn("doctor"), Pawn("guest"));
        Assert.True(EmergencyRules.EvaluateEmergency(Snapshot(facts), World, new(100)).Clear);
        Assert.Equal(0, EmergencyNeedsRules.EmergencyNeeds(Snapshot(facts), World, new(100)).Patients);
        var unknown = facts with { Unseen = [new("guest")] };
        Assert.Throws<RequiredFactException>(() => EmergencyNeedsRules.EmergencyNeeds(Snapshot(unknown), World, new(100)));
    }
}
