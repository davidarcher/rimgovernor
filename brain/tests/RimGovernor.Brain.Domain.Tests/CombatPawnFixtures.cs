using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The plain combat records a policy test starts from and narrows with <c>with</c>.</summary>
internal static class CombatPawnFixtures
{
    /// <summary>A whole, unarmed pawn standing at <paramref name="cell"/> with no combat row.</summary>
    internal static CombatPawnState Pawn(PawnId id, Cell cell) => new()
    {
        Id = id,
        Cell = cell,
        Downed = false,
        Dead = false,
        Presence = new CombatPresence.NoCombatRow(),
        Weapon = null,
        WeaponFacts = new WeaponDef(),
        Kind = "",
        Sapper = false,
        Job = "",
        Animal = false,
        BloodLoss = 0,
        BleedRatePerDay = 0,
        BleedOut = new BleedOut.NotBleedingOut(),
        Prisoner = false,
        Health = 1,
        Wild = false,
        Mech = false,
        Insect = false,
    };

    /// <summary>A combat row idle at no target, undrafted, beltless and with no drugs scanned.</summary>
    internal static CombatRow Row() => new()
    {
        Stance = CombatStance.Idle,
        Target = default,
        FireMode = DraftFireMode.Undrafted,
        TargetMortar = false,
        Shield = new ShieldBelt.NoBelt(),
        MedicalSkill = 0,
        MoveSpeed = 0,
        StunTicks = 0,
        GoJuice = false,
        CarriedDrugs = new DrugInventory.NotScanned(),
    };

    /// <summary>A view off any quest site, with no stored layout, no pod arrival, no doors and no colonists.</summary>
    internal static CombatView View() => new()
    {
        SiteExit = null,
        Layout = null,
        Pods = null,
        DoorStates = [],
        Population = 0,
    };
}
