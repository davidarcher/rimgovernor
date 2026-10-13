namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>Reports a siege camp at least half asleep: of the live camped besiegers, half or more on a LayDown job.</summary>
    internal static bool CampAsleep(CombatView view)
    {
        var besiegers = LiveBesiegers(view);
        var camped = 0;
        var asleep = 0;
        foreach (var p in view.Pawns)
        {
            if (!besiegers.TryGetValue(p.Id, out var toil) || toil != SiegeCampToil)
            {
                continue;
            }
            camped++;
            if (p.Job == "LayDown")
            {
                asleep++;
            }
        }
        return camped > 0 && 2 * asleep >= camped;
    }
}
