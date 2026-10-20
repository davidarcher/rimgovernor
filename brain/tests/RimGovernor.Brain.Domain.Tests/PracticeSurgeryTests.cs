using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using SurgeryOperation = RimGovernor.Brain.Policy.SurgeryOperation;
using SurgeryKind = RimGovernor.Brain.Policy.SurgeryKind;
using static RimGovernor.Brain.Domain.Fact;
namespace RimGovernor.Brain.Domain.Tests;

// #3637: a practice surgery on a prisoner is a generalized practice step: candidates only for legal practice-cycle steps, one per
// doctor x prisoner x step x medicine kind, valued only by the Investment term. #3639: a prisoner without a slot can be given one.
public sealed class PracticeSurgeryTests
{
    private const string Herbal = "MedicineHerbal", Industrial = "MedicineIndustrial";
    private static readonly RecipeFacts Recipes = new()
    {
        MaterialInstalls = [new MaterialInstall { Recipe = "InstallPegLeg", Part = "PegLeg", Bodies = ["Leg"], Work = 150,
            Learning = new RecipeLearning("Medicine", Known(1.6), false), SpeedStat = "MedicalOperationSpeed", Medicine = 1 }],
    };

    private static SurgeryOperation Restore(string recipe = "InstallPegLeg") => new()
    {
        Kind = SurgeryKind.Restore, Recipe = Known(recipe), PartIndex = Known(5), PartDefName = Known("Leg"),
        IngredientsOnMap = Known(true), Violation = Known(false),
    };

    private static PrisonerFacts Prisoner(string pawn, int queued = 0, params SurgeryOperation[] operations) => new()
    {
        Pawn = new PawnId(pawn), Prisoner = Known(true), Dead = Known(false), QueuedSurgeries = Known(queued),
        Operations = Known(EquatableArray.From(operations.Length == 0 ? [Restore()] : operations)),
    };

    private static PracticeSurgeryWorker Worker(string medicine, double chance, int count = 1, string recipe = "InstallPegLeg") =>
        new("pawn", "P1", recipe, 5, medicine, count, Known(chance), "DoBillsMedicalHumanOperation", new(2, 1), Known(0d), Known(200d))
        { SkillWork = ProductiveLearningTests.Surgery() };

    private static RoundsFacts Facts(PracticeSurgeryWorker[] workers, params PrisonerFacts[] prisoners)
    {
        var facts = ForecastPortfolioChoiceTests.Observed([], []);
        return WorkRules.ReviewWorkResources(facts with
        {
            Hostiles = Known(0L), CriticalPatients = Known(0L), Recipes = Recipes,
            Prisoners = Known(EquatableArray.From(prisoners.Length == 0 ? [Prisoner("P1")] : prisoners)),
            Resources = Known(EquatableArray.From(new[] { new RimGovernor.Brain.Policy.Amount(new(Herbal), 5), new RimGovernor.Brain.Policy.Amount(new(Industrial), 5) })),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs! with { Pawns = EquatableArray.From(facts.ForecastObserved.Needs!.Pawns.Select(p => p with
                { Needs = EquatableArray.From(p.Needs.Select(n => n with { Level = Known(1d), FallPerTick = Known(0d) })) })) },
                Coupled = facts.ForecastObserved.Coupled! with { Practice = new() { Known = true, Surgeries = EquatableArray.From(workers) } },
            },
        }, Known<EquatableArray<ProductionBatch>>([]), new([]));
    }

    private static LegalPracticeSurgery[] Legal(RoundsFacts facts, bool admitted = true, ISet<string>? findings = null) =>
        PracticeSurgeryRules.LegalSurgeries(facts, facts.ForecastObserved.Coupled!.Practice, admitted, findings ?? new HashSet<string>());

    [Fact]
    public void OnlyLegalPracticeCycleStepsWithinTheFailureCapAreCandidates()
    {
        // The herbal chance fails 30% of the time, above the 20% cap; industrial fails 10%.
        Assert.Equal([Industrial], Legal(Facts([Worker(Herbal, .7), Worker(Industrial, .9)])).Select(w => w.Row.Medicine));
        // A bionic restore is not a practice-cycle step.
        Assert.Empty(Legal(Facts([Worker(Industrial, .9, recipe: "InstallBionicLeg")], Prisoner("P1", 0, Restore("InstallBionicLeg")))));
        Assert.Empty(Legal(Facts([Worker(Industrial, .9)], Prisoner("P1", 0, Restore() with { IngredientsOnMap = Known(false) }))));
        Assert.Empty(Legal(Facts([Worker(Industrial, .9)], Prisoner("P1", 0, Restore() with { Violation = Known(true) }))));
        Assert.Empty(Legal(Facts([Worker(Industrial, .9)]), admitted: false));
    }

    [Fact]
    public void AGatedPrisonerYieldsNone()
    {
        var findings = new HashSet<string>();
        // Any queued prisoner surgery holds every practice surgery (the in-flight rule RoundsSurgery's harvest and recovery use).
        Assert.Empty(Legal(Facts([Worker(Industrial, .9)], Prisoner("P1"), Prisoner("P2", queued: 1)), findings: findings));
        Assert.Contains("practice_surgery_held_surgery_in_flight", findings);
        var forbidden = Facts([Worker(Industrial, .9)]) with { Preferences = new() { Forbidden = [ForbiddenMethod.OrganHarvest] } };
        Assert.Empty(Legal(forbidden, findings: findings));
        Assert.Contains("practice_surgery_forbidden_by_preference", findings);
        Assert.Empty(Legal(Facts([Worker(Industrial, .9)]) with { Prisoners = default }, findings: findings));
    }

    [Fact]
    public void TheMedicineKindIsACandidateDimensionCarryingItsConsumption()
    {
        var facts = Facts([Worker(Herbal, .85, count: 2), Worker(Industrial, .95)]);
        var generated = PracticeMethodCandidates.Generate(facts, facts.ForecastObserved.Coupled!.Practice, 300, [], true, Legal(facts));

        Assert.Equal(2, generated.Surgeries.Count);
        var herbal = Assert.Single(generated.Tasks, t => t.Id.EndsWith("/" + Herbal, StringComparison.Ordinal));
        Assert.Equal((new Resource(Herbal), Known(2d)), (herbal.Inputs.Single().Resource, herbal.Inputs.Single().Units));
        var industrial = Assert.Single(generated.Tasks, t => t.Id.EndsWith("/" + Industrial, StringComparison.Ordinal));
        Assert.Equal((new Resource(Industrial), Known(1d)), (industrial.Inputs.Single().Resource, industrial.Inputs.Single().Units));
        var binding = generated.Bindings[(industrial.Id, "pawn")];
        Assert.Equal((PawnActivityKind.Work, "P1", "DoBill"), (binding.Kind, binding.Target, binding.Job));
        Assert.Equal(new PawnActivitySurgery("InstallPegLeg", 5, Industrial), binding.Practice!.Surgery);
        Assert.All(generated.Roots, r => Assert.Equal("practice/surgery", r.ExclusiveOutcome));
    }

    [Fact]
    public void APracticeSurgeryWinsOnlyOnAPositiveMarginalAndBindsTheSurgeonRestrictedStep()
    {
        var facts = Facts([Worker(Industrial, .95)]);
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300)!;
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 300d / GameTime.TicksPerDay,
            facts with { CoupledAlternatives = generated }, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        bool Practises(ForecastCoupledSelection comparison) => comparison.Selected.Plan.Allocations.Any(a => a.Activity.StartsWith("practice/surgery/", StringComparison.Ordinal));

        // Zero marginals: evaluation refuses the practice, so it never wins.
        Assert.False(Practises(ForecastCoupledSelection.CompareActivityOutcomes(request, generated)));

        var valued = generated with { Context = generated.Context with { Investment = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [ProjectGraph.SkillKey("pawn", "Medicine")] = 0.01, [ProjectGraph.MedicineKey(new Resource(Industrial))] = 0.1,
        } } };
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request with { Coupled = valued.Context }, valued);
        Assert.True(Practises(selected));
        var batch = Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selected, valued, 1, "practice-surgery-test", 100));
        batch.Validate();
        var task = Assert.Single(batch.Tasks);
        Assert.Equal(new PawnActivitySurgery("InstallPegLeg", 5, Industrial), task.Step.Practice!.Surgery);
        Assert.Equal(("pawn", "P1"), (task.Pawn, task.Step.Target));
    }

    // #3639: a prisoner with no practice slot can be given one by removing a natural leg, priced by the slot's tier-2 counterfactual.
    private static SurgeryOperation Removal() => new()
    {
        Kind = SurgeryKind.Harvest, Recipe = Known("RemoveBodyPart"), PartIndex = Known(5), PartDefName = Known("Leg"),
        IngredientsOnMap = Known(true), Violation = Known(true), Lethal = Known(false),
    };

    /// <summary>P1 held by a one-colonist colony; skilled, it is worth recruiting (a recruit target), else not.</summary>
    private static PrisonerFacts Captive(bool skilled, params SurgeryOperation[] operations) => Prisoner("P1", 0, operations.Length == 0 ? [Removal()] : operations) with
    {
        Recruitable = Known(true), CurrentInteraction = Known(PrisonerInteractionMode.Maintain), Resistance = Known(0d),
        Prospect = Known(new PrisonerProspect { Age = 30, Health = 1, Skills = skilled ? [new PrisonerSkill { Name = "Shooting", Level = 12, Passion = "Major" }] : [] }),
    };

    private static RoundsFacts Amputation(PrisonerFacts captive, params NeedMemory[] memories) =>
        Facts([Worker(Industrial, .95, recipe: "RemoveBodyPart") with { Memories = EquatableArray.From(memories.Select(m => new PracticeSurgeryMemory("pawn", m))) }], captive)
            with { PrisonerColony = Known(new PrisonerColony { Colonists = 1 }) };

    private static ForecastCoupledSelection SelectAmputation(RoundsFacts facts, double slot, bool mood = false)
    {
        // A memory moves mood over hours: the mood case looks a day ahead, as a review does.
        var horizon = mood ? GameTime.TicksPerDay : 300d;
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)!;
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), horizon / GameTime.TicksPerDay,
            facts with { CoupledAlternatives = generated }, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var context = generated.Context with { Investment = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [ProjectGraph.SlotKey("InstallPegLeg")] = slot, [ProjectGraph.MedicineKey(new Resource(Industrial))] = 0.1,
        } };
        if (mood)
            context = context with { MoodTrajectory = true, Needs = context.Needs with { Pawns = EquatableArray.From(context.Needs.Pawns.Select(p => p with
                { Mood = Known(.4), MoodTarget = Known(.4), MoodUnclampedTarget = Known(.4), BreakThreshold = Known(.35) })) } };
        var valued = generated with { Context = context };
        return ForecastCoupledSelection.CompareActivityOutcomes(request with { Coupled = valued.Context }, valued);
    }

    private static bool Amputates(ForecastCoupledSelection comparison) =>
        comparison.Selected.Plan.Allocations.Any(a => a.Activity.StartsWith("practice/surgery/pawn/P1/RemoveBodyPart/", StringComparison.Ordinal));

    [Fact]
    public void AnAmputationOpeningAValuableSlotIsSelectedAndAWeakOneIsNot()
    {
        var facts = Amputation(Captive(skilled: false));
        var legal = Assert.Single(Legal(facts));
        Assert.Equal("InstallPegLeg", legal.Slot!.Recipe);

        var generated = PracticeMethodCandidates.Generate(facts, facts.ForecastObserved.Coupled!.Practice, 300, [], true, [legal]);
        var task = Assert.Single(generated.Tasks);
        // Success-weighted: the slot opens only when the surgery succeeds.
        Assert.Equal((ProjectGraph.SlotKey("InstallPegLeg"), .95 / 200), (task.ProjectProgress!.Project, task.ProjectProgress.PointsPerTick));
        Assert.Superset(new SortedSet<string> { "recruit_option_value_unassessed", "prisoner_goodwill_unassessed", "prisoner_sale_value_unassessed" },
            new SortedSet<string>(generated.Findings));

        // A doctor the slot's practice would lift (a large counterfactual) takes the leg; a competent one (a small one) does not.
        Assert.True(Amputates(SelectAmputation(facts, 1)));
        Assert.False(Amputates(SelectAmputation(facts, .01)));
    }

    [Fact]
    public void ARecruitTargetIsNeverAmputated()
    {
        var findings = new HashSet<string>();
        Assert.Empty(Legal(Amputation(Captive(skilled: true)), findings: findings));
        Assert.False(Amputates(SelectAmputation(Amputation(Captive(skilled: true)), 1000)));
        // An unread prospect cannot rule recruiting out.
        Assert.Empty(Legal(Amputation(Captive(skilled: false) with { Prospect = default }), findings: findings));
        Assert.Contains("practice_slot_recruiting_unknown", findings);
    }

    [Fact]
    public void APrisonerWithASlotCyclesItAndALethalOrKeptPartIsNotTaken()
    {
        // One-limb cycling: a prisoner already missing a leg a peg leg restores offers only that restore.
        var cycling = Facts([Worker(Industrial, .95, recipe: "RemoveBodyPart"), Worker(Industrial, .95)], Captive(false, Removal(), Restore()))
            with { PrisonerColony = Known(new PrisonerColony { Colonists = 1 }) };
        Assert.Equal(["InstallPegLeg"], Legal(cycling).Select(l => l.Row.Recipe));
        Assert.Empty(Legal(Amputation(Captive(false, Removal() with { Lethal = Known(true) }))));
        Assert.Empty(Legal(Amputation(Captive(false, Removal() with { Lethal = default }))));
    }

    [Fact]
    public void TheColonistsMemoriesOfTheRemovalRaiseItsCost()
    {
        var memory = new NeedMemory("KnowGuestOrganHarvested", -20, Known(8d * GameTime.TicksPerDay), false);
        var plain = SelectAmputation(Amputation(Captive(skilled: false)), 1, mood: true);
        var mourned = SelectAmputation(Amputation(Captive(skilled: false), memory), 1, mood: true);

        Assert.True(Amputates(plain));
        Assert.False(Amputates(mourned));
        Assert.Equal([new ForecastMoodMemory("pawn", memory)], ForecastCoupledCandidates.GenerateCoupledActivities(Amputation(Captive(skilled: false), memory), 300)!
            .Alternatives.Single(c => c.Id.StartsWith("practice/surgery/", StringComparison.Ordinal)).MoodMemories);
    }

    [Fact]
    public void TheCodecRoundTripsTheSurgeryVariantAndValidationKeepsItsShape()
    {
        var step = new PawnActivityStep(PawnActivityKind.Work, "P1", "DoBillsMedicalHumanOperation", "DoBill", 0, 500)
        { Practice = new("Medicine", "", 0, "", "", 200) { Surgery = new("InstallPegLeg", 5, Industrial) } };
        var batch = new PawnActivities(1, [new("pawn", "practice/pawn", "practice", 0, 1, false, [step]) { Task = "practice/surgery/x" }])
        { Tasks = [new("practice/surgery/x", "pawn", [], step, new(2, 1), 100)] };
        batch.Validate();

        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            RimGovernor.Brain.Store.Codec.Codecs.Default.Write(writer, DomainAction.NewPawnActivitiesAction(new("practice-surgery"), batch));
        using var document = System.Text.Json.JsonDocument.Parse(stream.ToArray());
        var stored = RimGovernor.Brain.Store.Codec.Codecs.Default.Read<DomainAction>(document.RootElement).PawnActivities!;
        Assert.Equal(step, stored.Orders[0].Steps[0]);
        Assert.Equal(step, stored.Tasks[0].Step);
        var wire = RimGovernor.Contracts.Wire.WireJson.Deserialize<RimGovernor.Contracts.Wire.Operations.PawnActivitiesIntent>(
            RimGovernor.Contracts.Wire.WireJson.Serialize(new RimGovernor.Contracts.Wire.Operations.PawnActivitiesIntent { Batch = batch }));
        Assert.Equal(step, wire.Batch!.Tasks[0].Step);

        // A practice surgery is a Medicine DoBill with no exact recipe arguments, maintenance role or loose medicine stack.
        PawnActivities With(PawnActivityStep changed) => batch with { Orders = [batch.Orders[0] with { Steps = [changed] }], Tasks = [batch.Tasks[0] with { Step = changed }] };
        Assert.Throws<DomainException>(() => With(step with { Practice = step.Practice with { Skill = "Shooting" } }).Validate());
        Assert.Throws<DomainException>(() => With(step with { Practice = step.Practice with { Role = "role" } }).Validate());
        Assert.Throws<DomainException>(() => With(step with { Medicine = "medicine/1" }).Validate());
        Assert.Throws<DomainException>(() => With(step with { Practice = step.Practice with { Surgery = new("InstallPegLeg", -1, Industrial) } }).Validate());
    }
}
