namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
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
        if (!RequireCatalog(catalog).HasAnomaly())
        {
            return output;
        }
        output = output with
        {
            Demand = ContainmentDemandOf(frame.Tables.Pawns.Table.Values()),
            Holders = BuiltHolders(frame.Tables.Buildings.Table.Values()),
            Entities = CapturableEntities(frame.Tables.Pawns.Table.Values()),
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
        var (wall, wallReason) = Definition(definitions, ShellWallDefinition);
        reason = wallReason;
        if (reason == "")
        {
            var (door, doorReason) = Definition(definitions, ShellDoorDefinition);
            reason = doorReason;
            if (reason == "")
            {
                // The shell is one stuff: the wall's cheapest, which the door must be makeable from too
                // (an unstuffed pair has no stuff).
                if (!SharedStuff(wall!, door!, out var stuff))
                {
                    reason = "the shell's wall and door are built from different stuff";
                }
                else
                {
                    try
                    {
                        output = output with
                        {
                            Defs = Known(catalog!.ContainmentDefs(ShellWallDefinition, stuff, ShellDoorDefinition, stuff)),
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
    /// most demanding needs. A pawn row whose entity, held or strength fact is unread leaves the demand unknown;
    /// a pawn that is no entity, or an entity with no holding-platform target, asks nothing.
    /// </summary>
    private static Fact<ContainmentDemand> ContainmentDemandOf(IEnumerable<O.PawnState> rows)
    {
        var demand = new ContainmentDemand();
        foreach (var row in rows)
        {
            if (!PawnAnomaly(row.Anomaly).TryGetValue(out var a))
            {
                continue;
            }
            if (!a.Entity.TryGetValue(out var entity))
            {
                return Unknown<ContainmentDemand>();
            }
            if (!entity || !row.HasDead || row.Dead)
            {
                if (!row.HasDead)
                {
                    return Unknown<ContainmentDemand>();
                }
                continue;
            }
            if (!a.Held.TryGetValue(out var held))
            {
                return Unknown<ContainmentDemand>();
            }
            if (held is null)
            {
                continue;
            }
            var hk = held.Held.TryGetValue(out var isHeld);
            var ck = held.CanBeCaptured.TryGetValue(out var capturable);
            var sk = a.MinContainmentStrength.TryGetValue(out var strength);
            if (!hk || !ck || !sk)
            {
                return Unknown<ContainmentDemand>();
            }
            if (isHeld || !capturable)
            {
                continue;
            }
            demand = demand with { Entities = demand.Entities + 1, Required = Math.Max(demand.Required, strength) };
        }
        return Known(demand);
    }

    /// <summary>The standing holding platforms' native strength and availability; a holder whose facts are unread makes the list unknown.</summary>
    private static Fact<EquatableArray<BuiltHolder>> BuiltHolders(IEnumerable<O.BuildingState> rows)
    {
        var holders = new List<BuiltHolder>();
        foreach (var row in rows)
        {
            if (!BuildingAnomaly(row).TryGetValue(out var b))
            {
                continue;
            }
            if (!b.Holder.TryGetValue(out var holder))
            {
                return Unknown<EquatableArray<BuiltHolder>>();
            }
            if (holder is null)
            {
                continue;
            }
            var sk = holder.ContainmentStrength.TryGetValue(out var strength);
            var ak = holder.Available.TryGetValue(out var available);
            if (!sk || !ak)
            {
                return Unknown<EquatableArray<BuiltHolder>>();
            }
            holders.Add(new BuiltHolder { Strength = strength, Available = available, HeldPawn = holder.HeldPawn, Doors = holder.Doors });
        }
        return Known(EquatableArray.From(holders));
    }

    // rounds_entities.go

    /// <summary>
    /// The entity pawn rows the capture rule decides: every row the game marks an entity, or whose entity fact is
    /// unread, with the facts the rule reads. A pawn with no holding-platform target block is one the colony
    /// cannot capture, a known absence.
    /// </summary>
    private static Fact<EquatableArray<CapturableEntity>> CapturableEntities(IEnumerable<O.PawnState> rows)
    {
        var output = new List<CapturableEntity>();
        foreach (var row in rows)
        {
            if (!PawnAnomaly(row.Anomaly).TryGetValue(out var a))
            {
                continue;
            }
            if (a.Entity.TryGetValue(out var isEntity) && !isEntity)
            {
                continue;
            }
            var e = new CapturableEntity
            {
                Pawn = new RimGovernor.Brain.Domain.PawnId(row.Pawn?.Id ?? ""),
                Dead = Optional(row.HasDead, row.Dead),
                Downed = Optional(row.HasDowned, row.Downed),
                CanBeCaptured = Unknown<bool>(),
                Held = Unknown<bool>(),
                Need = a.MinContainmentStrength,
                CurrentlyStudiable = Unknown<bool>(),
                NeedsTend = Unknown<bool>(),
                Bleeding = Unknown<bool>(),
                Escaping = Unknown<bool>(),
                Mode = Unknown<ContainmentMode>(),
                ExtractBioferrite = Unknown<bool>(),
                HarvesterAttached = Unknown<bool>(),
                BioferritePerDay = Unknown<double>(),
            };
            if (a.Study.TryGetValue(out var study))
            {
                e = e with { CurrentlyStudiable = study is null ? Known(false) : study.CurrentlyStudiable };
            }
            if (a.Held.TryGetValue(out var held))
            {
                a.Entity.TryGetValue(out var entity);
                if (held is null)
                {
                    // No block proves "cannot be captured" only for a pawn read as an entity.
                    if (entity)
                    {
                        e = e with { CanBeCaptured = Known(false), Held = Known(false) };
                    }
                }
                else
                {
                    e = e with
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
                    };
                }
            }
            output.Add(e);
        }
        return Known(EquatableArray.From(output));
    }

    // rounds_mech.go

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
            if (!PawnBiotech(row.Biotech).TryGetValue(out var bt))
            {
                continue;
            }
            var id = new PawnId(row.Pawn?.Id ?? "");
            var cell = RowCell(row.Pawn?.Position);
            if (bt.Mechanitor.TryGetValue(out var m) && m is not null && row.Colonist)
            {
                mechanitors.Add(new MechanitorInput { Id = id, Mechanitor = m, Cell = cell });
            }
            if (bt.Mech.TryGetValue(out var mm) && mm is not null && row.Mechanoid)
            {
                var mech = new MechInput { Id = id, Kind = row.KindDefName, Mech = mm, Cell = cell, Downed = row.Downed, Drafted = row.Drafted };
                if (row.Job?.TargetA?.Entity is { } target)
                {
                    mech = mech with { Target = new PawnId(target.Id) };
                }
                mechs.Add(mech);
            }
        }
        return new MechFleet { Mechanitors = mechanitors, Mechs = mechs };
    }

    /// <summary>The pawn row's position.</summary>
    public static Fact<Cell> RowCellOf(O.PawnState row) => RowCell(row.Pawn?.Position);

    // rounds_medical.go

    private static Fact<EquatableArray<CarePawn>> RoundsMedical(O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot? snapshot, DefinitionCatalog catalog)
    {
        var unknown = Unknown<EquatableArray<CarePawn>>();
        var parts = new DefinitionPartCatalog(catalog);
        if (!emergency.ColonistsComplete.TryGetValue(out var complete) || !complete || colony is null || !colony.HasColonistCount || snapshot is null
            || colony.ColonistCount != emergency.Colonists.Count || snapshot.Pawns.Count != emergency.Colonists.Count)
        {
            return unknown;
        }
        var byId = new Dictionary<string, O.PawnState>(StringComparer.Ordinal);
        foreach (var row in snapshot.Pawns)
        {
            if (row is null || row.Pawn is null || byId.ContainsKey(row.Pawn.Id))
            {
                return unknown;
            }
            byId[row.Pawn.Id] = row;
        }
        var rows = new List<CarePawn>(snapshot.Pawns.Count);
        foreach (var pawn in emergency.Colonists)
        {
            byId.TryGetValue(pawn.Id.Value, out var row);
            var dk = pawn.Dead.TryGetValue(out var dead);
            var nk = pawn.Downed.TryGetValue(out var downed);
            if (row is null || !dk || !nk || !row.HasDead || !row.HasDowned || row.Dead != dead || row.Downed != downed || !row.HasColonist || !row.Colonist)
            {
                return unknown;
            }
            var p = new CarePawn { Id = pawn.Id, Dead = Known(dead) };
            if (row.Settings is { } settings)
            {
                p = p with { Care = CareName(settings.MedicalCare) };
            }
            if (row.Health is { } h && !HasIssue(row.Issues, "health"))
            {
                var (missing, operations) = SurgeryFacts(h, parts);
                p = p with { MissingParts = missing, Operations = operations, QueuedSurgeries = QueuedSurgeries(h) };
                p = p with { InstalledParts = InstalledParts(h, parts) };
                var recipes = QueuedSurgeryRecipes(h) ?? [];
                p = p with { QueuedRecipes = recipes, QueuedItems = QueuedSurgeryItems(recipes, parts) };
                var (conditions, lifeThreatening) = CareConditions(h);
                p = p with
                {
                    Conditions = conditions,
                    LifeThreatening = lifeThreatening,
                    NeedsRest = Optional(h.HasShouldSeekMedicalRest, h.ShouldSeekMedicalRest),
                    NeedsTend = Optional(h.HasNeedsTend, h.NeedsTend),
                };
                var c = h.HediffCompleteness;
                // A visible-only or incomplete list cannot prove that bad conditions have resolved. Other health
                // details remain independent evidence.
                if (c is not null && c.Filtered == 0 && h.HasHiddenHediffs && h.HiddenHediffs == 0 && !HasIssue(h.Issues, "hediffs"))
                {
                    var bad = false;
                    var known = true;
                    foreach (var condition in h.Hediffs)
                    {
                        if (condition is null || !condition.HasDefName)
                        {
                            known = false;
                            break;
                        }
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
                }
            }
            rows.Add(p);
        }
        return Known(EquatableArray.From(rows));
    }
}
