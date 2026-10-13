namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// Keeps the persistent soldier squad current: sticky members, gaps filled with the best fighters, saved only
    /// on change. Unknown pawn facts keep the stored squad. Returns the squad in force.
    /// </summary>
    internal static Pol.SoldierSquad ReviewSoldierSquad(CancellationToken ct, StoreJournal journal, GenerationSnapshot snapshot, IReadOnlyList<Pol.WorkPawn> pawns)
    {
        var world = Player.PlayerWorld(snapshot);
        var previous = journal.LoadSoldierSquad(ct, world);
        var (squad, known) = Pol.SoldierSquad.Review(previous, pawns);
        if (!known || squad.Members.SequenceEqual(previous.Members))
        {
            return previous;
        }
        journal.SaveSoldierSquad(ct, new Store.SoldierSquadRecord { World = world, Members = squad.Members });
        return squad;
    }
}
