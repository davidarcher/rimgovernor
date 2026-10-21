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
            if (item.Thing is null || !things.TryRow(item.Thing, out var row) || (row.Entity?.DefName ?? "").Length == 0)
            {
                return false;
            }
            weapon = catalog.WeaponOf(row.Entity?.DefName ?? "");
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
                || !p.Dead.TryGetValue(out var dead) || !p.Downed.TryGetValue(out var downed)
                || row.Dead != dead || row.Downed != downed || !row.Colonist)
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
    /// Lifts one pawn row into the planner's WorkPawn: availability from the vital flags, the work snapshot token,
    /// manual mode, timetable and priorities from the settings block, skills, traits, incapable work types and age
    /// from the biography, and whether the primary weapon is ranged (its def rows, found through the frame's things
    /// table: unknown until the table holds the weapon). A missing or issued block leaves its facts unknown.
    /// The pawn's traits are resolved against the catalog; a trait the catalog lacks is an error, and a pawn with
    /// traits needs a catalog.
    /// </summary>
    public static WorkPawn WorkPawnRow(O.PawnState row, DefinitionCatalog? catalog, Things things)
    {
        var w = WorkPawnRowFrom(row);
        // Native writes the Biotech block on every pawn when Biotech is active and always names the developmental
        // stage; a row without one is a contract break, not an adult.
        if (row.Biotech is { } b && b.DevelopmentalStage is null)
        {
            throw new ObservationContractException($"pawn {(row.Pawn.Id ?? "")} has a Biotech block without a developmental stage");
        }
        if (row.Equipment is { } e)
        {
            if (PrimaryWeapon(ColonyDecodeReads.RequireCatalog(catalog), e, things, out var weapon))
            {
                w = w with { Ranged = Known(weapon.Ranged), Hunts = Known(weapon.Hunts()) };
            }
        }
        if (w.Work.TryGetValue(out var work))
        {
            var resolved = new List<WorkPriority>(work.Count);
            foreach (var entry in work)
            {
                resolved.Add(ColonyDecodeReads.RequireCatalog(catalog).ResolveWorkRow(entry));
            }
            w = w with { Work = Known(EquatableArray.From(resolved)) };
        }
        w = w with { SeriousBloodLoss = ColonyDecodeReads.RequireCatalog(catalog).SeriousBloodLoss() };
        if (w.Traits.TryGetValue(out var traits))
        {
            var resolved = new List<PawnTrait>(traits.Count);
            foreach (var trait in traits)
            {
                resolved.Add(ResolveTrait(catalog, trait));
            }
            w = w with { Traits = Known(EquatableArray.From(resolved)) };
        }
        return w;
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

    private static WorkPawn WorkPawnRowFrom(O.PawnState row)
    {
        var w = new WorkPawn { Id = new PawnId(row.Pawn.Id ?? ""), ConstructionAble = ColonyDecodeReads.Optional(row.ConstructionCapable is not null, row.ConstructionCapable.GetValueOrDefault()) };
        w = w with { Available = Known(!row.Dead && !row.Downed && !row.Drafted && row.MentalState is null) };
        if (row.Settings is { } s)
        {
            if (s.FoodPolicyRespected is { } respected)
            {
                w = w with { FoodPolicyRespected = Known(respected) };
            }
            w = w with { PolicyInputs = PolicyInputs.PawnPolicyInputs(s.PolicyInputs) };
            if (s.Body is { } b)
            {
                w = w with
                {
                    Body = Known(new PawnBody
                    {
                        ComfortableMinC = ColonyDecodeReads.Optional(b.ComfortableMinC is not null, b.ComfortableMinC.GetValueOrDefault()),
                        ComfortableMaxC = ColonyDecodeReads.Optional(b.ComfortableMaxC is not null, b.ComfortableMaxC.GetValueOrDefault()),
                        MoveSpeed = ColonyDecodeReads.Optional(b.MoveSpeed is not null, b.MoveSpeed.GetValueOrDefault()),
                        CellTemperatureC = ColonyDecodeReads.Optional(b.CellTemperatureC is not null, b.CellTemperatureC.GetValueOrDefault()),
                        Hypothermia = ColonyDecodeReads.Optional(b.Hypothermia is not null, b.Hypothermia.GetValueOrDefault()),
                    }),
                };
            }
            if (s.MedicalCare is not null)
            {
                w = w with { MedicalCare = Known(WireEnums.MedicalCareName(s.MedicalCare.GetValueOrDefault())) };
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
                    work.Add(new WorkPriority { Work = ColonyDecodeReads.WireParse<WorkType>(entry.DefName), Priority = entry.Priority, Disabled = entry.Disabled });
                }
                w = w with { Work = Known(EquatableArray.From(work)) };
            }
        }
        if (row.Job is { } j)
        {
            // JobRow: a pawn with no job carries no def; a job always carries its def and, when a work giver issued
            // it, that giver's work type. A preemptible pawn's job is its vanilla job (#3670).
            w = w with
            {
                Job = Known(j.DefName is null
                    ? new PawnJob { Preemptible = j.Preemptible }
                    : new PawnJob { Def = j.DefName, Work = ColonyDecodeReads.WireParseOpen(j.WorkTypeDefName, WorkType.Unrecognized), Target = JobTarget(j.TargetA, j.TargetACell), Preemptible = j.Preemptible }),
            };
        }
        if (row.Biography is { } bio)
        {
            w = w with
            {
                Skills = Known(EquatableArray.From(bio.Skills.Select(entry => new WorkSkill { Learning = entry.Learning, Name = entry.DefName, Level = entry.Level, Stored = entry.StoredLevel, Disabled = entry.Disabled, Passion = SkillPassion(entry.Passion) }))),
                SkillStatCurves = bio.SkillStatCurves,
                // Traits, incapable work types and age feed the pawn profile.
                Traits = Known(EquatableArray.From(bio.Traits.Select(entry => new PawnTrait { Name = entry.DefName, Degree = entry.Degree }))),
            };
            var incapable = new List<WorkType>();
            foreach (var name in bio.IncapableWorkTypes)
            {
                incapable.Add(ColonyDecodeReads.WireParseOpen(name, WorkType.Unrecognized));
            }
            w = w with { Incapable = Known(EquatableArray.From(incapable)), Age = Known(bio.BiologicalAgeYears) };
        }
        w = w with { Inspiration = ColonyDecodeReads.Optional(row.Inspiration is not null, (row.Inspiration ?? "")), Biotech = BiotechFactsRules.PawnBiotech(row.Biotech) };
        if (w.Biotech.TryGetValue(out var bt) && bt.IsDeathresting())
        {
            w = w with { Available = Known(false) };
        }
        if (row.Settings is { } hs && hs.HostilityResponse is not null && WireEnums.HostilityName(hs.HostilityResponse.GetValueOrDefault()) is { } hostility)
        {
            w = w with { Hostility = Known(hostility) };
        }
        if (row.Biography is { } db)
        {
            var violent = true;
            foreach (var tag in db.DisabledWorkTags)
            {
                violent = violent && tag != "Violent";
            }
            w = w with { ViolenceCapable = Known(violent) };
        }
        if (row.Health is { } h)
        {
            w = w with { BloodLoss = Known(h.BloodLoss), Health = Known(h.SummaryFraction) };
        }
        if (row.Needs is { } n)
        {
            // A psylink level is known only for a psycaster: an absent psyfocus is no psylink (Psylink.cs).
            w = w with
            {
                Rest = ColonyDecodeReads.Optional(n.Rest is not null, n.Rest.GetValueOrDefault()),
                Mood = ColonyDecodeReads.Optional(n.Mood is not null, n.Mood.GetValueOrDefault()),
                Hunger = ColonyDecodeReads.Optional(n.HungerCategory is not null, n.HungerCategory.GetValueOrDefault()),
                BreakThreshold = Known(n.BreakThresholdMinor),
                PsylinkLevel = ColonyDecodeReads.Optional(n.Psyfocus is not null, n.Psyfocus?.PsylinkLevel ?? 0),
            };
        }
        if (row.Social is { } social)
        {
            w = w with { HighExpectations = ColonyDecodeReads.Optional(social.HighExpectations is not null, social.HighExpectations.GetValueOrDefault()) };
        }
        return w;
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
