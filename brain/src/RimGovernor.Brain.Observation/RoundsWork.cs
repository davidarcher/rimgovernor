namespace RimGovernor.Brain.Observation;

public static class RoundsWorkReads
{
    /// <summary>
    /// The def facts of the primary weapon a pawn's equipment names. A gear
    /// reference carries no def name, so the weapon resolves through the frame's things table; false while the
    /// equipment does not say (no armed flag, no primary id), the primary is not among the equipped items, or the
    /// table does not hold its row yet. An unarmed pawn is known with the empty weapon.
    /// </summary>
    private static bool PrimaryWeapon(DefinitionCatalog catalog, O.PawnEquipment equipment, Things things, out WeaponDef weapon)
    {
        weapon = new WeaponDef();
        if (!equipment.Armed)
        {
            return true;
        }
        if (equipment.PrimaryId is null)
        {
            return false;
        }
        foreach (var item in equipment.Equipped)
        {
            if ((item.Thing?.Id ?? "") != (equipment.PrimaryId ?? ""))
            {
                continue;
            }
            if (item.Thing is null || !things.TryRow(item.Thing, out var row))
            {
                return false;
            }
            weapon = catalog.WeaponOf(row.Entity.DefName);
            return true;
        }
        return false;
    }

    internal static Fact<EquatableArray<WorkPawn>> RoundsWork(
        O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot snapshot, DefinitionCatalog? catalog, Things things)
    {
        if (colony is null || (int)colony.ColonistCount != emergency.Colonists.Count || snapshot.Pawns.Count != emergency.Colonists.Count)
        {
            return Unknown<EquatableArray<WorkPawn>>();
        }
        var byId = new Dictionary<string, O.PawnState>(StringComparer.Ordinal);
        foreach (var row in snapshot.Pawns)
        {
            byId[row.Pawn.Id ?? ""] = row;
        }
        var biotech = catalog?.Biotech;
        var rows = new List<WorkPawn>(snapshot.Pawns.Count);
        foreach (var p in emergency.Colonists)
        {
            if (!byId.TryGetValue(p.Id.Value, out var row)
                || row.Dead != p.Dead || row.Downed != p.Downed || !row.Colonist)
            {
                return Unknown<EquatableArray<WorkPawn>>();
            }
            var w = WorkPawnRow(row, catalog, things);
            if (w.Biotech.TryGetValue(out var bt))
            {
                if (bt.IsChild() is (true, true))
                {
                    var ages = RequireBiotech(biotech).WorkMinAges(row.Pawn.DefName ?? "");
                    bt = bt with { WorkMinAges = Known(ages) };
                    w = w with { Biotech = Known(bt) };
                }
                if (bt.Genes.TryGetValue(out var genes))
                {
                    var effects = RequireBiotech(biotech).GeneEffects(genes);
                    bt = bt with { Effects = Known(effects) };
                    w = w with { Biotech = Known(bt) };
                }
            }
            rows.Add(w);
        }
        return Known(EquatableArray.From(rows));
    }

    private static BiotechCatalog RequireBiotech(BiotechCatalog? biotech) =>
        biotech ?? throw new DomainException("biotech work facts need the Biotech catalog, which was not loaded");

    /// <summary>
    /// Lifts one pawn row into the planner's WorkPawn: availability from the vital flags, manual mode, timetable,
    /// priorities, care and policy inputs from the settings block, skills, traits, incapable work types and age from
    /// the biography, and whether the primary weapon is ranged (its def rows, found through the frame's things table:
    /// unknown until the table holds the weapon). Work rows and traits are resolved against the catalog, which every
    /// row needs; a trait the catalog lacks is an error.
    /// </summary>
    public static WorkPawn WorkPawnRow(O.PawnState row, DefinitionCatalog? catalog, Things things)
    {
        var defs = ColonyDecodeReads.RequireCatalog(catalog);
        var w = WorkPawnRowFrom(row, defs);
        if (PrimaryWeapon(defs, row.Equipment, things, out var weapon))
        {
            w = w with { Ranged = Known(weapon.Ranged), Hunts = Known(weapon.Hunts()) };
        }
        return w with { SeriousBloodLoss = defs.SeriousBloodLoss() };
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
    /// unread, when a colonist has no work flag, or a worker's timetable is not read (RestSchedule.Of).
    /// </summary>
    internal static RestSchedule? RoundsRest(O.PawnSnapshot snapshot)
    {
        var timetables = new List<IReadOnlyList<string>>();
        foreach (var row in snapshot.Pawns)
        {
            if (!row.Colonist)
            {
                continue;
            }
            if (row.Settings.WorkApplies is not { } works)
            {
                return null;
            }
            if (works)
            {
                timetables.Add([.. row.Settings.Schedule.Select(slot => slot.AssignmentDefName)]);
            }
        }
        return RestSchedule.Of(timetables);
    }

    private static WorkPawn WorkPawnRowFrom(O.PawnState row, DefinitionCatalog defs)
    {
        var w = new WorkPawn { Id = new PawnId(row.Pawn.Id ?? ""), ConstructionAble = ColonyDecodeReads.Optional(row.ConstructionCapable is not null, row.ConstructionCapable.GetValueOrDefault()) };
        w = w with { Available = Known(!row.Dead && !row.Downed && !row.Drafted && row.MentalState is null) };
        var s = row.Settings;
        var b = s.Body;
        w = w with
        {
            FoodPolicyRespected = Known(s.FoodPolicyRespected),
            PolicyInputs = Known(s.PolicyInputs),
            Body = Known(new PawnBody
            {
                ComfortableMinC = ColonyDecodeReads.Optional(b.ComfortableMinC is not null, b.ComfortableMinC.GetValueOrDefault()),
                ComfortableMaxC = ColonyDecodeReads.Optional(b.ComfortableMaxC is not null, b.ComfortableMaxC.GetValueOrDefault()),
                MoveSpeed = ColonyDecodeReads.Optional(b.MoveSpeed is not null, b.MoveSpeed.GetValueOrDefault()),
                CellTemperatureC = ColonyDecodeReads.Optional(b.CellTemperatureC is not null, b.CellTemperatureC.GetValueOrDefault()),
                Hypothermia = ColonyDecodeReads.Optional(b.Hypothermia is not null, b.Hypothermia.GetValueOrDefault()),
            }),
        };
        if (s.Care is O.PawnCare.Configured care)
        {
            w = w with { MedicalCare = Known(WireEnums.MedicalCareName(care.MedicalCare)) };
            if (WireEnums.HostilityName(care.HostilityResponse) is { } hostility)
            {
                w = w with { Hostility = Known(hostility) };
            }
        }
        w = w with { Applies = ColonyDecodeReads.Optional(s.WorkApplies is not null, s.WorkApplies.GetValueOrDefault()), Manual = ColonyDecodeReads.Optional(s.ManualWorkPriorities is not null, s.ManualWorkPriorities.GetValueOrDefault()) };
        // Native sends the 24 hourly slots hour 0 first, or none for a pawn without a timetable.
        if (s.Schedule.Count == 24)
        {
            w = w with { Schedule = Known(EquatableArray.From(s.Schedule.Select(slot => slot.AssignmentDefName))) };
        }
        // Work rows exist only for a pawn that works.
        if (s.WorkApplies == true)
        {
            var work = new List<WorkPriority>();
            foreach (var entry in s.Work)
            {
                // Another mod's work type has no planner meaning and nothing to write back: it is left out of the rows.
                if (ColonyDecodeReads.WireParseOpen(entry.DefName, WorkType.Unrecognized) == WorkType.Unrecognized)
                {
                    continue;
                }
                work.Add(defs.ResolveWorkRow(new WorkPriority { Work = ColonyDecodeReads.WireParse<WorkType>(entry.DefName), Priority = entry.Priority, Disabled = entry.Disabled }));
            }
            w = w with { Work = Known(EquatableArray.From(work)) };
        }
        // JobRow: a pawn with no job carries no def; a job always carries its def and, when a work giver issued
        // it, that giver's work type. A preemptible pawn's job is its vanilla job (#3670).
        var j = row.Job;
        w = w with
        {
            Job = Known(j.DefName is null
                ? new PawnJob { Preemptible = j.Preemptible }
                : new PawnJob { Def = j.DefName, Work = ColonyDecodeReads.WireParseOpen(j.WorkTypeDefName, WorkType.Unrecognized), Target = JobTarget(j.TargetA, j.TargetACell), Preemptible = j.Preemptible }),
        };
        var bio = row.Biography;
        var incapable = new List<WorkType>();
        foreach (var name in bio.IncapableWorkTypes)
        {
            incapable.Add(ColonyDecodeReads.WireParseOpen(name, WorkType.Unrecognized));
        }
        var violent = true;
        foreach (var tag in bio.DisabledWorkTags)
        {
            violent = violent && tag != "Violent";
        }
        w = w with
        {
            Skills = Known(EquatableArray.From(bio.Skills.Select(entry => new WorkSkill { Learning = entry.Learning, Name = entry.DefName, Level = entry.Level, Stored = entry.StoredLevel, Disabled = entry.Disabled, Passion = SkillPassion(entry.Passion) }))),
            SkillStatCurves = bio.SkillStatCurves,
            // Traits, incapable work types and age feed the pawn profile.
            Traits = Known(EquatableArray.From(bio.Traits.Select(entry => ResolveTrait(defs, new PawnTrait { Name = entry.DefName, Degree = entry.Degree })))),
            Incapable = Known(EquatableArray.From(incapable)),
            Age = Known(bio.BiologicalAgeYears),
            ViolenceCapable = Known(violent),
        };
        w = w with { Inspiration = ColonyDecodeReads.Optional(row.Inspiration is not null, (row.Inspiration ?? "")), Biotech = BiotechFactsRules.PawnBiotech(row.Biotech) };
        if (w.Biotech.TryGetValue(out var bt) && bt.IsDeathresting())
        {
            w = w with { Available = Known(false) };
        }
        var n = row.Needs;
        // A psylink level is known only for a psycaster: level 0 is no psylink (Psylink.cs).
        return w with
        {
            BloodLoss = Known(row.Health.BloodLoss),
            Health = Known(row.Health.SummaryFraction),
            Rest = ColonyDecodeReads.Optional(n.Rest is O.NeedLevel.Has, n.Rest is O.NeedLevel.Has rest ? rest.Level : 0),
            Mood = ColonyDecodeReads.Optional(n.Mood is O.NeedLevel.Has, n.Mood is O.NeedLevel.Has mood ? mood.Level : 0),
            Hunger = ColonyDecodeReads.Optional(n.Food is O.FoodNeed.Has, n.Food is O.FoodNeed.Has food ? food.Hunger : default),
            BreakThreshold = Known(n.BreakThresholdMinor),
            PsylinkLevel = ColonyDecodeReads.Optional(n.PsylinkLevel > 0, n.PsylinkLevel),
            HighExpectations = Known(row.Social.HighExpectations),
        };
    }

    /// <summary>Reads a job's targetA. An absent field is an older producer (unknown); an unavailable target is a job with none.</summary>
    private static Fact<JobTarget> JobTarget(O.TargetRef? t, C.Cell? at)
    {
        if (t is null)
        {
            return Unknown<JobTarget>();
        }
        if (t.Target?.Entity is { } e)
        {
            var target = new JobTarget { Thing = e.Id ?? "" };
            if (at is not null)
            {
                target = target with { Cell = Known(new Cell(at.X, at.Z)) };
            }
            if (target.Thing.Length == 0 && !target.Cell.TryGetValue(out _))
            {
                return Unknown<JobTarget>();
            }
            return Known(target);
        }
        if (t.Target?.Cell is { } c)
        {
            return Known(new JobTarget { Cell = Known(new Cell(c.X, c.Z)) });
        }
        if (t.Target?.Unavailable is not null)
        {
            return Known(new JobTarget());
        }
        return Unknown<JobTarget>();
    }

    /// <summary>The passion name policy reads; native sends "None" for no passion, which policy reads as "".</summary>
    internal static string SkillPassion(O.Passion p) => p == O.Passion.None ? "" : WireEnums.PassionName(p);

    /// <summary>The MedicalCareCategory name of an optional wire care tier.</summary>
    internal static Fact<string> CareName(Ops.MedicalCare? care) =>
        care is null ? Unknown<string>() : Known(WireEnums.MedicalCareName(care.Value));
}
