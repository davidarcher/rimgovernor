namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Delegates only the ordinary firing line. Native acquisition cannot honor target exclusions, so spared
    /// bleeders and nearby exploders retain targeted control. Tactical movement and specialist weapons keep
    /// their existing execution paths.
    /// </summary>
    internal static bool StationaryRangedHold(CombatMemory m, CombatRole r, CombatPawnState s) =>
        m.Tactic == CombatTactic.Hold && m.Flank is null && m.Groups.Count == 0 && !m.MechLure &&
        r.Ranged && r.Cell is not null && r.Duty == CombatDuty.None && !r.Retreat && r.Home is null &&
        r.Mortar is null && r.Ground is null && r.Repair is null &&
        s.WeaponFacts.Hunts() && !s.WeaponFacts.EMP && !s.WeaponFacts.OneUse && !s.WeaponFacts.ForcedMiss;
}
