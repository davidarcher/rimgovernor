using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class EmergencyNativeOwnershipTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static EmergencyFacts Decode(RimGovernor.Brain.GameFacts.Observations.StatusSnapshot status, params RimGovernor.Brain.GameFacts.Observations.PawnState[] rows) =>
        GameClient.EmergencyDecode.DecodeEmergencyStatus(status, Pawns.Of(rows)).Facts;

    [Fact]
    public void ColonistsAndDependentsRetainActualPawnAndMentalStateEvidence()
    {
        var colonist = EmergencyFixtures.Placed("colonist") with
        {
            InBed = true,
            Health = PawnFixtures.Healthy with { Bleeding = true },
            MentalState = new RimGovernor.Brain.GameFacts.Observations.PawnMentalState { DefName = "Berserk", IsAggro = true, Ticks = 30 },
        };
        var dependent = EmergencyFixtures.Placed("guest") with { Downed = true, Health = PawnFixtures.Healthy with { NeedsTend = true } };
        var status = EmergencyFixtures.Status("colonist") with
        {
            DependentPatients = new() { Patients = [new() { Pawn = new() { Id = "guest" }, Group = DependentPatientGroup.HostedGuest, Rescue = true, Tend = true }] },
        };
        var facts = Decode(status, colonist, dependent);

        var person = Assert.Single(facts.Colonists);
        Assert.Same(colonist, person.Observed);
        Assert.Same(colonist.MentalState, person.MentalState);
        Assert.Same(colonist.Needs.Food, person.Food);
        Assert.Same(dependent, Assert.Single(facts.DependentPatients).Pawn.Observed);
        var holds = EmergencyRules.EvaluateFacts(facts).Holds;
        Assert.Contains(new EmergencyHold(EmergencyReason.UnsafeThreat, new("colonist")), holds);
        Assert.Contains(new EmergencyHold(EmergencyReason.CriticalMedical, new("colonist")), holds);
        Assert.Contains(new EmergencyHold(EmergencyReason.CriticalMedical, new("guest")), holds);
    }

    [Fact]
    public void ClassificationUsesStatusEvidenceWhileSafetyUsesTheActualPawnRow()
    {
        // The status can classify a manhunter while the table's faction-hostility fact remains false.
        var pawn = EmergencyFixtures.Placed("hunter") with { Hostile = false, Animal = true };
        var census = EmergencyFixtures.ThreatPawn("hunter") with { FactionHostile = false, MentalState = "Manhunter", Passive = true };
        var status = EmergencyFixtures.Status() with { Threats = new() { Pawns = [census], HostileBuildings = [] } };
        var threat = Assert.IsType<EmergencyThreat.PawnThreat>(Assert.Single(Decode(status, pawn).Threats));

        Assert.Same(pawn, threat.ObservedPawn);
        Assert.Same(census, threat.CensusThreatPawn);
        Assert.Same(census.NearestColonist, threat.Distance);
        Assert.Equal(ThreatKind.Hostile, threat.Kind);
        Assert.True(threat.Passive);
        Assert.False(threat.Holds());

        var engaging = census with { Passive = false };
        status = status with { Threats = status.Threats with { Pawns = [engaging] } };
        Assert.True(Assert.Single(Decode(status, pawn).Threats).Holds());
        Assert.False(Assert.Single(Decode(status, pawn with { Downed = true }).Threats).Holds());
        Assert.False(Assert.Single(Decode(status, pawn with { Dead = true }).Threats).Holds());
        Assert.False(Assert.Single(Decode(status, pawn with { Fogged = true }).Threats).Holds());
        status = status with { Threats = status.Threats with { Pawns = [engaging with { NearestColonist = new RimGovernor.Brain.GameFacts.Observations.ColonistDistance.Nearest(50) }] } };
        Assert.False(Assert.Single(Decode(status, pawn).Threats).Holds());
    }

    [Fact]
    public void PredatorHuntClassificationDoesNotInheritHostileDormancy()
    {
        var census = EmergencyFixtures.ThreatPawn("hunter") with { FactionHostile = false, PredatorHunt = true, Passive = true };
        var status = EmergencyFixtures.Status() with { Threats = new() { Pawns = [census], HostileBuildings = [] } };
        var threat = Assert.Single(Decode(status, EmergencyFixtures.Placed("hunter")).Threats);

        Assert.Equal(ThreatKind.HuntingPredator, threat.Kind);
        Assert.False(threat.Passive);
        Assert.True(threat.Holds());
        status = status with { Threats = status.Threats with { Pawns = [census with { Ours = true }] } };
        Assert.Equal(ThreatKind.IgnoredHunter, Assert.Single(Decode(status, EmergencyFixtures.Placed("hunter")).Threats).Kind);
    }

    [Fact]
    public void HostileBuildingsRetainTheirCensusWithoutInventingATableJoin()
    {
        var building = EmergencyFixtures.ThreatBuilding("hive");
        var status = EmergencyFixtures.Status() with { Threats = new() { Pawns = [], HostileBuildings = [building] } };
        var threat = Assert.IsType<EmergencyThreat.BuildingThreat>(Assert.Single(Decode(status).Threats));

        Assert.Same(building, threat.ObservedBuilding);
        Assert.Same(building.NearestColonist, threat.Distance);
        Assert.Equal(building.Snapshot, threat.SnapshotToken);
        Assert.Equal(4, threat.Cells.Count);
        Assert.True(threat.Holds());
        Assert.Same(building, (threat with { Mortar = true }).ObservedBuilding);
        Assert.False((threat with { ObservedBuilding = building with { Passive = true } }).Holds());
        Assert.False((threat with { ObservedBuilding = building with { Fogged = true } }).Holds());
        Assert.False((threat with { ObservedBuilding = building with { NearestColonist = new RimGovernor.Brain.GameFacts.Observations.ColonistDistance.Nearest(50) } }).Holds());
        Assert.Throws<BridgeContractException>(() => Decode(status with { Threats = status.Threats with
        {
            HostileBuildings = [building with { Building = building.Building with { Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.Unplaced() } }],
        } }));
    }

    [Fact]
    public void SnapshotOwnsCollectionsAndRetainsNativeEvidence()
    {
        var pawn = EmergencyFixtures.Placed("colonist");
        var building = EmergencyFixtures.ThreatBuilding("hive");
        var colonists = new List<EmergencyPawn> { new() { Observed = pawn } };
        var threats = new List<EmergencyThreat> { new EmergencyThreat.BuildingThreat(building) };
        var snapshot = EmergencySnapshot.New(World, new(100), new EmergencyFacts { Colonists = colonists, Threats = threats });
        colonists.Clear();
        threats.Clear();

        Assert.Same(pawn, Assert.Single(snapshot.Facts.Colonists).Observed);
        Assert.Same(building, Assert.IsType<EmergencyThreat.BuildingThreat>(Assert.Single(snapshot.Facts.Threats)).ObservedBuilding);
        Assert.False(EmergencyRules.EvaluateEmergency(snapshot, World, new(100)).Clear);
    }

    [Fact]
    public void RealAbsenceAndAnUnresolvedThreatRemainDistinct()
    {
        var pawn = EmergencyFixtures.Placed("colonist");
        var quiet = Decode(EmergencyFixtures.Status("colonist"), pawn);
        Assert.Null(Assert.Single(quiet.Colonists).MentalState);
        Assert.IsType<RimGovernor.Brain.GameFacts.Observations.FoodNeed.Lacks>(Assert.Single(quiet.Colonists).Food);
        Assert.True(EmergencyRules.EvaluateFacts(quiet).Clear);
        Assert.Empty(quiet.Threats);

        var unresolved = Decode(EmergencyFixtures.Status() with
        {
            Threats = new() { Pawns = [EmergencyFixtures.ThreatPawn("missing")], HostileBuildings = [] },
        });
        Assert.Equal(new PawnId("missing"), Assert.Single(unresolved.Unseen));
        Assert.Equal(new EmergencyHold(EmergencyReason.StaleFacts, new("missing")), Assert.Single(EmergencyRules.EvaluateFacts(unresolved).Holds));
        Assert.Empty(unresolved.Threats);
    }
}
