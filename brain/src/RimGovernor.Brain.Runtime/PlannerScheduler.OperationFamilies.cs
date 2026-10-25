namespace RimGovernor.Brain.Runtime;

partial class PlannerScheduler
{
    /// <summary>
    /// The fact families a dispatched action of kind changes.
    /// Everything is true when the kind is not known or has no narrower answer: the whole decoded store is dropped.
    /// A building write changes the colony census, the room census and the pawns the builders are.
    /// </summary>
    internal static (bool Everything, IReadOnlyList<Pol.FactFamily> Families) OperationFamilies(ActionKind kind, bool known)
    {
        if (!known)
        {
            return (true, []);
        }
        return kind == ActionKind.Building
            ? (false, [Pol.FactFamily.Colony, Pol.FactFamily.Rooms, Pol.FactFamily.Pawns])
            : (true, []);
    }
}
