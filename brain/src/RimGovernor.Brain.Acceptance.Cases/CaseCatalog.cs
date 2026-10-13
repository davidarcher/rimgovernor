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

        // The case areas land with #3090 and #3093; each area adds one line below (sorted by area).
        AnimalsCases.Register(registry);
        AnomalyCases.Register(registry);
        AuthorityCases.Register(registry);
        BedCases.Register(registry);
        BillsCases.Register(registry);
        CampaignCases.Register(registry);
        CaravanCases.Register(registry);
        ChildCases.Register(registry);
        CleanCases.Register(registry);
        CombatCases.Register(registry);
        CombatLabCases.Register(registry);
        CustodyCases.Register(registry);
        DialogCases.Register(registry);
        DraftCases.Register(registry);
        ExplainCases.Register(registry);
        FarmCases.Register(registry);
        FoodCases.Register(registry);
        HusbandryCases.Register(registry);
        IdeologyCases.Register(registry);
        LabCases.Register(registry);
        LetterCases.Register(registry);
        LifecycleCases.Register(registry);
        NeedsCases.Register(registry);
        OdysseyCases.Register(registry);
        PawnCases.Register(registry);
        PresentationCases.Register(registry);
        ProductionCases.Register(registry);
        QuestCases.Register(registry);
        RecoveryCases.Register(registry);
        ResearchCases.Register(registry);
        ReviewCases.Register(registry);
        RoomsCases.Register(registry);
        RouteCases.Register(registry);
        ScheduleCases.Register(registry);
        ServiceCases.Register(registry);
        SmokeCases.Register(registry);
        StartupCases.Register(registry);
        StorageCases.Register(registry);
        SustainedCases.Register(registry);
        SuppliesCases.Register(registry);
        TickbudgetCases.Register(registry);
        ToolsCases.Register(registry);
        TradeCases.Register(registry);
        WallCases.Register(registry);
        WasteCases.Register(registry);
        ZoneCases.Register(registry);
    }
}
