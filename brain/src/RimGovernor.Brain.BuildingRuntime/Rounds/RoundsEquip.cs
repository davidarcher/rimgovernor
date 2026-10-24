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
            Dead = Fact.Known(row.Dead),
            Downed = Fact.Known(row.Downed),
            Drafted = Fact.Known(row.Drafted),
            MentalState = row.MentalState is not null,
        };
        var work = RoundsWorkReads.WorkPawnRow(row, catalog, things);
        facts = facts with { Profile = PawnRules.BuildProfile(work) };
        if (work.Work.Priorities().Any(w => w.Work == WorkType.Hunting && w.Priority > 0 && !w.Disabled))
        {
            facts = facts with { Role = WeaponRole.Hunter };
        }
        if (row.RaidArmor is O.RaidArmor.Rated armor)
        {
            facts = facts with { RaidArmor = Known(armor.Mean) };
        }
        facts = facts with { Position = row.Pawn.Place is O.EntityPlace.OnMap { Cell: var at } ? new Cell(at.X, at.Z) : default };
        var capable = true;
        foreach (var tag in row.Biography.DisabledWorkTags)
        {
            if (tag == "Shooting")
            {
                facts = facts with { ShootingDisabled = true };
            }
            if (tag == "Violent")
            {
                capable = false;
            }
        }
        facts = facts with { IncapableOfViolence = Known(!capable), Armed = Known(row.Equipment.Armed) };
        facts = facts with { NoArms = catalog.CreepJoinerDownsides().ArmsHold(CreepJoinerFacts.CreepJoinerPawn(row)) };
        return facts;
    }
}
