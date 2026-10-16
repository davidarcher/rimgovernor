namespace RimGovernor.Brain.Policy;

/// <summary>
/// What one Method holds for claim-overlap admission (#3249): the labor pawns and exclusive targets
/// <see cref="PolicyFunctions.WorkClaimsOf"/> derives (buildings at their whole footprint), plus the subject of every
/// other action kind that has one. <see cref="Shared"/> targets are capacity targets (a bed, an observed food
/// stack); two Methods may both share one, and work resource admission bounds them by capacity.
/// </summary>
public sealed record MethodClaims(EquatableArray<string> Pawns, EquatableArray<string> Targets, EquatableArray<WorkTargetClaim> Shared);

public static partial class PolicyFunctions
{
    /// <summary>
    /// The claims of one Method's actions. Labor, buildings and gear batches are <see cref="WorkClaimsOf"/>'s; the
    /// other kinds claim their subject while the action's work is open:
    /// <list type="bullet">
    /// <item>bills: <c>bill/&lt;bench&gt;/&lt;recipe&gt;</c> and the bill they replace or remove (<c>bill/&lt;id&gt;</c>);</item>
    /// <item>forage, hunts, hauls, grower crops, plant cuts: the source thing;</item>
    /// <item>zones: their cells (<c>cell/x/z</c>, the namespace buildings use), and an existing zone by id (<c>zone/&lt;id&gt;</c>);
    /// a stockpile patch its zone (<c>zone/&lt;id&gt;</c>) or its shelf;</item>
    /// <item>area edits: the edited cells of the area (<c>area/&lt;name&gt;/x/z</c>), or the whole area for create and delete;</item>
    /// <item>dressing (apparel policy, gear replacement, equip): <c>gear/&lt;pawn&gt;</c> and the item;</item>
    /// <item>policy prunes: the pruned database (<c>policy/&lt;database&gt;</c>);</item>
    /// <item>work assignments and pawn settings: <c>settings/&lt;pawn&gt;</c>;</item>
    /// <item>deconstruction and uninstalling: the cell and the thing;</item>
    /// <item>roof removal and roof building: each roof cell (<c>roof/x/z</c>); floor removal: its cell (<c>floor/x/z</c>);</item>
    /// <item>area plant cuts: each cut cell (<c>cut/x/z</c>);</item>
    /// <item>cover clearance and wall removal: the cell and the thing (a backup wall's removal has no thing yet);</item>
    /// <item>assignments (a bed, a throne), bed use and building claims: the thing;</item>
    /// <item>supply allow and forbid: the stack;</item>
    /// <item>husbandry (train, tame, slaughter, release, master, sterilize): the animal;</item>
    /// <item>rituals: the ritual precept or bestowing kind (<c>ritual/&lt;kind&gt;</c>);</item>
    /// <item>ideoligion reforms: the ideoligion (<c>ideoligion/&lt;id&gt;</c>);</item>
    /// <item>surgery bills: <c>surgery/&lt;patient&gt;/&lt;recipe&gt;/&lt;part&gt;</c>;</item>
    /// <item>research selection: the project (<c>research/&lt;project&gt;</c>);</item>
    /// <item>prisoner interaction modes: <c>prisoner/&lt;pawn&gt;</c>;</item>
    /// <item>dialog answers: the window (<c>dialog/&lt;window&gt;</c>), or a joiner letter (<c>letter/&lt;id&gt;</c>);</item>
    /// <item>tends and rescues: both pawns (<c>care/&lt;pawn&gt;</c>), so one patient has one care order open and a
    /// doctor or rescuer one patient;</item>
    /// <item>trade phases: the session's participant (<c>trade/&lt;participant&gt;</c>);</item>
    /// <item>caravan departures: each crew pawn (<c>caravan/&lt;pawn&gt;</c>).</item>
    /// </list>
    /// Any other kind claims nothing beyond its MethodId.
    /// </summary>
    public static MethodClaims MethodClaimsOf(WorkAdmissionEvidence evidence, IReadOnlyCollection<WorkAction> actions)
    {
        var work = WorkClaimsOf(evidence, actions, footprint: true);
        var targets = new HashSet<string>(work.Targets, StringComparer.Ordinal);
        foreach (var row in actions)
        {
            var v = row.Progress;
            if (!(v.Unresolved || v.Stage is Stage.Pending or Stage.Prepared or Stage.Dispatched or Stage.AwaitingObservation)) continue;
            targets.UnionWith(ActionSubjects(row.Action).Where(t => t.Length != 0));
        }
        return new(work.Pawns, EquatableArray.From(targets.Order(StringComparer.Ordinal)), work.Shared);
    }

    /// <summary>
    /// The first claim <paramref name="candidate"/> shares with <paramref name="held"/>, or null when they are
    /// disjoint. A target both share as a capacity target is not an overlap.
    /// </summary>
    public static string? MethodClaimOverlap(MethodClaims candidate, MethodClaims held)
    {
        if (candidate.Pawns.FirstOrDefault(held.Pawns.Contains) is { } pawn) return "pawn/" + pawn;
        var heldTargets = held.Targets.ToHashSet(StringComparer.Ordinal);
        var sharedHere = candidate.Shared.Select(s => s.Target).ToHashSet(StringComparer.Ordinal);
        var sharedThere = held.Shared.Select(s => s.Target).ToHashSet(StringComparer.Ordinal);
        return candidate.Targets.FirstOrDefault(t => heldTargets.Contains(t) && !(sharedHere.Contains(t) && sharedThere.Contains(t)));
    }

    /// <summary>
    /// Every cell a building occupies, as claim targets: its definition's footprint rotated about the anchor, read
    /// from the review's building costs. A definition the review did not price falls back to the anchor alone;
    /// work resource admission refuses such a building before it is committed (unknown_building_materials), so
    /// only an already committed Method can claim that way.
    /// </summary>
    public static IReadOnlyList<string> BuildingClaimTargets(WorkAdmissionEvidence evidence, Building building)
    {
        var size = evidence.Buildings.TryGetValue(out var costs)
            ? costs.FirstOrDefault(c => c.Definition == building.Definition && c.Size.IsKnown)?.Size ?? default
            : default;
        if (size.TryGetValue(out var known))
        {
            var rect = OccupiedRect(building.Cell, known, building.Rotation);
            if (rect.Width > 0 && rect.Height > 0) return [.. RectangleCells(rect).Select(CellClaimTarget)];
        }
        return [BuildingClaimTarget(building)];
    }

    private static string CellClaimTarget(Cell c) => "cell/" + c.X + "/" + c.Z;

    private static IEnumerable<string> ActionSubjects(DomainAction a)
    {
        if (a.ProductionBill is { } bill)
        {
            yield return "bill/" + bill.Bench + "/" + bill.ClaimRecipe;
            if (bill.Replaces.Length != 0) yield return "bill/" + bill.Replaces;
        }
        else if (a.RemoveProductionBill is { } removal) yield return "bill/" + removal.Bill;
        else if (a.Acquisition is { } source) yield return source.Thing;
        else if (a.Haul is { } haul) yield return haul.Thing;
        else if (a.CutPlant is { } plant) yield return plant.Plant;
        else if (a.WastepackHaul is { } waste) yield return waste.Thing;
        else if (a.GrowerCrop is { } crop) yield return crop.Thing;
        else if (a.ZoneCreate is { } zone)
        {
            foreach (var c in zone.Cells) yield return CellClaimTarget(c);
            if (zone.ExtendId.Length != 0) yield return "zone/" + zone.ExtendId;
        }
        else if (a.ZoneCellEdit is { } edit) yield return "zone/" + edit.Zone;
        else if (a.ZoneDelete is { } deleted) yield return "zone/" + deleted.Zone;
        else if (a.StockpilePatch is { } patch) yield return patch.TargetKind == StorageTargetKind.Zone ? "zone/" + patch.Target : patch.Target;
        else if (a.Area is { } area)
        {
            var name = "area/" + (area.PollutionClear ? "pollution" : area.Home ? "home" : "allowed:" + area.Key);
            if (area.Operation is AreaOperation.Create or AreaOperation.Delete) yield return name;
            else foreach (var c in area.Cells) yield return name + "/" + c.X + "/" + c.Z;
        }
        else if (a.ApparelPolicy is { } apparel) yield return "gear/" + apparel.Pawn.Value;
        else if (a.GearReplace is { } gear) { yield return "gear/" + gear.Pawn.Value; yield return gear.Thing; }
        else if (a.Equip is { } equip) { yield return "gear/" + equip.Pawn.Value; yield return equip.Thing; }
        else if (a.PolicyPrune is { } prune) yield return "policy/" + prune.Database;
        else if (a.WorkAssignment is { } assignment) yield return "settings/" + assignment.Pawn.Value;
        else if (a.PawnSettings is { } settings) yield return "settings/" + settings.Pawn.Value;
        else if (a.Deconstruction is { } cut) { yield return CellClaimTarget(cut.Cell); yield return cut.Target; }
        else if (a.UninstallBuilding is { } uninstall) yield return CellClaimTarget(uninstall.Cell);
        else if (a.RemoveRoof is { } roof) { foreach (var c in roof.Cells) yield return "roof/" + c.X + "/" + c.Z; }
        else if (a.BuildRoof is { } built) { foreach (var c in built.Cells) yield return "roof/" + c.X + "/" + c.Z; }
        else if (a.AreaPlantCut is { } plants) { foreach (var c in plants.Cells) yield return "cut/" + c.X + "/" + c.Z; }
        else if (a.CoverClearance is { } cover) { yield return CellClaimTarget(cover.Cell); yield return cover.Thing; }
        else if (a.WallRemoval is { } wall) { yield return CellClaimTarget(wall.Cell); yield return wall.Original; }
        else if (a.FloorRemoval is { } floor) yield return "floor/" + floor.Cell.X + "/" + floor.Cell.Z;
        else if (a.Assign is { } assign) yield return assign.Thing;
        else if (a.BedUse is { } bed) yield return bed.Thing;
        else if (a.ClaimBuilding is { } claimed) yield return claimed.Thing;
        else if (a.SupplyAllow is { } supply) yield return supply.Thing;
        else if (a.Husbandry is { } husbandry) yield return husbandry.Animal.Value;
        else if (a.Ritual is { } ritual) yield return "ritual/" + ritual.Kind.Value;
        else if (a.IdeoligionReform is { } reform) yield return "ideoligion/" + reform.IdeoId;
        else if (a.Surgery is { } surgery) yield return "surgery/" + surgery.Pawn.Value + "/" + surgery.Recipe + "/" + surgery.Part;
        else if (a.ResearchSelect is { } research) yield return "research/" + research.Project;
        else if (a.PrisonerInteraction is { } prisoner) yield return "prisoner/" + prisoner.Pawn.Value;
        else if (a.DialogAnswer is { } answer) yield return (answer.LetterToken.Length == 0 ? "dialog/" : "letter/") + answer.WindowId;
        else if (a.Tend is { } tend) { yield return "care/" + tend.Doctor.Value; yield return "care/" + tend.Patient.Value; }
        else if (a.Rescue is { } rescue) { yield return "care/" + rescue.Rescuer.Value; yield return "care/" + rescue.Patient.Value; }
        else if (a.Trade is { } trade) yield return "trade/" + trade.Participant.Key();
        else if (a.CaravanDeparture is { } departure) { foreach (var pawn in departure.Crew) yield return "caravan/" + pawn.Value; }
    }
}
