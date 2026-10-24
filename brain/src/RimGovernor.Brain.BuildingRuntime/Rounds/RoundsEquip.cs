using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The equip candidate facts the armory and combat loadout read from native weapon and pawn rows.</summary>
internal static class EquipCandidates
{
    internal static EquipCandidateWeapon EquipCandidateWeaponOf(DefinitionCatalog catalog, BridgeEquipCandidate w)
    {
        var facts = catalog.WeaponOf(w.Definition);
        return new EquipCandidateWeapon
        {
            Thing = w.Thing,
            Definition = w.Definition,
            Cell = w.Cell,
            Class = EquipRules.ClassifyWeapon(facts.ByTrade, facts.Ranged, facts.Melee),
            BiocodedTo = w.BiocodedTo,
            Biocoded = w.Biocoded,
            Facts = facts,
        };
    }

    internal static EquipCandidatePawn EquipCandidatePawnFacts(O.PawnState row, DefinitionCatalog catalog, BridgeThings things)
    {
        var facts = new EquipCandidatePawn
        {
            Pawn = new Domain.PawnId((row.Pawn.Id ?? "")),
            Dead = row.Dead,
            Downed = row.Downed,
            Drafted = row.Drafted,
            RaidArmor = row.RaidArmor,
            MentalState = row.MentalState is not null,
            IncapableOfViolence = row.Biography.DisabledWorkTags.Contains("Violent"),
            Armed = row.Equipment.Armed,
        };
        var work = RoundsWorkReads.WorkPawnRow(row, catalog, things);
        facts = facts with { Profile = PawnRules.BuildProfile(work) };
        if (work.Work.Priorities().Any(w => w.Work == WorkType.Hunting && w.Priority > 0 && !w.Disabled))
        {
            facts = facts with { Role = WeaponRole.Hunter };
        }
        facts = facts with { Position = row.Pawn.Place is O.EntityPlace.OnMap { Cell: var at } ? new Cell(at.X, at.Z) : default };
        foreach (var tag in row.Biography.DisabledWorkTags)
        {
            if (tag == "Shooting")
            {
                facts = facts with { ShootingDisabled = true };
            }
        }
        facts = facts with { NoArms = catalog.CreepJoinerDownsides().ArmsHold(CreepJoinerFacts.CreepJoinerPawn(row)) };
        return facts;
    }
}
