using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using BiotechCatalog = RimGovernor.Brain.Bridge.BiotechCatalog;
using Cell = RimGovernor.Brain.Domain.Cell;
using PawnId = RimGovernor.Brain.Domain.PawnId;
namespace RimGovernor.Brain.Bridge;

public static class WorkReads
{
    /// <summary>
    /// The def facts of the primary weapon a pawn's equipment names; the empty weapon for an unarmed pawn. A gear
    /// reference carries no def name, so the weapon resolves through the frame's things table; a primary that is not
    /// among the equipped items, or that the table holds no row for, throws (ColonyGear.PrimaryWeaponOption).
    /// </summary>
    private static WeaponDef PrimaryWeapon(DefinitionCatalog catalog, RimGovernor.Brain.GameFacts.Observations.PawnEquipment equipment, Things things)
    {
        if (equipment.PrimaryId is not { } id)
        {
            return new WeaponDef();
        }
        var item = equipment.Equipped.FirstOrDefault(e => e.Thing.Id == id) ?? throw new DomainException($"primary weapon {id}: not among the pawn's equipment");
        return things.TryRow(item.Thing, out var row)
            ? catalog.WeaponOf(row.Entity.DefName)
            : throw new DomainException($"primary weapon {id}: the frame holds no row for it");
    }

    internal static WorkPawnsRead Work(
        RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot? colony, EmergencyFacts emergency, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot snapshot, DefinitionCatalog? catalog, Things things)
    {
        if (colony is null || (int)colony.ColonistCount != emergency.Colonists.Count || snapshot.Pawns.Count != emergency.Colonists.Count)
        {
            throw new ObservationContractException("work pawn census disagrees with the colony roster");
        }
        var byId = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.PawnState>(StringComparer.Ordinal);
        foreach (var row in snapshot.Pawns)
        {
            byId[row.Pawn.Id ?? ""] = row;
        }
        var rows = new List<WorkPawn>(snapshot.Pawns.Count);
        foreach (var p in emergency.Colonists)
        {
            if (!byId.TryGetValue(p.Id.Value, out var row)
                || row.Dead != p.Dead || row.Downed != p.Downed || !row.Colonist)
            {
                throw new ObservationContractException("work pawn census disagrees with the colony roster");
            }
            var w = WorkPawnRow(row, catalog, things);
            rows.Add(w);
        }
        return new WorkPawnsRead(EquatableArray.From(rows));
    }

    private static BiotechCatalog RequireBiotech(BiotechCatalog? biotech) =>
        biotech ?? throw new DomainException("biotech work facts need the Biotech catalog, which was not loaded");

    /// <summary>Join the native pawn row with the catalog-derived work and equipment inputs.</summary>
    public static WorkPawn WorkPawnRow(RimGovernor.Brain.GameFacts.Observations.PawnState row, DefinitionCatalog? catalog, Things things)
    {
        var defs = ColonyPlanningReads.RequireCatalog(catalog);
        var weapon = PrimaryWeapon(defs, row.Equipment, things);
        var biotech = row.Biotech is RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Active { Facts: var facts }
            ? new PawnBiotechStatus.Active(ResolvedBiotech(facts, defs, row.Pawn.DefName))
            : (PawnBiotechStatus)new PawnBiotechStatus.Inactive();
        return new WorkPawn
        {
            Observed = row,
            Work = WorkTable(row.Settings.Work, defs),
            Ranged = weapon.Ranged,
            Hunts = weapon.Hunts(),
            Traits = EquatableArray.From(row.Biography.Traits.Select(entry => ResolveTrait(defs, entry))),
            Biotech = biotech,
            SeriousBloodLoss = defs.SeriousBloodLoss(),
        };
    }
    private static PawnBiotech ResolvedBiotech(RimGovernor.Brain.GameFacts.Observations.PawnBiotech facts, DefinitionCatalog catalog, string race)
    {
        var biotech = RequireBiotech(catalog.Biotech);
        return new PawnBiotech { Observed = facts, Effects = biotech.GeneEffects(facts.Genes), WorkMinAges = biotech.WorkMinAges(race) };
    }
    /// <summary>
    /// The planner's work table from the settings block. Another mod's work type has no planner meaning and nothing to
    /// write back: it is left out of the rows.
    /// </summary>
    private static PawnWorkTable WorkTable(RimGovernor.Brain.GameFacts.Observations.PawnWork work, DefinitionCatalog defs)
    {
        if (work is RimGovernor.Brain.GameFacts.Observations.PawnWork.Works works)
        {
            var rows = new List<WorkPriority>();
            foreach (var entry in works.Rows)
            {
                if (WorkRules.WorkTypeOf(entry.DefName) == WorkType.Unrecognized)
                {
                    continue;
                }
                rows.Add(defs.ResolveWorkRow(new WorkPriority { Observed = entry }));
            }
            return new PawnWorkTable.Works(works, EquatableArray.From(rows));
        }
        return work is RimGovernor.Brain.GameFacts.Observations.PawnWork.Uninitialized ? new PawnWorkTable.Uninitialized() : new PawnWorkTable.NeverWorks();
    }

    /// <summary>Returns the trait with its Effects set from the catalog.</summary>
    internal static PawnTrait ResolveTrait(DefinitionCatalog? catalog, RimGovernor.Brain.GameFacts.Observations.Trait trait)
    {
        if (catalog is null)
        {
            throw new DomainException($"trait {trait.DefName} has no definition catalog to resolve against");
        }
        return new PawnTrait { Observed = trait, Effects = catalog.TraitEffects(trait.DefName, trait.Degree) };
    }

    /// <summary>
    /// The colony's rest schedule (#3750) from its workers' timetables: the colonists whose settings say they work. Null,
    /// unread, when a worker's timetable is not read (RestSchedule.Of).
    /// </summary>
    internal static RestSchedule? Rest(RimGovernor.Brain.GameFacts.Observations.PawnSnapshot snapshot)
    {
        var timetables = new List<IReadOnlyList<string>>();
        foreach (var row in snapshot.Pawns)
        {
            if (row.Colonist && row.Settings.Work is not RimGovernor.Brain.GameFacts.Observations.PawnWork.NeverWorks)
            {
                timetables.Add([.. row.Settings.Schedule.Select(slot => slot.AssignmentDefName)]);
            }
        }
        return RestSchedule.Of(timetables);
    }



}
