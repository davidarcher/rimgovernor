using System.Globalization;

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

/// <summary>Arms the colonists without a weapon with the best weapon lying on the map, one wave at a time.</summary>
public sealed class RoundsEquipPlanner
{
    private readonly Rounder _reviewer;
    private readonly IRoundsEquipSource _native;

    public RoundsEquipPlanner(Rounder reviewer, IRoundsEquipSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsEquipPlanner: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
    }

    internal static MethodId NextEquipWaveMethod(StandardState goal) => NextWaveMethod(goal, "equip-wave-");

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
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsEquipResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(review, PolicyApi.EnsureBasicDefense, call);
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
        var wavePlans = goal.History.Where(m => m.Episode == goal.Standard.Episode).Select(m => m.Plan).ToList();
        var refusals = RefusalLedger(p.Journal, wavePlans, progress => progress.Action.Equip is { } equip ? "equip-" + equip.Pawn.Value : "", call);
        var started = _reviewer.Clock.Now();
        var identity = Boundary.Identity(state.Snapshot);
        var (emergency, _) = await _native.ReadEmergencyAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(emergency.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("step: err != nil || emergency.Context.GetTick() < int64(review.Tick)");
        }
        if ((emergency.Context?.Tick ?? 0) < review.Tick.Value)
        {
            throw new ControlException("step: err != nil || emergency.Context.GetTick() < int64(review.Tick)");
        }
        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return new RoundsEquipResult { Verdict = WaitFor(Cause.MethodUsed, "colonist_census_incomplete") };
        }
        var ids = emergency.Facts.Colonists.Select(c => c.Id.Value).ToList();
        var (reply, _) = await _native.ReadCombatPawnsAsync(identity, ids, call).ConfigureAwait(false);
        var observed = reply.Observed;
        if (observed is null)
        {
            throw new ControlException("step: observed == nil");
        }
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("step: err != nil");
        }
        if (observed.Pawns.Count != ids.Count)
        {
            throw new ControlException("step: len(observed.Pawns) != len(ids)");
        }
        var things = await FrameThingsOfAsync(_native, identity, call).ConfigureAwait(false);
        var catalog = await _native.DefinitionCatalogAsync(identity, call).ConfigureAwait(false) ?? throw new ControlException("step: no definition catalog");
        var pawns = new List<EquipCandidatePawn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in observed.Pawns)
        {
            if (row is null || row.Pawn is null || !seen.Add(row.Pawn.Id))
            {
                throw new ControlException("step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]");
            }
            var facts = EquipCandidatePawnFacts(row, catalog, things);
            var current = row.Equipment?.PrimaryId ?? "";
            if (current != "")
            {
                foreach (var item in row.Equipment!.Equipped)
                {
                    if (item.Thing?.Id == current)
                    {
                        var def = GearDef(things, item.Thing);
                        var rows = catalog.WeaponOf(def);
                        facts = facts with
                        {
                            Current = new EquipCandidateWeapon
                            {
                                Thing = current,
                                Definition = def,
                                Class = PolicyApi.ClassifyWeapon(true, rows.Ranged, rows.Melee),
                                BiocodedTo = new Domain.PawnId(item.BiocodedTo),
                                Biocoded = item.Biocoded,
                                Facts = rows,
                            },
                        };
                    }
                }
            }
            pawns.Add(facts);
        }
        var (bounds, _) = await _native.ReadMapBoundsAsync(identity, new Cell(0, 0), call).ConfigureAwait(false);
        try
        {
            Boundary.Context(bounds.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("step: err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0");
        }
        if (bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0)
        {
            throw new ControlException("step: err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0");
        }
        var (weapons, _) = await _native.ReadEquipWeaponsAsync(identity, new Cell(0, 0), new Cell(bounds.Bounds.Width - 1, bounds.Bounds.Height - 1), call).ConfigureAwait(false);
        try
        {
            Boundary.Context(weapons.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("step: err != nil");
        }
        var candidates = weapons.Targets.Select(w => EquipCandidateWeaponOf(catalog, w)).ToList();
        List<EquipCandidatePawn> pool;
        Verdict refused = default;
        var held = new List<Domain.PawnId>();
        List<EquipAssignment> assignments;
        lock (arbiter.Mu)
        {
            pool = new List<EquipCandidatePawn>(pawns.Count);
            foreach (var pawn in pawns)
            {
                if (pawn.NoArms != "")
                {
                    held.Add(pawn.Pawn);
                    continue;
                }
                var (verdict, ok) = BudgetVerdict(refusals, "equip-" + pawn.Pawn.Value, state.Snapshot);
                if (!ok)
                {
                    if (refused.IsZero)
                    {
                        refused = verdict;
                    }
                    continue;
                }
                if (!arbiter.Pawns.Contains((PawnId)pawn.Pawn))
                {
                    pool.Add(pawn);
                }
            }
            var available = candidates.Where(w => !arbiter.Resources.Contains("equip-weapon:" + w.Thing)).ToList();
            assignments = PolicyApi.AssignEquip(pool, available).ToList();
            foreach (var pair in assignments)
            {
                arbiter.Pawns.Add(pair.Pawn);
                arbiter.Resources.Add("equip-weapon:" + pair.Weapon.Thing);
            }
        }
        if (assignments.Count == 0)
        {
            if (!refused.IsZero)
            {
                return new RoundsEquipResult { Verdict = refused };
            }
            if (held.Count > 0 && pool.Count == 0)
            {
                return new RoundsEquipResult { Verdict = Refuse(Cause.NoWorker, "creepjoiner_downside_unrevealed", held[0].Value) };
            }
            return new RoundsEquipResult { Verdict = WaitFor(Cause.MethodUsed, "armory_crafts_weapon") };
        }
        var methodId = NextEquipWaveMethod(goal);
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>(assignments.Count);
        for (var i = 0; i < assignments.Count; i++)
        {
            var pair = assignments[i];
            var equip = new Equip((Domain.PawnId)pair.Pawn, pair.Weapon.Thing, pair.Weapon.Definition, pair.Weapon.Cell);
            actions.Add(DomainAction.NewEquipAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), equip));
        }
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, methodId, spec, call);
        return new RoundsEquipResult { Verdict = BuildingReasonAdmitted, Plan = id };
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
            Pawn = new Domain.PawnId(row.Pawn.Id),
            Dead = Boundary.FactBool(row.HasDead, row.Dead),
            Downed = Boundary.FactBool(row.HasDowned, row.Downed),
            Drafted = Boundary.FactBool(row.HasDrafted, row.Drafted),
            MentalState = Boundary.FactPresence(row.HasMentalState, row.Issues, "mental_state"),
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
        if (row.HasRaidArmor && !Boundary.IssueField(row.Issues, "raid_armor"))
        {
            facts = facts with { RaidArmor = Known(row.RaidArmor) };
        }
        facts = facts with { Position = new Cell(row.Pawn.Position?.X ?? 0, row.Pawn.Position?.Z ?? 0) };
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
        if (row.Equipment is { HasArmed: true } equipment && !Boundary.IssueField(equipment.Issues, "armed"))
        {
            facts = facts with { Armed = Known(equipment.Armed) };
        }
        facts = facts with { NoArms = catalog.CreepJoinerDownsides().ArmsHold(BridgeApi.CreepJoinerPawn(row)) };
        return facts;
    }
}
