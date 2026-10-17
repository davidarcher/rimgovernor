namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Wires the harness-side observation seams that the in-mod brain's state database can answer: the store reader
/// (<see cref="ObservedSession.StoreOpener"/>, <see cref="CaseJournal"/>, the phase store), the layout-plan reader and the
/// combat-fight journal. The harness points the game at <c>&lt;Output&gt;/service.sqlite</c> with <c>RIMGOVERNOR_STATE</c>
/// at launch, so every reader opens the file the game writes (<see cref="ServiceJournal.PathOf"/>).
/// </summary>
/// <remarks>
/// The seams that need the game's own data (<c>StarterSite.Planner</c>, <c>LayoutSiteHost.Surveyor</c>,
/// <c>ColonyFactsSource.Reader</c>, <c>GameCatalogReader.ImpressivenessReader</c>/<c>FloorTerrainsReader</c>)
/// call the mod's read-only <c>rimgovernor/acceptance_read_*</c> tools through the
/// generic native route (<see cref="HostReads"/>); the decode runs in the game process (<c>AcceptanceReads</c>).
/// </remarks>
internal static class HostSeams
{
    /// <summary>Assigns the store-backed seams (idempotent; a seam a caller already assigned is replaced by the same reader).</summary>
    internal static void Install()
    {
        ObservedSession.StoreOpener = async (config, ct) => await CaseStore.OpenAsync(config, ct).ConfigureAwait(false);
        ObservedSession.LayoutPlanReader = CaseStore.ReadLayoutPlanAsync;
        CombatLabResume.JournalOpener = CaseStore.OpenCombatFightsAsync;

        // The game-side reads: the mod's rimgovernor/acceptance_read_* tools over the generic native route.
        var planner = new HostReads.StarterSiteReader();
        StarterSite.Planner = planner;
        StarterSiteHost.Planner = planner;
        LayoutSiteHost.Surveyor = HostReads.SurveyAsync;
        ColonyFactsSource.Reader = HostReads.ColonyFactsAsync;
        GameCatalogReader.ImpressivenessReader = HostReads.ImpressivenessAsync;
        GameCatalogReader.FloorTerrainsReader = HostReads.FloorTerrainsAsync;
    }
}
