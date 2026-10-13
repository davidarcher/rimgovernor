namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    /// <summary>
    /// Go's <c>DefinitionCatalog.PrimaryWeapon</c>: the def facts of the primary weapon a pawn's equipment names. A gear
    /// reference carries no def name, so the weapon resolves through the frame's things table; false while the
    /// equipment does not say (no armed flag, no primary id), the primary is not among the equipped items, or the
    /// table does not hold its row yet. An unarmed pawn is known with the empty weapon.
    /// </summary>
    private static bool PrimaryWeapon(DefinitionCatalog catalog, O.PawnEquipment equipment, Things things, out WeaponDef weapon)
    {
        weapon = new WeaponDef();
        if (!equipment.HasArmed)
        {
            return false;
        }
        if (!equipment.Armed)
        {
            return true;
        }
        if (!equipment.HasPrimaryId)
        {
            return false;
        }
        foreach (var item in equipment.Equipped)
        {
            if ((item.Thing?.Id ?? "") != equipment.PrimaryId)
            {
                continue;
            }
            if (item.Thing is null || !things.TryRow(item.Thing, out var row) || (row.Thing_?.DefName ?? "").Length == 0)
            {
                return false;
            }
            weapon = catalog.WeaponOf(row.Thing_?.DefName ?? "");
            return true;
        }
        return false;
    }

    internal static Fact<EquatableArray<WorkPawn>> RoundsWork(
        O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot snapshot, DefinitionCatalog? catalog, Things things)
    {
        if (colony is null || !colony.HasColonistCount || (int)colony.ColonistCount != emergency.Colonists.Count || snapshot.Pawns.Count != emergency.Colonists.Count)
        {
            return Unknown<EquatableArray<WorkPawn>>();
        }
        var byId = new Dictionary<string, O.PawnState>(StringComparer.Ordinal);
        foreach (var row in snapshot.Pawns)
        {
            byId[row.Pawn?.Id ?? ""] = row;
        }
        var biotech = catalog?.Biotech;
        var rows = new List<WorkPawn>(snapshot.Pawns.Count);
        foreach (var p in emergency.Colonists)
        {
            if (!byId.TryGetValue(p.Id.Value, out var row)
                || !p.Dead.TryGetValue(out var dead) || !p.Downed.TryGetValue(out var downed)
                || !row.HasDead || !row.HasDowned || row.Dead != dead || row.Downed != downed || !row.HasColonist || !row.Colonist)
            {
                return Unknown<EquatableArray<WorkPawn>>();
            }
            var w = WorkPawnRow(row, catalog, things);
            if (w.Biotech.TryGetValue(out var bt))
            {
                if (bt.IsChild() is (true, true))
                {
                    var ages = RequireBiotech(biotech).WorkMinAges(row.Pawn?.DefName ?? "");
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
        if (row.Biotech is { } b && !b.HasDevelopmentalStage)
        {
            throw new ObservationContractException($"pawn {row.Pawn?.Id} has a Biotech block without a developmental stage");
        }
        if (row.Equipment is { } e)
        {
            if (PrimaryWeapon(RequireCatalog(catalog), e, things, out var weapon))
            {
                w = w with { Ranged = Known(weapon.Ranged), Hunts = Known(weapon.Hunts()) };
            }
        }
        if (w.Work.TryGetValue(out var work))
        {
            var resolved = new List<WorkPriority>(work.Count);
            foreach (var entry in work)
            {
                resolved.Add(RequireCatalog(catalog).ResolveWorkRow(entry));
            }
            w = w with { Work = Known(EquatableArray.From(resolved)) };
        }
        w = w with { SeriousBloodLoss = RequireCatalog(catalog).SeriousBloodLoss() };
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
        var w = new WorkPawn { Id = new PawnId(row.Pawn?.Id ?? ""), ConstructionAble = Optional(row.HasConstructionCapable, row.ConstructionCapable) };
        var mentalKnown = NativePresence(row.HasMentalState ? row.MentalState : null, row.Issues, "mental_state").TryGetValue(out var mental);
        if (row.Dead || row.Downed || row.Drafted || (mentalKnown && mental))
        {
            w = w with { Available = Known(false) };
        }
        else if (row.HasDead && row.HasDowned && row.HasDrafted && mentalKnown)
        {
            w = w with { Available = Known(true) };
        }
        if (row.Settings is { } s)
        {
            if (s.FoodRestriction is { } food && food.HasPolicyId && !HasIssue(s.Issues, "food_restriction"))
            {
                w = w with { FoodRestriction = Known(new FoodRestriction { PolicyId = food.PolicyId, Allowed = [.. food.AllowedDefs] }) };
            }
            w = w with { PolicyInputs = PawnPolicyInputs(s.PolicyInputs) };
            if (s.HasEatTrigger)
            {
                w = w with { EatTrigger = Known((double)s.EatTrigger) };
            }
            if (s.Body is { } b && !HasIssue(s.Issues, "body"))
            {
                w = w with
                {
                    Body = Known(new PawnBody
                    {
                        ComfortableMinC = Optional(b.HasComfortableMinC, b.ComfortableMinC),
                        ComfortableMaxC = Optional(b.HasComfortableMaxC, b.ComfortableMaxC),
                        MoveSpeed = Optional(b.HasMoveSpeed, b.MoveSpeed),
                        CarryCapacity = Optional(b.HasCarryCapacity, b.CarryCapacity),
                        ToxicResistance = Optional(b.HasToxicEnvironmentResistance, b.ToxicEnvironmentResistance),
                        CellTemperatureC = Optional(b.HasCellTemperatureC, b.CellTemperatureC),
                        Hypothermia = Optional(b.HasHypothermia, b.Hypothermia),
                        Heatstroke = Optional(b.HasHeatstroke, b.Heatstroke),
                        Frostbite = Optional(b.HasFrostbite, b.Frostbite),
                        ToxicBuildup = Optional(b.HasToxicBuildup, b.ToxicBuildup),
                        Moving = Optional(b.HasMoving, b.Moving),
                        Manipulation = Optional(b.HasManipulation, b.Manipulation),
                        Consciousness = Optional(b.HasConsciousness, b.Consciousness),
                    }),
                };
            }
            if (s.HasMedicalCare && !HasIssue(s.Issues, "medical_care"))
            {
                w = w with { MedicalCare = Known(MedicalCareName(s.MedicalCare)) };
            }
            w = w with { Applies = Optional(s.HasWorkApplies, s.WorkApplies), Manual = Optional(s.HasManualWorkPriorities, s.ManualWorkPriorities) };
            if (!HasIssue(s.Issues, "schedule") && s.Schedule.Count == 24)
            {
                var slots = new string[24];
                var known = true;
                var seen = new bool[24];
                foreach (var slot in s.Schedule)
                {
                    if (!slot.HasHour || !slot.HasAssignmentDefName || slot.Hour >= 24 || seen[slot.Hour] || slot.AssignmentDefName.Length == 0)
                    {
                        known = false;
                        break;
                    }
                    seen[slot.Hour] = true;
                    slots[slot.Hour] = slot.AssignmentDefName;
                }
                if (known)
                {
                    w = w with { Schedule = Known(EquatableArray.From(slots)) };
                }
            }
            if (!HasIssue(s.Issues, "work"))
            {
                var work = new List<WorkPriority>();
                var known = true;
                foreach (var entry in s.Work)
                {
                    if (!entry.HasPriority || !entry.HasDisabled)
                    {
                        known = false;
                        break;
                    }
                    work.Add(new WorkPriority { Work = WireParse<WorkType>(entry.DefName), Priority = (int)entry.Priority, Disabled = entry.Disabled });
                }
                if (known)
                {
                    w = w with { Work = Known(EquatableArray.From(work)) };
                }
            }
        }
        if (row.Job is { } j && !HasIssue(row.Issues, "job"))
        {
            // JobRow: a pawn with no job carries the current_job issue and no def; a job always carries its def and,
            // when a work giver issued it, that giver's work type.
            if (HasIssue(j.Issues, "current_job") && !j.HasDefName)
            {
                w = w with { Job = Known(new PawnJob()) };
            }
            else if (!HasIssue(j.Issues, "current_job") && j.HasDefName)
            {
                w = w with { Job = Known(new PawnJob { Def = j.DefName, Work = WireParse<WorkType>(j.WorkTypeDefName), Target = JobTarget(j.TargetA, j.TargetACell) }) };
            }
        }
        if (row.Biography is { } bio)
        {
            if (!HasIssue(bio.Issues, "skills"))
            {
                var skills = new List<WorkSkill>();
                var known = true;
                foreach (var entry in bio.Skills)
                {
                    if (!entry.HasLevel || !entry.HasDisabled || !entry.HasPassion)
                    {
                        known = false;
                        break;
                    }
                    skills.Add(new WorkSkill { Name = entry.DefName, Level = (int)entry.Level, Stored = (int)entry.StoredLevel, Disabled = entry.Disabled, Passion = SkillPassion(entry.Passion) });
                }
                if (known)
                {
                    w = w with { Skills = Known(EquatableArray.From(skills)) };
                }
            }
            // Traits, incapable work types and age feed the pawn profile; a read issue on them leaves the row
            // unknown and the planner skill-only for this pawn.
            if (!HasIssue(bio.Issues, "traits"))
            {
                var traits = new List<PawnTrait>();
                foreach (var entry in bio.Traits)
                {
                    traits.Add(new PawnTrait { Name = entry.DefName, Degree = (int)entry.Degree });
                }
                w = w with { Traits = Known(EquatableArray.From(traits)) };
            }
            var incapable = new List<WorkType>();
            foreach (var name in bio.IncapableWorkTypes)
            {
                incapable.Add(WireParse<WorkType>(name));
            }
            w = w with { Incapable = Known(EquatableArray.From(incapable)), Age = Optional(bio.HasBiologicalAgeYears, bio.BiologicalAgeYears) };
        }
        w = w with { Inspiration = Optional(row.HasInspiration, row.Inspiration), Biotech = PawnBiotech(row.Biotech) };
        if (w.Biotech.TryGetValue(out var bt) && bt.IsDeathresting())
        {
            w = w with { Available = Known(false) };
        }
        if (row.Settings is { } hs && hs.HasHostilityResponse && !HasIssue(hs.Issues, "hostility_response") && HostilityName(hs.HostilityResponse) is { } hostility)
        {
            w = w with { Hostility = Known(hostility) };
        }
        if (row.Settings is { } ts && ts.HasSelfTend && !HasIssue(ts.Issues, "self_tend"))
        {
            w = w with { SelfTend = Known(ts.SelfTend) };
        }
        if (row.Biography is { } db && !HasIssue(db.Issues, "disabled_work_tags"))
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
            w = w with { BloodLoss = Optional(h.HasBloodLoss, h.BloodLoss), Health = Optional(h.HasSummaryFraction, h.SummaryFraction) };
        }
        if (row.Needs is { } n && !HasIssue(row.Issues, "needs"))
        {
            w = w with
            {
                Rest = Optional(n.HasRest, n.Rest),
                Joy = Optional(n.HasJoy, n.Joy),
                Mood = Optional(n.HasMood, n.Mood),
                BreakThreshold = Optional(n.HasBreakThresholdMinor, n.BreakThresholdMinor),
            };
        }
        if (row.Social is { } social && !HasIssue(row.Issues, "social"))
        {
            w = w with { HighExpectations = Optional(social.HasHighExpectations, social.HighExpectations) };
        }
        if (row.Needs is { } pn && pn.HasPsyfocus && pn.HasPsyfocusTarget && pn.HasPsylinkLevel)
        {
            w = w with { Psyfocus = Known((double)pn.Psyfocus), PsyfocusTarget = Known((double)pn.PsyfocusTarget), PsylinkLevel = Known((int)pn.PsylinkLevel) };
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
        if (t.Entity is { } e)
        {
            var target = new JobTarget { Thing = e.Id };
            if (at is { HasX: true, HasZ: true })
            {
                target = target with { Cell = Known(new Cell(at.X, at.Z)) };
            }
            if (target.Thing.Length == 0 && !target.Cell.TryGetValue(out _))
            {
                return Unknown<JobTarget>();
            }
            return Known(target);
        }
        if (t.Cell is { HasX: true, HasZ: true } c)
        {
            return Known(new JobTarget { Cell = Known(new Cell(c.X, c.Z)) });
        }
        if (t.Unavailable is not null)
        {
            return Known(new JobTarget());
        }
        return Unknown<JobTarget>();
    }

    /// <summary>The passion name policy reads; native sends "None" for no passion, which policy reads as "".</summary>
    internal static string SkillPassion(O.Passion p) => p == O.Passion.None ? "" : PassionName(p);

    /// <summary>The MedicalCareCategory name of an optional wire care tier.</summary>
    internal static Fact<string> CareName(Ops.MedicalCare? care) =>
        care is null ? Unknown<string>() : Known(MedicalCareName(care.Value));
}
