using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using Cell = RimGovernor.Brain.Domain.Cell;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Bridge;

public static class ContainmentReads
{
    /// <summary>
    /// The containment cell's inputs from the frame: the entities' demand from the pawn table, the standing holders
    /// from the building table and the prediction's def inputs from the catalog. Without Anomaly no pawn is an entity
    /// and no building a platform: no demand, no holders or entities, and the defs Inactive.
    /// </summary>
    internal static ContainmentPlanning ContainmentPlanningOf(ReviewFrame frame, DefinitionCatalog? catalog, IReadOnlyList<PlanningDefinition> definitions)
    {
        if (!ColonyPlanningReads.RequireCatalog(catalog).HasAnomaly())
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
    private static ContainmentDemand ContainmentDemandOf(IEnumerable<RimGovernor.Brain.GameFacts.Observations.PawnState> rows)
    {
        var demand = new ContainmentDemand();
        foreach (var row in rows)
        {
            if (row.Dead || row.Anomaly is not RimGovernor.Brain.GameFacts.Observations.PawnAnomalyState.Active { Facts: { Entity: true, Held: { } held } a })
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
    public static ContainmentForecast ContainmentForecastOf(IEnumerable<RimGovernor.Brain.GameFacts.Observations.PawnState> pawns, IEnumerable<RimGovernor.Brain.GameFacts.Observations.BuildingState> buildings, ContainmentCurves curves)
    {
        ContainmentForecast Unread(string pawn, string what) => new() { Unread = pawn + ": " + what, Curves = curves };
        var platforms = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.BuildingState>(StringComparer.Ordinal);
        foreach (var row in buildings)
        {
            if (row.Anomaly?.Holder is { HeldPawn.Id: { Length: > 0 } held } && row.Building.Place is RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap)
            {
                platforms[held] = row;
            }
        }
        var entities = new List<ContainedEntity>();
        foreach (var row in pawns)
        {
            if (row.Dead || row.Anomaly is not RimGovernor.Brain.GameFacts.Observations.PawnAnomalyState.Active { Facts: var a })
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
            RimGovernor.Brain.GameFacts.Observations.BuildingState? platform = null;
            if (h.Held && !platforms.TryGetValue(row.Pawn.Id, out platform))
            {
                return Unread(row.Pawn.Id, "no platform with a read strength and cell holds it");
            }
            entities.Add(new ContainedEntity { Observed = row, Platform = platform });
        }
        return new ContainmentForecast { Entities = EquatableArray.From(entities), Curves = curves };
    }

    /// <summary>The standing holding platforms' native strength, availability, held pawn and room doors.</summary>
    private static EquatableArray<RimGovernor.Brain.GameFacts.Observations.EntityHolderState> BuiltHolders(IEnumerable<RimGovernor.Brain.GameFacts.Observations.BuildingState> rows)
    {
        var holders = new List<RimGovernor.Brain.GameFacts.Observations.EntityHolderState>();
        foreach (var row in rows)
        {
            if (row.Anomaly is { Holder: var h })
            {
                holders.Add(h);
            }
        }
        return EquatableArray.From(holders);
    }


    /// <summary>
    /// The entity pawn rows the capture rule decides: every row the game marks an entity, with the facts the rule
    /// reads. A pawn with no holding-platform target block is one the colony cannot capture; its held entity's facts
    /// do not apply.
    /// </summary>
    private static EquatableArray<CapturableEntity> CapturableEntities(IEnumerable<RimGovernor.Brain.GameFacts.Observations.PawnState> rows)
    {
        var output = new List<CapturableEntity>();
        foreach (var row in rows)
        {
            if (row.Anomaly is not RimGovernor.Brain.GameFacts.Observations.PawnAnomalyState.Active { Facts: { Entity: true } a })
            {
                continue;
            }
            output.Add(new CapturableEntity { Observed = row });
        }
        return EquatableArray.From(output);
    }


    /// <summary>
    /// Lifts the pawn table's living mechanitors (a colonist with a mechanitor block) and mechs (a mechanoid with a
    /// mech block) into the mech planners' inputs. A row without Biotech has neither.
    /// </summary>
    public static MechFleet MechFleetOf(IEnumerable<RimGovernor.Brain.GameFacts.Observations.PawnState> rows)
    {
        var mechanitors = new List<MechanitorInput>();
        var mechs = new List<MechInput>();
        var overseers = new Dictionary<string, Cell>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null || row.Dead || row.Biotech is not RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Active { Facts: var block })
            {
                continue;
            }
            var id = new PawnId(row.Pawn.Id ?? "");
            if (block.Mechanitor is { } nativeMechanitor && row.Colonist)
            {
                mechanitors.Add(new MechanitorInput { Observed = row });
                if (row.Pawn.Place is RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap { Cell: var at })
                {
                    overseers[id.Value] = new Cell(at.X, at.Z);
                }
            }
            if (block.Mech is { } nativeMech && row.Mechanoid)
            {
                mechs.Add(new MechInput { Observed = row });
            }
        }
        return new MechFleet { Mechanitors = mechanitors, Mechs = mechs, OverseerCells = overseers };
    }


    internal static EquatableArray<CarePawn> Medical(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot? colony, EmergencyFacts emergency, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot? snapshot, DefinitionCatalog catalog)
    {

        var parts = new DefinitionPartCatalog(catalog);
        if (colony is null || snapshot is null
            || colony.ColonistCount != emergency.Colonists.Count || snapshot.Pawns.Count != emergency.Colonists.Count)
        {
            throw new ObservationContractException("medical pawn census disagrees with the colony roster");
        }
        var byId = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.PawnState>(StringComparer.Ordinal);
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
            if (row.Settings.Care is not RimGovernor.Brain.GameFacts.Observations.PawnCare.Configured)
            {
                throw new ObservationContractException($"colonist {pawn.Id.Value} without medical care settings");
            }
            EquatableArray<CareOperation> operations = [];
            IReadOnlyList<Resource> items = [];
            if (h.Surgery is { } surgery)
            {
                operations = SurgeryFactsRules.CareOperations(surgery, parts);
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
                Observed = row,
                Operations = operations,
                QueuedItems = items,
                BadConditions = bad,
            };
            rows.Add(p);
        }
        return EquatableArray.From(rows);
    }
}
