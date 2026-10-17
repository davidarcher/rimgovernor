namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The registration entry point of the acceptance cases (Go: each area package's <c>init()</c>). The
/// <c>acceptance</c> command calls <see cref="Register"/> once at startup; a case area adds one call here
/// (<c>BuildCases.Register(registry)</c>) and registers its <see cref="AcceptanceCase"/> values through the public
/// <see cref="CaseRegistry"/> API only, never the harness internals.
/// </summary>
public static class CaseCatalog
{
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        HostSeams.Install();
        registry.Register(PawnActivityCase.Case());
        registry.Register(PlannedRoomPredictionCase.Case());
        registry.Register(ProductionCoupledNeeds.Case());
        registry.Register(NeedsTargetCapacity.Case());
        registry.Register(ProductionCoupledThermal.Case());
        registry.Register(ProductionCookingDevelopment.Productive());
        registry.Register(ProductionCookingDevelopment.Lifecycle());
        registry.Register(ProductionCookingDevelopment.Reassignment());
        registry.Register(ProductionMaintenancePractice.Case(false));
        registry.Register(ProductionMaintenancePractice.Case(true));
        registry.Register(ShelterServiceInvestment.Case());
        foreach (var c in ControlActivities.Cases())
        {
            registry.Register(c);
        }

        registry.Register(ControlDelivery.Case());
        registry.Register(ControlCooking.Case());
        registry.Register(ProductionClothingActivity.Case());
        registry.Register(ProductionClothingActivity.Case(interrupted: true));
        registry.Register(CoordinatedWork.Case("food"));
        registry.Register(CoordinatedWork.Case("food", interrupted: true));
        registry.Register(CoordinatedWork.Case("construction"));

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
        CleanCases.Register(registry);
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
        RouteCases.Register(registry);
        ServiceCases.Register(registry);
        SmokeCases.Register(registry);
        SpeedmatrixCases.Register(registry);
        StatsCases.Register(registry);
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
