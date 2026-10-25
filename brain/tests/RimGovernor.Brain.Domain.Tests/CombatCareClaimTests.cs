using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CombatCareClaimTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialRescueLeavesAvailableDefendersAbleToAdmitTheFight(bool nextStep)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-combat-care-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new() { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(),
            Facts = WorkResourceTestFacts.Create("rescuer", "fighter-a", "fighter-b", "civilian", "patient") with { Hostiles = HostileCensus.Of(1) } }, ct);
        PawnId patient = new("patient");
        var rescue = new PlanSpec(new("rescue"), new(1), [DomainAction.NewRescueAction(new("rescue-0"), new(new("rescuer"), patient))]);
        var medical = new FamilyRunner<MedicalFacts>(new MedicalFamily(true, false, false, false),
            (_, _) => Task.FromResult(new MedicalFacts { Critical = [patient] }), MedicalReview.Schedule,
            (work, _, call) => Task.FromResult(BuildingPlanner.OwnerCommitVerdict(journal.CommitOwnerWork(work.Owner, new("rescue-patient"), "", rescue, call))));
        Assert.Equal(ClaimOutcome.Admitted, Assert.Single((await medical.RunAsync(new FamilyStep { Journal = journal, World = World, Immediate = true }, ct)).Methods).Outcome);

        var arbiter = new StepArbiter();
        if (!nextStep) Assert.True(arbiter.TryClaim([new("rescuer"), patient]));
        var rows = new[]
        {
            Fighter("rescuer", 10), Fighter("fighter-a", 11), Fighter("fighter-b", 12),
            EmergencyFixtures.Placed("civilian", 13, 10) with { Biography = PawnFixtures.Row("civilian").Biography with { DisabledWorkTags = ["Violent"] } },
            EmergencyFixtures.Placed("patient", 10, 11) with { Downed = true },
        };
        var hostile = EmergencyFixtures.Placed("hostile", 20, 10) with { Colonist = false, FreeColonist = false };
        var context = EmergencyFixtures.Status().Context!;
        var emergency = new EmergencyFacts { Colonists = [.. rows.Select(row => new EmergencyPawn { Observed = row })],
            Threats = [new EmergencyThreat.PawnThreat(hostile, EmergencyFixtures.ThreatPawn("hostile"), ThreatKind.Hostile)] };
        var catalog = new DefinitionCatalog([new Verse.ThingDef { defName = "Silver" }, new Verse.TerrainDef { defName = "Soil" },
            new Verse.ThingDef { defName = "Human", race = new Verse.RaceProperties { baseBodySize = 1 } },
            new Verse.ThingDef { defName = "MeleeWeapon_Knife" },
            new Verse.HediffDef { defName = nameof(RimWorld.HediffDefOf.BloodLoss), stages = [new Verse.HediffStage { label = "moderate", minSeverity = .5f }] }],
            DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));
        var weapons = rows.Where(row => row.Equipment.Armed).Select(row => new RimGovernor.Brain.GameFacts.Observations.Thing
        {
            Entity = new() { Id = row.Equipment.PrimaryId!, DefName = "MeleeWeapon_Knife", Label = "knife", Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.Unplaced() },
            Forbidden = false, Corpse = false,
        });
        var combat = new RimGovernor.Brain.Bridge.Combat { Context = context, Emergency = new(context, emergency), Detail = Pawns.Of([.. rows, hostile]), Catalog = catalog, Things = Things.Of(weapons) };
        var (inputs, reason) = DefensePlanner.CombatFrameInputs(combat, [], []);
        Assert.True(reason.IsZero);
        Assert.NotNull(inputs);
        inputs.Unavailable = DefensePlanner.CombatUnavailable(journal, World, emergency, arbiter, ct);
        Assert.Contains(patient, inputs.Unavailable);
        Assert.Contains(new PawnId("rescuer"), inputs.Unavailable);
        var view = DefensePlanner.CombatView(combat, inputs, [], null);
        Assert.Equal(6, view.Pawns.Count);
        Assert.True(Assert.Single(view.Pawns, pawn => pawn.Id == patient).Downed);
        Assert.Single(view.Threats);
        Assert.DoesNotContain(view.Defenders, pawn => pawn.Id == new PawnId("rescuer"));
        Assert.Contains(view.Defenders, pawn => pawn.Id == new PawnId("civilian"));

        var geometry = new GeometryReply { Answered = true, Standable = [.. CombatShelterRules.ShelterChecks(view)] };
        var shelter = CombatShelterRules.ShelterRoles(view, geometry, []);
        Assert.Contains(shelter, role => role.Pawn == new PawnId("civilian"));
        Assert.DoesNotContain(shelter, role => role.Pawn == new PawnId("rescuer") || role.Pawn == patient);
        var (_, _, memory) = CombatDecideRules.DecideCombat(view, geometry, new StopEvent(), new CombatMemory());
        Assert.Contains(memory.Roles, role => role.Pawn == new PawnId("fighter-a") && role.Target == new PawnId("hostile"));
        Assert.Contains(memory.Roles, role => role.Pawn == new PawnId("fighter-b") && role.Target == new PawnId("hostile"));
        Assert.DoesNotContain(memory.Roles, role => role.Pawn == new PawnId("rescuer") || role.Pawn == patient);
        var drafted = memory.Roles.Select(role => role.Pawn).ToList();
        Assert.True(arbiter.TryClaim(drafted));
        var ready = DefensePlanner.CombatView(combat, inputs, drafted, null);
        var (orders, _, _) = CombatDecideRules.DecideCombat(ready, geometry, new StopEvent(), memory);
        Assert.Contains(orders, order => order.Target == new PawnId("hostile"));
        Assert.DoesNotContain(orders, order => order.Pawn == new PawnId("rescuer") || order.Pawn == patient);
        var fightPlan = new PlanSpec(new("fight"), new(1), []);
        journal.CommitCombatFight(World, DefensePlanner.EngageMethod(World), fightPlan, memory, [.. drafted.Select(pawn => (RimGovernor.Brain.Domain.PawnId)pawn)], ct);
        Assert.True(journal.LoadCombatFight(fightPlan.Id, ct)!.Open);
        Assert.Single(journal.LoadNeed(MedicalRules.CriticalNeed(patient), World, ct).Methods);
        Assert.True(WorkProgress.Open(journal.LoadPlan(rescue.Id, ct).Progress));
    }

    private static RimGovernor.Brain.GameFacts.Observations.PawnState Fighter(string id, int x) =>
        EmergencyFixtures.Placed(id, x, 10) with { Equipment = GearFixtures.NoEquipment with { Armed = true, MeleeDps = 10, PrimaryId = "knife-" + id,
            Equipped = [GearFixtures.Item("knife-" + id, new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Normal))] } };
}
