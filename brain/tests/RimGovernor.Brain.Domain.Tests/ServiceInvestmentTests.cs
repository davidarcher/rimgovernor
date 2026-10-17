using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ServiceInvestmentTests
{
    [Fact]
    public void UsefulShelterExplainsSharedOwnerPhysicalWorkAndBoundedRelief()
    {
        var facts = ForecastThermalPortfolioTests.Review(-10);
        var selection = ForecastThermalPortfolioTests.Select(facts);
        var proposal = Assert.Single(selection.ServiceInvestments);
        Assert.Equal(ShelterFamily.Temperature.FilingKey, proposal.Target.Owner);
        Assert.Equal("owner_observed_recovery", proposal.Target.Predicate);
        Assert.Equal(1000, proposal.HorizonTicks);
        Assert.Contains("pawn", proposal.Beneficiaries);
        Assert.True(proposal.AvoidedExposure.Value > 0);
        Assert.NotEmpty(proposal.Tasks);
        Assert.Equal(0, selection.SelectedVector.Development);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScarceOrTooLateShelterCannotEarnCompletionCredit(bool late)
    {
        var facts = ForecastThermalPortfolioTests.Review(-10);
        var read = facts.ForecastObserved.Coupled!;
        var wall = read.Construction.Options.Single();
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = read with
        { Construction = read.Construction with { Options = [wall with
        { AvailableInput = late ? wall.AvailableInput : Known(0d), FinishTicks = late ? Known(2000d) : wall.FinishTicks }] } } } };
        facts = facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1000) };
        var selection = ForecastThermalPortfolioTests.Select(facts);
        Assert.Empty(selection.Selected.CompletedDevelopmentSites);
        Assert.Empty(selection.ServiceInvestments);
        Assert.Equal(0, selection.SelectedVector.Development);
    }

    [Fact]
    public void UnrelatedAdoptedSiteHasNoValueAndNoOriginRequirement()
    {
        var facts = ForecastCoupledTests.ConstructionReview();
        var selection = ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new("world", 100), 1000d / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new()), facts.CoupledAlternatives!);
        Assert.Contains(facts.CoupledAlternatives!.Alternatives, a => a.ExclusiveOutcome == "construction/frame0");
        Assert.Empty(selection.Selected.CompletedDevelopmentSites);
    }

    [Theory]
    [InlineData("started", false, true)]
    [InlineData("completed", false, false)]
    [InlineData("completed", true, true)]
    [InlineData("unowned", false, true)]
    public void ConstructionRecomparesAtSafeObservedBoundaryWithoutClaimingSatisfaction(string state, bool truncated, bool needed)
    {
        var facts = ForecastThermalPortfolioTests.Review(-10);
        var selection = ForecastThermalPortfolioTests.Select(facts);
        var batch = ForecastCoupledSelection.StartCoupledActivity(selection, facts.CoupledAlternatives!, 1, "coupled-test", 100)!;
        var read = new ActivityTaskRead { Known = true, Context = new() { Tick = 110, Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 } }, Truncated = truncated,
            Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
                new(o.Assignment, o.Method, o.Revision, 0, state, "", null, 110)))) };
        Assert.Equal(Known(needed), ForecastServiceInvestments.ReviewServiceActivityNeed(batch, Known(true), read));
        Assert.Contains(batch.Tasks, t => t.SiteDefinition.Length != 0 || t.Step.Job == "BuildRoof");
        Assert.Empty(read.SatisfiedTasks);
    }
    [Fact]
    public void FoodProposalUsesSameBeneficiariesAndRetiresWhenTheirDemandIsMet()
    {
        var facts = ForecastCoupledSelectionTests.Review();
        ForecastCoupledSelection Select(RoundsFacts f) => ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new("world", 100),
            300d / GameTime.TicksPerDay, f,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(f) }, [], new()), f.CoupledAlternatives!);
        var proposal = Assert.Single(Select(facts).ServiceInvestments);
        Assert.Equal(FoodSupplyNeed.FilingKey, proposal.Target.Owner);
        Assert.Equal("pawn", Assert.Single(proposal.Beneficiaries));
        Assert.True(proposal.AvoidedExposure.Value > 0);
        Assert.Empty(Select(ForecastCoupledSelectionTests.Review(level: 1, fall: 0)).ServiceInvestments);
    }

    [Fact]
    public void DisabledShelterControlCannotGenerateRoofOrFinishWork()
    {
        var facts = ForecastThermalPortfolioTests.Review(-10) with
        { AvailableNeeds = Known<EquatableArray<NeedId>>([AdminFamily.WorkAssignments, FoodSupplyNeed]) };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1000)!;
        Assert.DoesNotContain(generated.Alternatives, c => c.ExclusiveOutcome.StartsWith("shelter/", StringComparison.Ordinal)
            || c.ExclusiveOutcome.StartsWith("construction/", StringComparison.Ordinal));
    }

    [Fact]
    public void AdoptedSitesDoNotReserveStockButAdmittedSupplyClaimsAreExactAndIdempotent()
    {
        var facts = ForecastCoupledTests.ConstructionReview();
        var evidence = facts.WorkView!.Evidence;
        Assert.Equal(100, facts.WorkView.Materials.Value[new("Rice")].Value);
        Assert.Equal(100, facts.ConstructionDeficit.Value[new("Rice")]); // Demand diagnostic remains intact.
        var task = new SelectedActivityTask("supply", "pawn", [],
            new(PawnActivityKind.Supply, "stack", "", "", 100, 500) { Container = "frame0" }, new(1, 1), 100)
            { Resource = "Rice", SiteDefinition = "TestWall", Source = new(1, 1),
                Inputs = [new("Rice", "stack", 100, new(1, 1), "observed") { Observed = true }] };
        var batch = new PawnActivities(1, [new("pawn", "assignment", "coupled-method", 0, 1, false, [task.Step]) { Task = task.Id }]) { Tasks = [task] };
        var action = DomainAction.NewPawnActivitiesAction(new("supply-action"), batch);
        var row = new WorkAction(new("plan"), action, new() { Action = action.Id, Stage = Stage.Pending });
        var view = WorkResourcesOf(evidence, Known<EquatableArray<ProductionBatch>>([]), [row, row]);
        Assert.Equal(0, view.Materials.Value[new("Rice")].Value);
        Assert.Equal(100, view.Claims.Materials.SelectMany(c => c.MaterialClaims).Sum(c => c.Units.Value));
        var partial = batch with { Orders = [batch.Orders[0] with { Steps = [task.Step with { Count = 40 }] }] };
        var partialAction = DomainAction.NewPawnActivitiesAction(new("partial"), partial);
        var completedTrip = evidence with { WorkResources = Known<EquatableArray<WorkResourcePawn>>([
            new("pawn", "assignment", 1, "completed", "", "", 0)]) };
        var partialView = WorkResourcesOf(completedTrip, Known<EquatableArray<ProductionBatch>>([]),
            [new(new("plan"), partialAction, new() { Action = partialAction.Id, Stage = Stage.Pending })]);
        Assert.Equal(60, partialView.Claims.Materials.SelectMany(c => c.MaterialClaims).Sum(c => c.Units.Value));
        var displaced = completedTrip with { WorkResources = Known<EquatableArray<WorkResourcePawn>>([
            new("pawn", "player", 2, "started", "", "", 0)]) };
        var displacedView = WorkResourcesOf(displaced, Known<EquatableArray<ProductionBatch>>([]),
            [new(new("plan"), partialAction, new() { Action = partialAction.Id, Stage = Stage.Completed, Attempt = new(1), Tick = new(99) })]);
        Assert.Empty(displacedView.Claims.Materials);
        Assert.True(WorkResourcesFit(facts.WorkView, new(new("new-plan"), new(1), [action])));
        var scarce = facts with { Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), 99)]) };
        var poor = WorkResourcesOf(WorkAdmissionEvidenceOf(scarce, Known<EquatableArray<ProductionBatch>>([])), Known<EquatableArray<ProductionBatch>>([]), []);
        Assert.False(WorkResourcesFit(poor, new(new("new-plan"), new(1), [action])));
    }

    [Fact]
    public void UnsupportedEnergyDoesNotBuyCompletionAndAnAlternateFinishedRoomRemovesInvestment()
    {
        var facts = ForecastThermalPortfolioTests.Review(-10);
        var read = facts.ForecastObserved.Coupled!;
        var noPower = read.Thermal! with { Sources = EquatableArray.From(read.Thermal!.Sources.Select(s => s with { SupplyTicks = default })) };
        var unavailable = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = read with { Thermal = noPower } } };
        unavailable = unavailable with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(unavailable, 1000) };
        Assert.Empty(ForecastThermalPortfolioTests.Select(unavailable).Selected.CompletedDevelopmentSites);
        var solved = read.Thermal with { Cells = EquatableArray.From(read.Thermal.Cells.Select(c => c with
        { Temperature = Known(20d), Roof = "thin", Solid = c.Cell != new Cell(1, 1) && c.Cell != new Cell(3, 1), Site = "", FutureSolid = false })) };
        var alternate = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = read with { Thermal = solved } } };
        alternate = alternate with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(alternate, 1000) };
        var choice = ForecastThermalPortfolioTests.Select(alternate);
        Assert.Empty(choice.Selected.CompletedDevelopmentSites);
        Assert.Empty(choice.ServiceInvestments);
    }
}
