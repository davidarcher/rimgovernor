namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads of the equip planner.</summary>
public interface IRoundsEquipSource : Observation.IDefinitionSource
{
    Task<(EmergencyObservation Observation, BridgeResult Result)> ReadEmergencyAsync(C.Identity identity, CancellationToken ct);

    Task<(O.ListPawnsReply Reply, BridgeResult Result)> ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct);

    Task<(BridgeMapBounds Bounds, BridgeResult Result)> ReadMapBoundsAsync(C.Identity identity, Cell origin, CancellationToken ct);

    Task<(BridgeEquipRead Read, BridgeResult Result)> ReadEquipWeaponsAsync(C.Identity identity, Cell from, Cell toCell, CancellationToken ct);

}

public sealed class RoundsEquipResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

/// <summary>Reports ordinary arming demand until the shared forecast has an equipment adapter.</summary>
public sealed class RoundsEquipPlanner
{
    private readonly Rounder _reviewer;

    public RoundsEquipPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsEquipPlanner: reviewer == nil");
        }
        _reviewer = reviewer;
    }

    internal async Task<RoundsEquipResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsEquipResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = await p.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsEquipResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = await p.Journal.WorkableAsync(review, PolicyApi.EnsureBasicDefense, call).ConfigureAwait(false);
        if (!workable || goal is null)
        {
            return new RoundsEquipResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan0 = p.Journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan0))
            {
                return new RoundsEquipResult { Verdict = BuildingReasonExistingWork };
            }
        }
        return new RoundsEquipResult { Verdict = AwaitingPlan("unsupported_forecast_equipment",
            "Observed equipment demand requires shared forecast worker, timing and outcome alternatives.") };
    }

    internal static EquipCandidateWeapon EquipCandidateWeaponOf(DefinitionCatalog catalog, BridgeEquipCandidate w)
    {
        var facts = catalog.WeaponOf(w.Definition);
        return new EquipCandidateWeapon
        {
            Thing = w.Thing,
            Definition = w.Definition,
            Cell = w.Cell,
            Class = PolicyApi.ClassifyWeapon(facts.ByTrade, facts.Ranged, facts.Melee),
            BiocodedTo = w.BiocodedTo,
            Biocoded = w.Biocoded,
            Facts = facts,
        };
    }

    internal static EquipCandidatePawn EquipCandidatePawnFacts(O.PawnState row, DefinitionCatalog catalog, BridgeThings things)
    {
        var facts = new EquipCandidatePawn
        {
            Pawn = new Domain.PawnId((row.Pawn?.Id ?? "")),
            Dead = Boundary.FactBool(row.Dead is not null, row.Dead.GetValueOrDefault()),
            Downed = Boundary.FactBool(row.Downed is not null, row.Downed.GetValueOrDefault()),
            Drafted = Boundary.FactBool(row.Drafted is not null, row.Drafted.GetValueOrDefault()),
            MentalState = Boundary.FactPresence(row.MentalState is not null, row.Issues, "mental_state"),
        };
        var work = ObservationApi.WorkPawnRow(row, catalog, things);
        facts = facts with { Profile = PolicyApi.BuildProfile(work) };
        if (work.Work.TryGetValue(out var rows))
        {
            foreach (var w in rows)
            {
                if (w.Work == WorkType.Hunting && w.Priority > 0 && !w.Disabled)
                {
                    facts = facts with { Role = WeaponRole.Hunter };
                }
            }
        }
        if (row.RaidArmor is not null && !Boundary.IssueField(row.Issues, "raid_armor"))
        {
            facts = facts with { RaidArmor = Known(row.RaidArmor.GetValueOrDefault()) };
        }
        facts = facts with { Position = new Cell(row.Pawn?.Position?.X ?? 0, row.Pawn?.Position?.Z ?? 0) };
        if (row.Biography is { } biography && !Boundary.IssueField(biography.Issues, "disabled_work_tags"))
        {
            var capable = true;
            foreach (var tag in biography.DisabledWorkTags)
            {
                if (tag == "Shooting")
                {
                    facts = facts with { ShootingDisabled = true };
                }
                if (tag == "Violent")
                {
                    capable = false;
                }
            }
            facts = facts with { IncapableOfViolence = Known(!capable) };
        }
        if (row.Equipment is { Armed: not null } equipment && !Boundary.IssueField(equipment.Issues, "armed"))
        {
            facts = facts with { Armed = Known(equipment.Armed.GetValueOrDefault()) };
        }
        facts = facts with { NoArms = catalog.CreepJoinerDownsides().ArmsHold(BridgeApi.CreepJoinerPawn(row)) };
        return facts;
    }
}
