using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The Anomaly colony section and the pawn and building blocks are total (#3773): the section is a named state
/// (<see cref="AnomalySection.Active"/> or <see cref="AnomalySection.Inactive"/> without Anomaly), every member is
/// <c>required</c>, the monolith, a held block, a study block, a creepjoiner block and a held pawn are relations (null
/// for none), and each round-trips through <c>OrdersJsonContext</c> exactly.
/// </summary>
public sealed class AnomalySectionTests
{
    /// <summary>The section of a game without Anomaly: the section every test without anomaly carries.</summary>
    internal static readonly AnomalySection NoAnomaly = new AnomalySection.Inactive();

    [Fact]
    public void ColonySectionJsonRoundTripIsExact()
    {
        AnomalySection section = new AnomalySection.Active(new AnomalyColonyFacts
        {
            Incidents = new AnomalyIncidentState { MonolithSpawned = true, Level = 1, AmbientHorrorMode = false },
            Monolith = new MonolithState
            {
                MonolithId = "Thing_VoidMonolith1",
                PendingVoidStructureIds = [],
                VoidNodeId = null,
                VoidNodePawnIds = [],
                CanActivate = false,
                NextLevelDef = "Embraced",
                CodexShortfall = 2,
                BlockingConditions = [],
                GleamingInteractionAvailable = false,
            },
        });

        var json = OrdersJson.Serialize(section);

        Assert.Equal(
            """{"type":"active","facts":{"incidents":{"monolithSpawned":true,"level":1,"ambientHorrorMode":false},"monolith":{"monolithId":"Thing_VoidMonolith1","pendingVoidStructureIds":[],"voidNodeId":null,"voidNodePawnIds":[],"canActivate":false,"nextLevelDef":"Embraced","codexShortfall":2,"blockingConditions":[],"gleamingInteractionAvailable":false}}}""",
            json);
        Assert.Equal(section, OrdersJson.Deserialize<AnomalySection>(json));
        Assert.Equal("""{"type":"inactive"}""", OrdersJson.Serialize(NoAnomaly));
        Assert.Equal(NoAnomaly, OrdersJson.Deserialize<AnomalySection>("""{"type":"inactive"}"""));

        // The colony read lifts the active section into the Anomaly state whole; a monolith block that disagrees with MonolithSpawned breaks the contract.
        var read = Assert.IsType<RimGovernor.Brain.Policy.AnomalyState.Active>(RimGovernor.Brain.Bridge.AnomalyColonyReads.AnomalyOf(section));
        Assert.Equal((1, false, "Thing_VoidMonolith1", "Embraced", 2), (read.Monolith.Incidents.Level, read.Monolith.Incidents.AmbientHorrorMode, read.Monolith.Monolith!.MonolithId, read.Monolith.Monolith.NextLevelDef, read.Monolith.Monolith.CodexShortfall));
        var active = (AnomalySection.Active)section;
        Assert.Same(active.Facts, read.Monolith);
        Assert.Throws<RimGovernor.Brain.GameAccess.BridgeContractException>(() => RimGovernor.Brain.Bridge.AnomalyColonyReads.AnomalyOf(active with { Facts = active.Facts with { Monolith = null } }));
    }

    [Fact]
    public void PawnBlockJsonRoundTripIsExact()
    {
        var anomaly = new PawnAnomaly
        {
            Entity = true,
            Mutant = false,
            MinContainmentStrength = 40,
            Held = new HeldState
            {
                Held = true,
                Mode = EntityContainmentModeKind.Study,
                Escaping = false,
                ExtractBioferrite = true,
                CanBeCaptured = true,
                NeedsTend = false,
                Bleeding = false,
                HarvesterAttached = false,
                BioferritePerDay = 1.5,
                Escape = new EntityEscapeState { BaseMtbDays = 60, MovingFactor = 1, DaysInCaptivity = 2, ColdFactor = 1, CombatPower = 100, CanEscape = true },
            },
            Study = new StudyState { CurrentlyStudiable = true, Yield = new StudyYieldState { Knowledge = 2, Category = null, FrequencyTicks = 120000 } },
            HiddenFromPlayer = false,
            PsychicRitualInvoker = false,
            MeleeOnly = true,
            Creepjoiner = new CreepJoinerState { DownsideTriggered = false, JoinedTick = 120000, CombatPower = 55 },
        };

        var json = OrdersJson.Serialize(anomaly);

        Assert.Equal(
            """{"entity":true,"mutant":false,"minContainmentStrength":40,"held":{"held":true,"mode":"ENTITY_CONTAINMENT_MODE_KIND_STUDY","escaping":false,"extractBioferrite":true,"canBeCaptured":true,"needsTend":false,"bleeding":false,"harvesterAttached":false,"bioferritePerDay":1.5,"escape":{"baseMtbDays":60,"movingFactor":1,"daysInCaptivity":2,"coldFactor":1,"combatPower":100,"canEscape":true}},"study":{"currentlyStudiable":true,"yield":{"knowledge":2,"category":null,"frequencyTicks":120000}},"hiddenFromPlayer":false,"psychicRitualInvoker":false,"meleeOnly":true,"creepjoiner":{"downsideTriggered":false,"joinedTick":120000,"combatPower":55}}""",
            json);
        Assert.Equal(anomaly, OrdersJson.Deserialize<PawnAnomaly>(json));

        // The containment join retains the native study state, including a category the game says is absent.
        var row = PawnFixtures.Row("entity") with
        {
            Anomaly = new PawnAnomalyState.Active(anomaly with { Held = anomaly.Held! with { Held = false } }),
        };
        var forecast = RimGovernor.Brain.Bridge.ContainmentReads.ContainmentForecastOf([row], [], new());
        var entity = Assert.Single(forecast.Entities);
        Assert.Same(row, entity.Observed);
        Assert.Same(anomaly.Study!.Yield, entity.Study);
        Assert.Null(entity.Study!.Category);
    }

    [Fact]
    public void BuildingBlockJsonRoundTripIsExact()
    {
        var building = new AnomalyBuilding
        {
            Holder = new EntityHolderState
            {
                ContainmentStrength = 55.5,
                Available = false,
                HeldPawn = new RimGovernor.Brain.GameFacts.Common.Ref { Id = "Thing_Entity2" },
                Doors = [new AnomalyDoor { Cell = new Cell(3, 4), HoldOpen = false, ContainmentBreached = false, BlockedOpen = true }],
            },
        };

        var json = OrdersJson.Serialize(building);

        Assert.Equal(
            """{"holder":{"containmentStrength":55.5,"available":false,"heldPawn":{"id":"Thing_Entity2"},"doors":[{"cell":{"x":3,"z":4},"holdOpen":false,"containmentBreached":false,"blockedOpen":true}]}}""",
            json);
        Assert.Equal(building, OrdersJson.Deserialize<AnomalyBuilding>(json));
    }
}
