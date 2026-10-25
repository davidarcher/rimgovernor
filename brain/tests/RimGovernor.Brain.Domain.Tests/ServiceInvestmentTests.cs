using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

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
        Assert.True(proposal.AvoidedExposure > 0);
        Assert.NotEmpty(proposal.Tasks);
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
        { AvailableInput = late ? wall.AvailableInput : 0, Finish = late ? new ConstructionFinish.Finishes(2000d, null) : wall.Finish }] } } } };
        facts = facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1000)?.Priced() };
        var selection = ForecastThermalPortfolioTests.Select(facts);
        Assert.Empty(selection.Selected.CompletedConstructionSites);
        Assert.Empty(selection.ServiceInvestments);
    }

    [Fact]
    public void UnrelatedAdoptedSiteHasNoValueAndNoOriginRequirement()
    {
        var facts = ForecastCoupledTests.ConstructionReview();
        var selection = ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new(100), 1000d / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []), facts.CoupledAlternatives!);
        Assert.Contains(facts.CoupledAlternatives!.Alternatives, a => a.ExclusiveOutcome == "construction/frame0");
        Assert.Empty(selection.Selected.CompletedConstructionSites);
    }

    [Theory]
    [InlineData("started", false, true, true)]
    [InlineData("completed", false, true, false)]
    [InlineData("completed", true, true, true)]
    [InlineData("unowned", false, true, true)]
    // A frame's ended Supply is no boundary: the finish, or the carry's retry, goes on in the same binding (#3537).
    [InlineData("completed", false, false, true)]
    [InlineData("failed", false, false, true)]
    public void ConstructionRecomparesAtSafeObservedBoundaryWithoutClaimingSatisfaction(string state, bool truncated, bool finished, bool needed)
    {
        var facts = ForecastThermalPortfolioTests.Review(-10);
        var selection = ForecastThermalPortfolioTests.Select(facts);
        var batch = ForecastCoupledSelection.StartCoupledActivity(selection, facts.CoupledAlternatives!, 1, "coupled-test", 100)!;
        Assert.All(batch.Orders.Where(o => o.Task.Length != 0), o => Assert.Equal(PawnActivityKind.Supply, batch.Tasks.Single(t => t.Id == o.Task).Step.Kind));
        if (finished)
        {
            batch = batch with { Orders = [.. batch.Orders.Select(o => o.Task.Length == 0 ? o
                : o with { Task = batch.Tasks.First(t => t.Pawn == o.Pawn && t.Step.Kind != PawnActivityKind.Supply).Id })] };
        }
        var read = ConstructionFixtures.NoTasks() with { Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Tick = 110, Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 } }, Truncated = truncated,
            Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
                new(o.Assignment, o.Method, o.Revision, 0, state, "", 110)))) };
        Assert.Equal(needed ? ActivityNeed.Owed : ActivityNeed.Ended, ForecastServiceInvestments.ReviewServiceActivityNeed(batch, ActivityNeed.Owed, read));
        Assert.Contains(batch.Tasks, t => t.SiteDefinition.Length != 0 || t.Step.Job == "BuildRoof");
        Assert.Empty(read.SatisfiedTasks);
    }
    [Fact]
    public void FoodProposalUsesSameBeneficiariesAndRetiresWhenTheirDemandIsMet()
    {
        var facts = ForecastCoupledSelectionTests.Review();
        ForecastCoupledSelection Select(ReviewFacts f) => ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new(100),
            300d / GameTime.TicksPerDay, f,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(f) }, []), f.CoupledAlternatives!);
        var proposal = Assert.Single(Select(facts).ServiceInvestments);
        Assert.Equal(FoodRules.FoodSupplyNeed.FilingKey, proposal.Target.Owner);
        Assert.Equal("pawn", Assert.Single(proposal.Beneficiaries));
        Assert.True(proposal.AvoidedExposure > 0);
        Assert.Empty(Select(ForecastCoupledSelectionTests.Review(level: 1, fall: 0)).ServiceInvestments);
    }

    [Fact]
    public void DisabledShelterControlCannotGenerateRoofOrFinishWork()
    {
        var facts = ForecastThermalPortfolioTests.Review(-10) with
        { AvailableNeeds = [AdminFamily.WorkAssignments, FoodRules.FoodSupplyNeed] };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1000)!;
        Assert.DoesNotContain(generated.Alternatives, c => c.ExclusiveOutcome.StartsWith("shelter/", StringComparison.Ordinal)
            || c.ExclusiveOutcome.StartsWith("construction/", StringComparison.Ordinal));
    }

    [Fact]
    public void AdoptedSitesDoNotReserveStockButAdmittedSupplyClaimsAreExactAndIdempotent()
    {
        var facts = ForecastCoupledTests.ConstructionReview();
        var evidence = facts.WorkView!.Evidence;
        Assert.Equal(100, facts.WorkView.Materials.AssessedBalances()[new("Rice")].ExactCount());
        Assert.Equal(100, facts.CurrentConstruction!.Deficit[new("Rice")]); // Demand diagnostic remains intact.
        var task = new SelectedActivityTask("supply", "pawn", [],
            new(PawnActivityKind.Supply, "stack", "", "", 100, 500) { Container = "frame0" }, new(1, 1), 100)
            { Resource = "Rice", SiteDefinition = "TestWall", Source = new(1, 1),
                Inputs = [new("Rice", "stack", 100, new(1, 1), "observed") { Observed = true }] };
        var batch = new PawnActivities(1, [new("pawn", "assignment", "coupled-method", 0, 1, false, [task.Step]) { Task = task.Id }]) { Tasks = [task] };
        var action = DomainAction.NewPawnActivitiesAction(new("supply-action"), batch);
        var row = new WorkAction(new("plan"), action, new() { Action = action.Id, Stage = Stage.Pending });
        var view = WorkRules.WorkResourcesOf(evidence, new ProductionBatchCensus.Read([]), [row, row]);
        Assert.Equal(0, view.Materials.AssessedBalances()[new("Rice")].ExactCount());
        Assert.Equal(100, view.Claims.Materials.SelectMany(c => c.MaterialClaims).Sum(c => c.ExactCount()));
        var partial = batch with { Orders = [batch.Orders[0] with { Steps = [task.Step with { Count = 40 }] }] };
        var partialAction = DomainAction.NewPawnActivitiesAction(new("partial"), partial);
        var completedTrip = evidence with { WorkResources = [
            new("pawn", "assignment", 1, "completed", "", "", 0) { LocalHour = 12 }] };
        var partialView = WorkRules.WorkResourcesOf(completedTrip, new ProductionBatchCensus.Read([]),
            [new(new("plan"), partialAction, new() { Action = partialAction.Id, Stage = Stage.Pending })]);
        Assert.Equal(60, partialView.Claims.Materials.SelectMany(c => c.MaterialClaims).Sum(c => c.ExactCount()));
        var displaced = completedTrip with { WorkResources = [
            new("pawn", "player", 2, "started", "", "", 0) { LocalHour = 12 }] };
        var displacedView = WorkRules.WorkResourcesOf(displaced, new ProductionBatchCensus.Read([]),
            [new(new("plan"), partialAction, new() { Action = partialAction.Id, Stage = Stage.Completed, Attempt = new(1), Tick = new(99) })]);
        Assert.Empty(displacedView.Claims.Materials);
        Assert.True(WorkRules.WorkResourcesFit(facts.WorkView, new(new("new-plan"), new(1), [action])));
        var scarce = facts with { Census = facts.Census with { Resources = [new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = "Rice", Units = 99 }] }};
        var poor = WorkRules.WorkResourcesOf(ProductionRules.WorkAdmissionEvidenceOf(scarce, new ProductionBatchCensus.Read([])), new ProductionBatchCensus.Read([]), []);
        Assert.False(WorkRules.WorkResourcesFit(poor, new(new("new-plan"), new(1), [action])));
    }

    [Fact]
    public void UnsupportedEnergyDoesNotBuyCompletionAndAnAlternateFinishedRoomRemovesInvestment()
    {
        var facts = ForecastThermalPortfolioTests.Review(-10);
        var read = facts.ForecastObserved.Coupled!;
        var noPower = read.Thermal! with { Sources = EquatableArray.From(read.Thermal!.Sources.Select(s => s with { Supply = new HeatSupply.Unconnected() })) };
        var unavailable = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = read with { Thermal = noPower } } };
        unavailable = unavailable with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(unavailable, 1000)?.Priced() };
        Assert.Empty(ForecastThermalPortfolioTests.Select(unavailable).Selected.CompletedConstructionSites);
        var solved = read.Thermal with { Cells = EquatableArray.From(read.Thermal.Cells.Select(c => c with
        { Temperature = 20d, Roof = "thin", Solid = c.Cell != new Cell(1, 1) && c.Cell != new Cell(3, 1), Site = "", FutureSolid = false })) };
        var alternate = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = read with { Thermal = solved } } };
        alternate = alternate with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(alternate, 1000)?.Priced() };
        var choice = ForecastThermalPortfolioTests.Select(alternate);
        Assert.Empty(choice.Selected.CompletedConstructionSites);
        Assert.Empty(choice.ServiceInvestments);
    }
}
