using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Observation;

public static class RoundsContainmentReads
{
    /// <summary>
    /// The containment cell's inputs from the frame: the entities' demand from the pawn table, the standing holders
    /// from the building table and the prediction's def inputs from the catalog. All stay unknown without Anomaly.
    /// </summary>
    internal static ContainmentPlanning ContainmentPlanningOf(RoundsFrame frame, DefinitionCatalog? catalog, IReadOnlyList<PlanningDefinition> definitions)
    {
        var output = new ContainmentPlanning
        {
            Demand = Unknown<ContainmentDemand>(),
            Holders = Unknown<EquatableArray<BuiltHolder>>(),
            Defs = Unknown<ContainmentDefs>(),
            Entities = Unknown<EquatableArray<CapturableEntity>>(),
        };
        if (!ColonyDecodeReads.RequireCatalog(catalog).HasAnomaly())
        {
            return output;
        }
        output = output with
        {
            Demand = ContainmentDemandOf(frame.Tables.Pawns.Table.Values()),
            Holders = BuiltHolders(frame.Tables.Buildings.Table.Values()),
            Entities = CapturableEntities(frame.Tables.Pawns.Table.Values()),
            Forecast = ContainmentForecastOf(frame.Tables.Pawns.Table.Values(), frame.Tables.Buildings.Table.Values(), CurvesOf(catalog!)),
        };
        static (PlanningDefinition? Def, string Reason) Definition(IReadOnlyList<PlanningDefinition> all, string name)
        {
            foreach (var d in all)
            {
                if (d.Name == name)
                {
                    return (d, "");
                }
            }
            return (null, name + " is not among the planning definitions");
        }
        var reason = "";
        var (wall, wallReason) = Definition(definitions, ContainmentCellRules.ShellWallDefinition);
        reason = wallReason;
        if (reason == "")
        {
            var (door, doorReason) = Definition(definitions, ContainmentCellRules.ShellDoorDefinition);
            reason = doorReason;
            if (reason == "")
            {
                // The shell is one stuff: the wall's cheapest, which the door must be makeable from too
                // (an unstuffed pair has no stuff).
                if (!StuffChoiceReads.SharedStuff(wall!, door!, out var stuff))
                {
                    reason = "the shell's wall and door are built from different stuff";
                }
                else
                {
                    try
                    {
                        output = output with
                        {
                            Defs = Known(catalog!.ContainmentDefs(ContainmentCellRules.ShellWallDefinition, stuff, ContainmentCellRules.ShellDoorDefinition, stuff)),
                        };
                    }
                    catch (DomainException e)
                    {
                        reason = e.Message;
                    }
                }
            }
        }
        return output with { DefsReason = reason };
    }

    /// <summary>
    /// Counts the living entities the game lets the colony capture that no platform holds, and the strength the
    /// most demanding needs. A pawn that is no entity, or an entity with no holding-platform target, asks nothing.
    /// </summary>
    private static Fact<ContainmentDemand> ContainmentDemandOf(IEnumerable<O.PawnState> rows)
    {
        var demand = new ContainmentDemand();
        foreach (var row in rows)
        {
            if (row.Dead || row.Anomaly is not O.PawnAnomalyState.Active { Facts: { Entity: true, Held: { } held } a })
            {
                continue;
            }
            if (held.Held || !held.CanBeCaptured)
            {
                continue;
            }
            demand = demand with { Entities = demand.Entities + 1, Required = Math.Max(demand.Required, a.MinContainmentStrength) };
        }
        return Known(demand);
    }

    /// <summary>The catalog's containment curves (#3811); a failed read leaves them empty with its reason.</summary>
    private static ContainmentCurves CurvesOf(DefinitionCatalog catalog)
    {
        try
        {
            return catalog.ContainmentCurves();
        }
        catch (DomainException e)
        {
            return new ContainmentCurves { Reason = e.Message };
        }
    }

    /// <summary>
    /// The escape term's and the cell credit's entities (#3811): each living entity held on a platform, with that platform's
    /// cell and strength, and each one the game lets the colony capture that no platform holds, with its need, escape and
    /// study facts; and the catalog's containment curves (a failed read is their reason). The entities are unread, which the
    /// readers raise as a required fact, when a pawn's held block failed or an entity's need, held state, escape or study
    /// facts or its platform are unread, the reason naming the pawn.
    /// </summary>
    public static ContainmentForecast ContainmentForecastOf(IEnumerable<O.PawnState> pawns, IEnumerable<O.BuildingState> buildings, ContainmentCurves curves)
    {
        ContainmentForecast Unread(string pawn, string what) => new() { Unread = pawn + ": " + what, Curves = curves };
        var platforms = new Dictionary<string, EntityPlatform>(StringComparer.Ordinal);
        foreach (var row in buildings)
        {
            if (row.Anomaly?.Holder is { HeldPawn.Id: { Length: > 0 } held, ContainmentStrength: var strength } && row.Building.Place is O.EntityPlace.OnMap { Cell: var at })
            {
                platforms[held] = new EntityPlatform(new Cell(at.X, at.Z), strength);
            }
        }
        var entities = new List<ContainedEntity>();
        foreach (var row in pawns)
        {
            if (row.Dead || row.Anomaly is not O.PawnAnomalyState.Active { Facts: var a })
            {
                continue;
            }
            if (a.Held is not { } h || !a.Entity)
            {
                continue;
            }
            if (!h.Held && !h.CanBeCaptured)
            {
                continue;
            }
            EntityPlatform? platform = null;
            if (h.Held && !platforms.TryGetValue(row.Pawn.Id, out platform))
            {
                return Unread(row.Pawn.Id, "no platform with a read strength and cell holds it");
            }
            var e = h.Escape;
            var study = a.Study?.Yield is { } y ? new StudyKnowledge(y.Knowledge, y.Category ?? "", y.FrequencyTicks) : null;
            entities.Add(new ContainedEntity(row.Pawn.Id, a.MinContainmentStrength, new EntityEscape(e.BaseMtbDays, e.MovingFactor, e.DaysInCaptivity, e.ColdFactor, e.CombatPower, e.CanEscape), study, platform));
        }
        return new ContainmentForecast { Entities = EquatableArray.From(entities), Curves = curves };
    }

    /// <summary>The standing holding platforms' native strength, availability, held pawn and room doors.</summary>
    private static Fact<EquatableArray<BuiltHolder>> BuiltHolders(IEnumerable<O.BuildingState> rows)
    {
        var holders = new List<BuiltHolder>();
        foreach (var row in rows)
        {
            if (row.Anomaly is { Holder: var h })
            {
                holders.Add(new BuiltHolder { Strength = h.ContainmentStrength, Available = h.Available, HeldPawn = h.HeldPawn?.Id ?? "", Doors = Known(h.Doors) });
            }
        }
        return Known(EquatableArray.From(holders));
    }


    /// <summary>
    /// The entity pawn rows the capture rule decides: every row the game marks an entity, with the facts the rule
    /// reads. A pawn with no holding-platform target block is one the colony cannot capture; its held entity's facts
    /// do not apply.
    /// </summary>
    private static Fact<EquatableArray<CapturableEntity>> CapturableEntities(IEnumerable<O.PawnState> rows)
    {
        var output = new List<CapturableEntity>();
        foreach (var row in rows)
        {
            if (row.Anomaly is not O.PawnAnomalyState.Active { Facts: { Entity: true } a })
            {
                continue;
            }
            var e = new CapturableEntity
            {
                Pawn = new RimGovernor.Brain.Domain.PawnId(row.Pawn.Id ?? ""),
                Dead = Known(row.Dead),
                Downed = Known(row.Downed),
                CanBeCaptured = Known(false),
                Held = Known(false),
                Need = Known(a.MinContainmentStrength),
                CurrentlyStudiable = Known(a.Study is { CurrentlyStudiable: true }),
                NeedsTend = Unknown<bool>(),
                Bleeding = Unknown<bool>(),
                Escaping = Unknown<bool>(),
                Mode = Unknown<O.EntityContainmentModeKind>(),
                ExtractBioferrite = Unknown<bool>(),
                HarvesterAttached = Unknown<bool>(),
                BioferritePerDay = Unknown<double>(),
            };
            if (a.Held is { } held)
            {
                e = e with
                {
                    CanBeCaptured = Known(held.CanBeCaptured),
                    Held = Known(held.Held),
                    NeedsTend = Known(held.NeedsTend),
                    Bleeding = Known(held.Bleeding),
                    Escaping = Known(held.Escaping),
                    Mode = Known(held.Mode),
                    ExtractBioferrite = Known(held.ExtractBioferrite),
                    HarvesterAttached = Known(held.HarvesterAttached),
                    BioferritePerDay = Known(held.BioferritePerDay),
                };
            }
            output.Add(e);
        }
        return Known(EquatableArray.From(output));
    }


    /// <summary>
    /// Lifts the pawn table's living mechanitors (a colonist with a mechanitor block) and mechs (a mechanoid with a
    /// mech block) into the mech planners' inputs. A row whose biotech read failed is skipped: its facts are
    /// unknown, so nothing is planned for it.
    /// </summary>
    public static MechFleet MechFleetOf(IEnumerable<O.PawnState> rows)
    {
        var mechanitors = new List<MechanitorInput>();
        var mechs = new List<MechInput>();
        foreach (var row in rows)
        {
            if (row is null || row.Dead)
            {
                continue;
            }
            if (!BiotechFactsRules.PawnBiotech(row.Biotech).TryGetValue(out var bt))
            {
                continue;
            }
            var id = new PawnId(row.Pawn.Id ?? "");
            var cell = RitualSitesReads.RowCell(row.Pawn.Place);
            if (bt.Mechanitor.TryGetValue(out var m) && m is not null && row.Colonist)
            {
                mechanitors.Add(new MechanitorInput { Id = id, Mechanitor = m, Cell = cell });
            }
            if (bt.Mech.TryGetValue(out var mm) && mm is not null && row.Mechanoid)
            {
                var mech = new MechInput { Id = id, Kind = row.KindDefName, Mech = mm, Cell = cell, Downed = row.Downed, Drafted = row.Drafted };
                if (row.Job.TargetA?.Target?.Entity is { } target)
                {
                    mech = mech with { Target = new PawnId(target.Id ?? "") };
                }
                mechs.Add(mech);
            }
        }
        return new MechFleet { Mechanitors = mechanitors, Mechs = mechs };
    }

    /// <summary>The pawn row's position.</summary>
    public static Fact<Cell> RowCellOf(O.PawnState row) => RitualSitesReads.RowCell(row.Pawn.Place);


    internal static Fact<EquatableArray<CarePawn>> RoundsMedical(O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot? snapshot, DefinitionCatalog catalog)
    {
        var unknown = Unknown<EquatableArray<CarePawn>>();
        var parts = new DefinitionPartCatalog(catalog);
        if (!emergency.AllSeen || colony is null || snapshot is null
            || colony.ColonistCount != emergency.Colonists.Count || snapshot.Pawns.Count != emergency.Colonists.Count)
        {
            return unknown;
        }
        var byId = new Dictionary<string, O.PawnState>(StringComparer.Ordinal);
        foreach (var row in snapshot.Pawns)
        {
            if (row is null || byId.ContainsKey((row.Pawn.Id ?? "")))
            {
                return unknown;
            }
            byId[(row.Pawn.Id ?? "")] = row;
        }
        var rows = new List<CarePawn>(snapshot.Pawns.Count);
        foreach (var pawn in emergency.Colonists)
        {
            byId.TryGetValue(pawn.Id.Value, out var row);
            if (row is null || row.Dead != pawn.Dead || row.Downed != pawn.Downed || !row.Colonist)
            {
                return unknown;
            }
            var p = new CarePawn { Id = pawn.Id, Dead = Known(pawn.Dead) };
            p = p with { Care = RoundsWorkReads.CareName(row.Settings.Care is O.PawnCare.Configured care ? care.MedicalCare : null) };
            var h = row.Health;
            var (_, operations) = SurgeryFactsRules.SurgeryFacts(h.Surgery, parts);
            p = p with { Operations = operations, QueuedSurgeries = SurgeryFactsRules.QueuedSurgeries(h.Surgery) };
            var recipes = SurgeryFactsRules.QueuedSurgeryRecipes(h.Surgery) ?? [];
            p = p with { QueuedItems = SurgeryFactsRules.QueuedSurgeryItems(recipes, parts) };
            var (conditions, lifeThreatening) = CareConditionsRules.CareConditions(h);
            p = p with
            {
                Conditions = conditions,
                LifeThreatening = lifeThreatening,
                NeedsRest = Known(h.ShouldSeekMedicalRest),
                NeedsTend = Known(h.NeedsTend),
            };
            // The list is every hediff, so it proves bad conditions resolved; a hediff the catalog cannot
            // classify leaves the answer unknown.
            var bad = false;
            var known = true;
            foreach (var condition in h.Hediffs)
            {
                if (!catalog.HediffBad(condition.DefName).TryGetValue(out var isBad))
                {
                    known = false;
                    break;
                }
                bad = bad || isBad;
            }
            if (known)
            {
                p = p with { BadConditions = Known(bad) };
            }
            rows.Add(p);
        }
        return Known(EquatableArray.From(rows));
    }
}
