using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Observation;

public static class RoundsContainmentReads
{
    /// <summary>
    /// The containment cell's inputs from the frame: the entities' demand from the pawn table, the standing holders
    /// from the building table and the prediction's def inputs from the catalog. Without Anomaly no pawn is an entity
    /// and no building a platform: no demand, no holders or entities, and the defs Inactive.
    /// </summary>
    internal static ContainmentPlanning ContainmentPlanningOf(RoundsFrame frame, DefinitionCatalog? catalog, IReadOnlyList<PlanningDefinition> definitions)
    {
        if (!ColonyDecodeReads.RequireCatalog(catalog).HasAnomaly())
        {
            return new ContainmentPlanning();
        }
        var output = new ContainmentPlanning
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
                            Defs = new ContainmentDefsRead.Read(catalog!.ContainmentDefs(ContainmentCellRules.ShellWallDefinition, stuff, ContainmentCellRules.ShellDoorDefinition, stuff)),
                        };
                    }
                    catch (DomainException e)
                    {
                        reason = e.Message;
                    }
                }
            }
        }
        return reason == "" ? output : output with { Defs = new ContainmentDefsRead.Unread(reason) };
    }

    /// <summary>
    /// Counts the living entities the game lets the colony capture that no platform holds, and the strength the
    /// most demanding needs. A pawn that is no entity, or an entity with no holding-platform target, asks nothing.
    /// </summary>
    private static ContainmentDemand ContainmentDemandOf(IEnumerable<O.PawnState> rows)
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
        return demand;
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
    private static EquatableArray<BuiltHolder> BuiltHolders(IEnumerable<O.BuildingState> rows)
    {
        var holders = new List<BuiltHolder>();
        foreach (var row in rows)
        {
            if (row.Anomaly is { Holder: var h })
            {
                holders.Add(new BuiltHolder { Strength = h.ContainmentStrength, Available = h.Available, HeldPawn = h.HeldPawn?.Id ?? "", Doors = h.Doors });
            }
        }
        return EquatableArray.From(holders);
    }


    /// <summary>
    /// The entity pawn rows the capture rule decides: every row the game marks an entity, with the facts the rule
    /// reads. A pawn with no holding-platform target block is one the colony cannot capture; its held entity's facts
    /// do not apply.
    /// </summary>
    private static EquatableArray<CapturableEntity> CapturableEntities(IEnumerable<O.PawnState> rows)
    {
        var output = new List<CapturableEntity>();
        foreach (var row in rows)
        {
            if (row.Anomaly is not O.PawnAnomalyState.Active { Facts: { Entity: true } a })
            {
                continue;
            }
            output.Add(new CapturableEntity
            {
                Pawn = new RimGovernor.Brain.Domain.PawnId(row.Pawn.Id ?? ""),
                Dead = row.Dead,
                Downed = row.Downed,
                Need = a.MinContainmentStrength,
                CurrentlyStudiable = a.Study is { CurrentlyStudiable: true },
                Target = a.Held is { } held
                    ? new PlatformTarget
                    {
                        CanBeCaptured = held.CanBeCaptured,
                        Held = held.Held,
                        NeedsTend = held.NeedsTend,
                        Bleeding = held.Bleeding,
                        Escaping = held.Escaping,
                        Mode = held.Mode,
                        ExtractBioferrite = held.ExtractBioferrite,
                        HarvesterAttached = held.HarvesterAttached,
                        BioferritePerDay = held.BioferritePerDay,
                    }
                    : null,
            });
        }
        return EquatableArray.From(output);
    }


    /// <summary>
    /// Lifts the pawn table's living mechanitors (a colonist with a mechanitor block) and mechs (a mechanoid with a
    /// mech block) into the mech planners' inputs. A row without Biotech has neither.
    /// </summary>
    public static MechFleet MechFleetOf(IEnumerable<O.PawnState> rows)
    {
        var mechanitors = new List<MechanitorInput>();
        var mechs = new List<MechInput>();
        var overseers = new Dictionary<string, Cell>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null || row.Dead || row.Biotech is not O.PawnBiotechState.Active { Facts: var block })
            {
                continue;
            }
            var id = new PawnId(row.Pawn.Id ?? "");
            if (block.Mechanitor is { } nativeMechanitor && row.Colonist)
            {
                mechanitors.Add(new MechanitorInput { Id = id, Mechanitor = PawnMechanitor.Of(nativeMechanitor) });
                if (row.Pawn.Place is O.EntityPlace.OnMap { Cell: var at })
                {
                    overseers[id.Value] = new Cell(at.X, at.Z);
                }
            }
            if (block.Mech is { } nativeMech && row.Mechanoid)
            {
                var mech = new MechInput { Id = id, Kind = row.KindDefName, Mech = PawnMech.Of(nativeMech), Place = row.Pawn.Place, Downed = row.Downed, Drafted = row.Drafted };
                if (row.Job.TargetA?.Target?.Entity is { } target)
                {
                    mech = mech with { Target = new PawnId(target.Id ?? "") };
                }
                mechs.Add(mech);
            }
        }
        return new MechFleet { Mechanitors = mechanitors, Mechs = mechs, OverseerCells = overseers };
    }


    internal static EquatableArray<CarePawn> RoundsMedical(O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot? snapshot, DefinitionCatalog catalog)
    {

        var parts = new DefinitionPartCatalog(catalog);
        if (colony is null || snapshot is null
            || colony.ColonistCount != emergency.Colonists.Count || snapshot.Pawns.Count != emergency.Colonists.Count)
        {
            throw new ObservationContractException("medical pawn census disagrees with the colony roster");
        }
        var byId = new Dictionary<string, O.PawnState>(StringComparer.Ordinal);
        foreach (var row in snapshot.Pawns)
        {
            if (row is null || byId.ContainsKey((row.Pawn.Id ?? "")))
            {
                throw new ObservationContractException("medical pawn census disagrees with the colony roster");
            }
            byId[(row.Pawn.Id ?? "")] = row;
        }
        var rows = new List<CarePawn>(snapshot.Pawns.Count);
        foreach (var pawn in emergency.Colonists)
        {
            byId.TryGetValue(pawn.Id.Value, out var row);
            if (row is null || row.Dead != pawn.Dead || row.Downed != pawn.Downed || !row.Colonist)
            {
                throw new ObservationContractException("medical pawn census disagrees with the colony roster");
            }
            var h = row.Health;
            // A colonist always has player settings; a living one on the map always has its surgery facts.
            if (row.Settings.Care is not O.PawnCare.Configured care)
            {
                throw new ObservationContractException($"colonist {pawn.Id.Value} without medical care settings");
            }
            EquatableArray<CareOperation> operations = [];
            var queued = 0;
            IReadOnlyList<Resource> items = [];
            if (h.Surgery is { } surgery)
            {
                operations = SurgeryFactsRules.CareOperations(surgery, parts);
                queued = surgery.SurgeryBills.Count;
                items = SurgeryFactsRules.QueuedSurgeryItems(SurgeryFactsRules.QueuedSurgeryRecipes(surgery), parts);
            }
            else if (!pawn.Dead)
            {
                throw new ObservationContractException($"living colonist {pawn.Id.Value} without surgery facts");
            }
            // The list is every hediff, so it proves bad conditions resolved; the catalog classifies each.
            var bad = false;
            foreach (var condition in h.Hediffs)
            {
                bad = bad || catalog.HediffBad(condition.DefName);
            }
            var p = new CarePawn
            {
                Id = pawn.Id,
                Dead = pawn.Dead,
                Care = WireEnums.RequiredMedicalCare(care.MedicalCare),
                Operations = operations,
                QueuedSurgeries = queued,
                QueuedItems = items,
                Conditions = h.Hediffs,
                LifeThreatening = h.LifeThreatening,
                NeedsRest = h.ShouldSeekMedicalRest,
                BadConditions = bad,
            };
            rows.Add(p);
        }
        return EquatableArray.From(rows);
    }
}
