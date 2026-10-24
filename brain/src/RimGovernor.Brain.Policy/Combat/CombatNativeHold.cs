namespace RimGovernor.Brain.Policy;

public static partial class CombatNativeHoldRules
{
    /// <summary>
    /// Delegates only the ordinary firing line. Native acquisition cannot honor target exclusions, so spared
    /// bleeders and nearby exploders retain targeted control. Tactical movement and specialist weapons keep
    /// their existing execution paths.
    /// </summary>
    internal static bool StationaryRangedHold(CombatMemory m, CombatRole r, CombatPawnState s) =>
        m.Tactic == CombatTactic.Hold && m.Flank is null && m.Groups.Count == 0 && !m.MechLure &&
        r.Ranged && r.Post is CombatPost.At && r.Duty == CombatDuty.None && !r.Retreat && r.Standby is CombatPost.Free &&
        r.Fire is RoleFire.Own && r.RepairDoor is RoleRepair.NoDoor &&
        s.WeaponFacts.Hunts() && !s.WeaponFacts.EMP && !s.WeaponFacts.OneUse && !s.WeaponFacts.ForcedMiss;
}
