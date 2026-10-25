namespace RimGovernor.Brain.Observation;

public static class RoundsWorkReads
{
    /// <summary>
    /// The def facts of the primary weapon a pawn's equipment names; the empty weapon for an unarmed pawn. A gear
    /// reference carries no def name, so the weapon resolves through the frame's things table; a primary that is not
    /// among the equipped items, or that the table holds no row for, throws (ColonyGear.PrimaryWeaponOption).
    /// </summary>
    private static WeaponDef PrimaryWeapon(DefinitionCatalog catalog, O.PawnEquipment equipment, Things things)
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

    internal static WorkPawnsRead RoundsWork(
        O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot snapshot, DefinitionCatalog? catalog, Things things)
    {
        if (colony is null || (int)colony.ColonistCount != emergency.Colonists.Count || snapshot.Pawns.Count != emergency.Colonists.Count)
        {
            throw new ObservationContractException("work pawn census disagrees with the colony roster");
        }
        var byId = new Dictionary<string, O.PawnState>(StringComparer.Ordinal);
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

    /// <summary>
    /// Lifts one pawn row into the planner's WorkPawn: availability from the vital flags, the work table, timetable,
    /// care and policy inputs from the settings block, skills, traits, incapable work types and age from the
    /// biography, and whether the primary weapon is ranged or hunts (its def rows, found through the frame's things
    /// table). Work rows and traits are resolved against the catalog, which every row needs; a trait the catalog lacks
    /// is an error.
    /// </summary>
    public static WorkPawn WorkPawnRow(O.PawnState row, DefinitionCatalog? catalog, Things things)
    {
        var defs = ColonyDecodeReads.RequireCatalog(catalog);
        var weapon = PrimaryWeapon(defs, row.Equipment, things);
        var s = row.Settings;
        var bio = row.Biography;
        var n = row.Needs;
        var biotech = row.Biotech is O.PawnBiotechState.Active { Facts: var facts }
            ? new PawnBiotechStatus.Active(ResolvedBiotech(facts, defs, row.Pawn.DefName))
            : (PawnBiotechStatus)new PawnBiotechStatus.Inactive();
        var deathresting = biotech is PawnBiotechStatus.Active { Facts: var bt } && bt.IsDeathresting();
        return new WorkPawn
        {
            Id = new PawnId(row.Pawn.Id ?? ""),
            ConstructionAble = row.ConstructionCapable,
            Available = !row.Dead && !row.Downed && !row.Drafted && row.MentalState is null && !deathresting,
            Work = WorkTable(s.Work, defs),
            Ranged = weapon.Ranged,
            Hunts = weapon.Hunts(),
            // Native sends the 24 hourly slots hour 0 first, or none for a pawn without a timetable.
            Schedule = EquatableArray.From(s.Schedule.Select(slot => slot.AssignmentDefName)),
            FoodPolicyRespected = s.FoodPolicyRespected,
            Care = s.Care,
            PolicyInputs = s.PolicyInputs,
            Body = s.Body,
            Job = Job(row.Job),
            Skills = EquatableArray.From(bio.Skills.Select(entry => new WorkSkill { Learning = entry.Learning, Name = entry.DefName, Level = entry.Level, Stored = entry.StoredLevel, Disabled = entry.Disabled, Passion = SkillPassion(entry.Passion) })),
            SkillStatCurves = bio.SkillStatCurves,
            WorkSpeeds = bio.WorkSpeeds,
            // Traits, incapable work types and age feed the pawn profile.
            Traits = EquatableArray.From(bio.Traits.Select(entry => ResolveTrait(defs, new PawnTrait { Name = entry.DefName, Degree = entry.Degree }))),
            Incapable = EquatableArray.From(bio.IncapableWorkTypes.Select(name => ColonyDecodeReads.WireParseOpen(name, WorkType.Unrecognized))),
            Age = bio.BiologicalAgeYears,
            ViolenceCapable = !bio.DisabledWorkTags.Contains("Violent"),
            Inspiration = row.Inspiration ?? "",
            Biotech = biotech,
            BloodLoss = row.Health.BloodLoss,
            SeriousBloodLoss = defs.SeriousBloodLoss(),
            Health = row.Health.SummaryFraction,
            Rest = n.Rest,
            Mood = n.Mood,
            Food = n.Food,
            BreakThreshold = n.BreakThresholdMinor,
            // Level 0 is no psylink (Psylink.cs).
            PsylinkLevel = n.PsylinkLevel,
            HighExpectations = row.Social.HighExpectations,
        };
    }

    private static PawnBiotech ResolvedBiotech(O.PawnBiotech facts, DefinitionCatalog catalog, string race)
    {
        var biotech = RequireBiotech(catalog.Biotech);
        var genes = EquatableArray.From(facts.Genes.Select(g => new PawnGene { Name = g.DefName, Active = g.Active }));
        return PawnBiotech.Of(facts, biotech.GeneEffects(genes), biotech.WorkMinAges(race));
    }
    /// <summary>
    /// The planner's work table from the settings block. Another mod's work type has no planner meaning and nothing to
    /// write back: it is left out of the rows.
    /// </summary>
    private static PawnWorkTable WorkTable(O.PawnWork work, DefinitionCatalog defs)
    {
        if (work is O.PawnWork.Works works)
        {
            var rows = new List<WorkPriority>();
            foreach (var entry in works.Rows)
            {
                if (ColonyDecodeReads.WireParseOpen(entry.DefName, WorkType.Unrecognized) == WorkType.Unrecognized)
                {
                    continue;
                }
                rows.Add(defs.ResolveWorkRow(new WorkPriority { Work = ColonyDecodeReads.WireParse<WorkType>(entry.DefName), Priority = entry.Priority, Disabled = entry.Disabled }));
            }
            return new PawnWorkTable.Works(works.ManualPriorities, EquatableArray.From(rows));
        }
        return work is O.PawnWork.Uninitialized ? new PawnWorkTable.Uninitialized() : new PawnWorkTable.NeverWorks();
    }

    /// <summary>Returns the trait with its Effects set from the catalog.</summary>
    internal static PawnTrait ResolveTrait(DefinitionCatalog? catalog, PawnTrait trait)
    {
        if (catalog is null)
        {
            throw new DomainException($"trait {trait.Name} has no definition catalog to resolve against");
        }
        return trait with { Effects = catalog.TraitEffects(trait.Name, trait.Degree) };
    }

    /// <summary>
    /// The colony's rest schedule (#3750) from its workers' timetables: the colonists whose settings say they work. Null,
    /// unread, when a worker's timetable is not read (RestSchedule.Of).
    /// </summary>
    internal static RestSchedule? RoundsRest(O.PawnSnapshot snapshot)
    {
        var timetables = new List<IReadOnlyList<string>>();
        foreach (var row in snapshot.Pawns)
        {
            if (row.Colonist && row.Settings.Work is not O.PawnWork.NeverWorks)
            {
                timetables.Add([.. row.Settings.Schedule.Select(slot => slot.AssignmentDefName)]);
            }
        }
        return RestSchedule.Of(timetables);
    }

    /// <summary>
    /// JobRow: a pawn with no job carries no def; a job always carries its def, its targetA and, when a work giver
    /// issued it, that giver's work type. A preemptible pawn's job is its vanilla job (#3670).
    /// </summary>
    private static PawnJob Job(O.JobEvidence j)
    {
        if (j.DefName is null)
        {
            return new PawnJob { Preemptible = j.Preemptible };
        }
        return new PawnJob { Def = j.DefName, Work = ColonyDecodeReads.WireParseOpen(j.WorkTypeDefName, WorkType.Unrecognized), Target = Target(j), Preemptible = j.Preemptible };
    }

    /// <summary>
    /// A job's targetA: a thing target is spawned at its cell or held, a cell target is ground, an unavailable target
    /// is a job with none. Native writes targetA on every job, so a job without one is an error.
    /// </summary>
    private static JobTarget Target(O.JobEvidence j)
    {
        var t = j.TargetA?.Target ?? throw new DomainException($"job {j.DefName}: no targetA");
        if (t.Entity is { } e)
        {
            var id = e.Id is { Length: > 0 } thing ? thing : throw new DomainException($"job {j.DefName}: a thing target without an id");
            return j.TargetACell is { } at ? new JobTarget.Spawned(id, new Cell(at.X, at.Z)) : new JobTarget.Held(id);
        }
        if (t.Cell is { } c)
        {
            return new JobTarget.Ground(new Cell(c.X, c.Z));
        }
        return t.Unavailable is not null ? new JobTarget.None() : throw new DomainException($"job {j.DefName}: an unread targetA");
    }

    /// <summary>The passion name policy reads; native sends "None" for no passion, which policy reads as "".</summary>
    internal static string SkillPassion(O.Passion p) => p == O.Passion.None ? "" : WireEnums.PassionName(p);
}
