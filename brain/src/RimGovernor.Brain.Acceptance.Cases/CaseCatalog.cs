namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The registration entry point of the acceptance cases. The
/// <c>acceptance</c> command calls <see cref="Register"/> once at startup; a case area adds one call here
/// (<c>BuildCases.Register(registry)</c>) and registers its <see cref="TypedCase"/> values through the public
/// <see cref="CaseRegistry"/> API only, never the harness internals.
/// </summary>
public static class CaseCatalog
{
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(PawnActivityCase.Case());
        registry.Register(OrderedWorkLifecycleCase.Case());
        registry.Register(OrderedWorkCancellationCase.Case());
        registry.Register(OrderedWorkCancellationCase.Case(replaced: true));
        registry.Register(PlannedRoomPredictionCase.Case());
        registry.Register(PlannedSiteDeconstructCase.Case());
        registry.Register(ControlDeconstruct.Case());
        registry.Register(ControlDeconstruct.Case(designated: true));
        registry.Register(ControlDeconstructRoofHolder.Case());
        registry.Register(PlannedSiteDeconstructCase.Case(roofHolder: true));
        registry.Register(ControlUninstall.Case());
        registry.Register(ControlCut.Case());
        registry.Register(ControlCut.Case(removed: true));
        registry.Register(ControlRemoveFloor.Case());
        registry.Register(PlannedSiteCutCase.Case());
        registry.Register(ControlMine.Case());
        registry.Register(ControlHaul.Case());
        registry.Register(RemovalDesignationRowsCase.Case());
        registry.Register(ConstructionRouteLegsCase.Case());
        registry.Register(PrisonProjectedRoutesCase.Case());
        registry.Register(ProductionCoupledNeeds.Case());
        registry.Register(ForecastResidualCase.Case());
        registry.Register(NeedsTargetCapacity.Case());
        registry.Register(NeedsSharedStack.Case());
        registry.Register(ProductionCoupledThermal.Case());
        registry.Register(ProductionCookingDevelopment.Productive());
        registry.Register(ProductionCookingDevelopment.Lifecycle());
        registry.Register(ProductionCookingDevelopment.Reassignment());
        registry.Register(ProductionMaintenancePractice.Case(false));
        registry.Register(ProductionMaintenancePractice.Case(true));
        registry.Register(ProductionDevilstrandStarving.Case());
        registry.Register(ProductionDevilstrandInvestment.Case());
        registry.Register(ProductionResearchBlock.Case());
        registry.Register(ProductionSowBlock.Case());
        registry.Register(ProductionSkillMarginal.Case());
        registry.Register(ShelterServiceInvestment.Case());
        foreach (var c in ControlActivities.Cases())
        {
            registry.Register(c);
        }

        registry.Register(ControlUnassignedVanilla.Case());
        registry.Register(ControlHomeFire.Case());
        registry.Register(ControlPreemptHolder.Case());

        registry.Register(ControlDelivery.Case());
        registry.Register(ProductionClothingActivity.Case());
        registry.Register(ProductionClothingActivity.Case(interrupted: true));
        registry.Register(CoordinatedWork.Food());
        registry.Register(CoordinatedWork.Food(interrupted: true));
        registry.Register(CoordinatedWork.Construction());

        // The case areas land with #3090 and #3093; each area adds one line below (sorted by area).
        ApparelCases.Register(registry);
        ApplyCases.Register(registry);
        AnimalsCases.Register(registry);
        AnomalyCases.Register(registry);
        AuthorityCases.Register(registry);
        BedCases.Register(registry);
        BillsCases.Register(registry);
        BurialCases.Register(registry);
        CaravanCases.Register(registry);
        ChildCases.Register(registry);
        ClearanceCases.Register(registry);
        CombatCases.Register(registry);
        CombatLabCases.Register(registry);
        CustodyCases.Register(registry);
        DefenseCases.Register(registry);
        DialogCases.Register(registry);
        DraftCases.Register(registry);
        EnvironmentCases.Register(registry);
        ExplainCases.Register(registry);
        FarmCases.Register(registry);
        FloorCases.Register(registry);
        FoodCases.Register(registry);
        GoalsCases.Register(registry);
        HazardCases.Register(registry);
        HusbandryCases.Register(registry);
        IdeologyCases.Register(registry);
        LabCases.Register(registry);
        LayoutCases.Register(registry);
        LetterCases.Register(registry);
        LifecycleCases.Register(registry);
        LightCases.Register(registry);
        MapscopeCases.Register(registry);
        MedicalCases.Register(registry);
        MoodCases.Register(registry);
        MovementCases.Register(registry);
        NeedsCases.Register(registry);
        OdysseyCases.Register(registry);
        PawnCases.Register(registry);
        PowerCases.Register(registry);
        ProductionCases.Register(registry);
        QuestCases.Register(registry);
        RecoveryCases.Register(registry);
        RefrigerationCases.Register(registry);
        ResearchCases.Register(registry);
        RoomsCases.Register(registry);
        ShelterCases.Register(registry);
        SleepingCases.Register(registry);
        RouteCases.Register(registry);
        ServiceCases.Register(registry);
        SmokeCases.Register(registry);
        StorageCases.Register(registry);
        SuppliesCases.Register(registry);
        TemperatureCases.Register(registry);
        ToolsCases.Register(registry);
        TradeCases.Register(registry);
        UpkeepCases.Register(registry);
        WallCases.Register(registry);
        WasteCases.Register(registry);
        ZoneCases.Register(registry);
    }
}
