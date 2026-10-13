using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads of the equip planner.</summary>
public interface IRoundsEquipSource
{
    (EmergencyObservation Observation, BridgeResult Result) ReadEmergency(C.Identity identity, CancellationToken ct);

    (O.ListPawnsReply Reply, BridgeResult Result) ReadCombatPawns(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct);

    (BridgeMapBounds Bounds, BridgeResult Result) ReadMapBounds(C.Identity identity, Cell origin, CancellationToken ct);

    (BridgeEquipRead Read, BridgeResult Result) ReadEquipWeapons(C.Identity identity, Cell from, Cell to, CancellationToken ct);

    DefinitionCatalog DefinitionCatalog(C.Identity identity, CancellationToken ct);
}

public sealed class RoundsEquipResult
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

    internal RoundsEquipResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
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
        if (!workable)
        {
            return new RoundsEquipResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan0 = p.Journal.LoadPlan(method.Plan, call);
            if (StoreApi.PlanOpen(plan0))
            {
                return new RoundsEquipResult { Verdict = BuildingReasonExistingWork };
            }
        }
        var wavePlans = goal.History.Where(m => m.Episode == goal.Standard.Episode).Select(m => m.Plan).ToList();
        var refusals = RefusalLedger(call, p.Journal, wavePlans, progress => progress.Action.Equip(out var equip) ? "equip-" + equip.Pawn.Value : "");
        var started = _reviewer.Clock.Now();
        var identity = BoundaryApi.Identity(state.Snapshot);
        var (emergency, _) = _native.ReadEmergency(identity, call);
        try
        {
            BoundaryApi.Context(emergency.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("step: err != nil || emergency.Context.GetTick() < int64(review.Tick)");
        }
        if (emergency.Context.Tick < (long)review.Tick)
        {
            throw new ControlException("step: err != nil || emergency.Context.GetTick() < int64(review.Tick)");
        }
        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return new RoundsEquipResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "colonist_census_incomplete") };
        }
        var ids = emergency.Facts.Colonists.Select(c => c.Id.Value).ToList();
        var (reply, _) = _native.ReadCombatPawns(identity, ids, call);
        var observed = reply.Observed;
        if (observed is null)
        {
            throw new ControlException("step: observed == nil");
        }
        try
        {
            BoundaryApi.Context(observed.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("step: err != nil");
        }
        if (observed.Pawns.Count != ids.Count)
        {
            throw new ControlException("step: len(observed.Pawns) != len(ids)");
        }
        var things = FrameThings(call, _native, identity);
        var catalog = _native.DefinitionCatalog(identity, call);
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
                        facts.Current = new EquipCandidateWeapon
                        {
                            Thing = current,
                            Definition = def,
                            Class = PolicyApi.ClassifyWeapon(true, rows.Ranged, rows.Melee),
                            BiocodedTo = new PawnId(item.BiocodedTo),
                            Biocoded = item.Biocoded,
                            Facts = rows,
                        };
                    }
                }
            }
            pawns.Add(facts);
        }
        var (bounds, _) = _native.ReadMapBounds(identity, new Cell(0, 0), call);
        try
        {
            BoundaryApi.Context(bounds.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("step: err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0");
        }
        if (bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0)
        {
            throw new ControlException("step: err != nil || bounds.Bounds.Width <= 0 || bounds.Bounds.Height <= 0");
        }
        var (weapons, _) = _native.ReadEquipWeapons(identity, new Cell(0, 0), new Cell(bounds.Bounds.Width - 1, bounds.Bounds.Height - 1), call);
        try
        {
            BoundaryApi.Context(weapons.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("step: err != nil");
        }
        var candidates = weapons.Targets.Select(w => EquipCandidateWeaponOf(catalog, w)).ToList();
        List<EquipCandidatePawn> pool;
        Verdict refused = default;
        var held = new List<PawnId>();
        List<EquipPair> assignments;
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
                if (!arbiter.Pawns.Contains(pawn.Pawn))
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
                return new RoundsEquipResult { Verdict = Refuse(PolicyApi.CauseNoWorker, "creepjoiner_downside_unrevealed", held[0].Value) };
            }
            return new RoundsEquipResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "armory_crafts_weapon") };
        }
        var methodId = NextEquipWaveMethod(goal);
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>(assignments.Count);
        for (var i = 0; i < assignments.Count; i++)
        {
            var pair = assignments[i];
            var equip = DomainApi.NewEquip(pair.Pawn, pair.Weapon.Thing, pair.Weapon.Definition, pair.Weapon.Cell);
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
            Pawn = new PawnId(row.Pawn.Id),
            Dead = BoundaryApi.FactBool(row.HasDead ? row.Dead : null),
            Downed = BoundaryApi.FactBool(row.HasDowned ? row.Downed : null),
            Drafted = BoundaryApi.FactBool(row.HasDrafted ? row.Drafted : null),
            MentalState = BoundaryApi.FactPresence(row.MentalState, row.Issues, "mental_state"),
        };
        var work = ObservationApi.WorkPawnRow(row, catalog, things);
        facts.Profile = PolicyApi.BuildProfile(work);
        if (work.Work.TryGetValue(out var rows))
        {
            foreach (var w in rows)
            {
                if (w.Work == PolicyApi.WorkHunting && w.Priority > 0 && !w.Disabled)
                {
                    facts.Role = WeaponRole.Hunter;
                }
            }
        }
        if (row.HasRaidArmor && !BoundaryApi.IssueField(row.Issues, "raid_armor"))
        {
            facts.RaidArmor = Known(row.RaidArmor);
        }
        facts.Position = new Cell(row.Pawn.Position?.X ?? 0, row.Pawn.Position?.Z ?? 0);
        if (row.Biography is { } biography && !BoundaryApi.IssueField(biography.Issues, "disabled_work_tags"))
        {
            var capable = true;
            foreach (var tag in biography.DisabledWorkTags)
            {
                if (tag == "Shooting")
                {
                    facts.ShootingDisabled = true;
                }
                if (tag == "Violent")
                {
                    capable = false;
                }
            }
            facts.IncapableOfViolence = Known(!capable);
        }
        if (row.Equipment is { HasArmed: true } equipment && !BoundaryApi.IssueField(equipment.Issues, "armed"))
        {
            facts.Armed = Known(equipment.Armed);
        }
        facts.NoArms = catalog.CreepJoinerDownsides().ArmsHold(BridgeApi.CreepJoinerPawn(row));
        return facts;
    }
}
