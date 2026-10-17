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
            Class = PolicyApi.ClassifyWeapon(facts.ByTrade, facts.Ranged, facts.Melee),
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
        var work = ObservationApi.WorkPawnRow(row, catalog, things);
        facts = facts with { Profile = PolicyApi.BuildProfile(work) };
        if (work.Work.TryGetValue(out var rows))
        {
            foreach (var w in rows)
            {
                if (w.Work == WorkType.Hunting && w.Priority > 0 && !w.Disabled)
                {
                    facts = facts with { Role = WeaponRole.Hunter };
                }
            }
        }
        if (row.RaidArmor is not null && !Boundary.IssueField(row.Issues, "raid_armor"))
        {
            facts = facts with { RaidArmor = Known(row.RaidArmor.GetValueOrDefault()) };
        }
        facts = facts with { Position = new Cell(row.Pawn.Position?.X ?? 0, row.Pawn.Position?.Z ?? 0) };
        if (row.Biography is { } biography)
        {
            var capable = true;
            foreach (var tag in biography.DisabledWorkTags)
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
            facts = facts with { IncapableOfViolence = Known(!capable) };
        }
        if (row.Equipment is { } equipment)
        {
            facts = facts with { Armed = Known(equipment.Armed) };
        }
        facts = facts with { NoArms = catalog.CreepJoinerDownsides().ArmsHold(CreepJoinerFacts.CreepJoinerPawn(row)) };
        return facts;
    }
}
