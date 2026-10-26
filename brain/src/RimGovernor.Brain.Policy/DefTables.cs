namespace RimGovernor.Brain.Policy;

public static class DefTablesRules
{
    /// <summary>
    /// The items a Beggars quest's request may be answered with. Native gives whatever the lord toil requests,
    /// so which items the colony hands over is policy's choice, not a game rule.
    /// </summary>
    public static readonly IReadOnlyList<string> QuestGiftDefs = ["Beer", "MedicineHerbal", "MedicineIndustrial", "Penoxycyline", "Silver"];

}
