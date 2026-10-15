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
        registry.Register(NeedsForecastCase.Case());
        registry.Register(CoupledForecastCase.Case());
        registry.Register(ConditionalMoodCase.Case());
        registry.Register(PortfolioNeedsCase.Case());
        registry.Register(ProductiveCookingCase.Case());
        registry.Register(MaintenancePracticeCase.Case(false));
        registry.Register(MaintenancePracticeCase.Case(true));
        registry.Register(PawnControlCase.Case());
        registry.Register(PawnControlCase.LifecycleCase());
        registry.Register(PawnDeliveryCase.Case());
        registry.Register(PawnCookingCase.Case());
        registry.Register(ClothingActivityCase.Case());
        registry.Register(ClothingActivityCase.Case(interrupted: true));
        registry.Register(CoordinatedWorkCase.Case("food"));
        registry.Register(CoordinatedWorkCase.Case("food", interrupted: true));
        registry.Register(CoordinatedWorkCase.Case("construction"));

        // The case areas land with #3090 and #3093; each area adds one line below (sorted by area).
        ApplyCases.Register(registry);
        AnimalsCases.Register(registry);
        AnomalyCases.Register(registry);
        AuthorityCases.Register(registry);
        BedCases.Register(registry);
        BillsCases.Register(registry);
        BurialCases.Register(registry);
        CampaignCases.Register(registry);
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
        PresentationCases.Register(registry);
        ProductionCases.Register(registry);
        QuestCases.Register(registry);
        RecoveryCases.Register(registry);
        RefrigerationCases.Register(registry);
        ResearchCases.Register(registry);
        ReviewCases.Register(registry);
        RoomsCases.Register(registry);
        ShelterCases.Register(registry);
        RouteCases.Register(registry);
        ScheduleCases.Register(registry);
        ServiceCases.Register(registry);
        SleepingCases.Register(registry);
        SmokeCases.Register(registry);
        SoloCases.Register(registry);
        SpeedmatrixCases.Register(registry);
        StartupCases.Register(registry);
        StatsCases.Register(registry);
        StorageCases.Register(registry);
        SuppliesCases.Register(registry);
        SustainedCases.Register(registry);
        TemperatureCases.Register(registry);
        TickbudgetCases.Register(registry);
        ToolsCases.Register(registry);
        TradeCases.Register(registry);
        UpkeepCases.Register(registry);
        WallCases.Register(registry);
        WasteCases.Register(registry);
        ZoneCases.Register(registry);
    }
}
