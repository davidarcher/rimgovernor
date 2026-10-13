namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Wires the harness-side observation seams that the in-mod brain's state database can answer: the store reader
/// (<see cref="ObservedSession.StoreOpener"/>, <see cref="CaseJournal"/>, the phase store), the layout-plan reader and the
/// combat-fight journal. The harness points the game at <c>&lt;Output&gt;/service.sqlite</c> with <c>RIMGOVERNOR_STATE</c>
/// at launch, so every reader opens the file the game writes (<see cref="ServiceJournal.PathOf"/>).
/// </summary>
/// <remarks>
/// The seams that need the game's own data stay unwired and fail with their "no reader wired" messages:
/// <c>StarterSite.Planner</c>, <c>LayoutSiteHost.Surveyor</c>, <c>ColonyFactsSource.Reader</c>,
/// <c>GameCatalogReader.ImpressivenessReader</c>/<c>AnimalRacesReader</c> and <c>StarterSiteHost.BlightReader</c> read Defs,
/// the native facade or typed observations, which only exist inside the game process (a mod route would be needed).
/// </remarks>
internal static class HostSeams
{
    /// <summary>Assigns the store-backed seams (idempotent; a seam a caller already assigned is replaced by the same reader).</summary>
    internal static void Install()
    {
        ObservedSession.StoreOpener = async (config, ct) => await CaseStore.OpenAsync(config, ct).ConfigureAwait(false);
        ObservedSession.LayoutPlanReader = CaseStore.ReadLayoutPlanAsync;
        CombatLabResume.JournalOpener = CaseStore.OpenCombatFightsAsync;
    }
}
