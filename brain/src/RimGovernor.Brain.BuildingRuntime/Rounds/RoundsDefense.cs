using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads of the defense planner: the geometry question and the combat read.</summary>
public interface IRoundsDefenseSource
{
    Task<O.DefenseSiteSnapshot> ReadDefenseSiteAsync(C.Identity identity, BridgeCellRect region, CancellationToken ct);

    M.CombatGeometry CombatGeometry(M.CombatGeometryRequest request, CancellationToken ct);

    Task<BridgeCombat> ReadCombatAsync(C.Identity identity, CancellationToken ct);
}

/// <summary>Runs combat batch actions through the executor.</summary>
public interface ICombatHands
{
    IReadOnlyList<BatchItem> RunBatch(PlanId plan, IReadOnlyList<ActionId> actions, CancellationToken ct);
}

public sealed class RoundsDefenseResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

/// <summary>Plans the colony's fight: squad admission, per-tick combat orders, subdue of breaking colonists and the post-fight cleanup.</summary>
public sealed partial class RoundsDefensePlanner
{
    private readonly ICombatHands _hands;
    private readonly Rounder _reviewer;
    private readonly IRoundsDefenseSource _native;
    private CombatTuning? _tuning;

    public RoundsDefensePlanner(Rounder reviewer, IRoundsDefenseSource native, ICombatHands hands)
    {
        if (reviewer is null || native is null || hands is null)
        {
            throw new ControlException("NewRoundsDefensePlanner: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
        _hands = hands;
    }

    /// <summary>Applies the combat tuning of the profile to every fight memory this planner starts.</summary>
    public RoundsDefensePlanner WithCombatTuning(CombatTuning? t)
    {
        _tuning = t;
        return this;
    }

    internal CombatMemory NewFightMemory(Tick tick)
    {
        var m = new CombatMemory { Tick = tick };
        if (_tuning is not null)
        {
            m = m.WithTuning(_tuning);
        }
        return m;
    }

    internal async Task<RoundsDefenseResult> DecideAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsDefenseResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("decide: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        try
        {
            await RestoreReloadedCombatAsync(state, call).ConfigureAwait(false);
        }
        catch (CombatRestorationPendingException ex)
        {
            Tel.Decide(new Decision
            {
                Kind = "admission",
                Component = "routine-defense",
                Verdict = "held",
                Reason = "restoration_pending",
                Attrs = new Dictionary<string, object?> { ["error"] = ex.Message },
            });
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsDefenseResult { Verdict = BuildingReasonNoReview };
        }
        // The review's combat standing (#3311): filed while combat is active or the military/combat Need still
        // holds open work; none once it is settled.
        if (review.Combat is not { } standing)
        {
            return new RoundsDefenseResult { Verdict = BuildingReasonNoDeficit };
        }
        var owner = p.Journal.LoadNeed(MilitaryFamily.Combat, state.Snapshot, call);
        var (subdued, handled) = await ReconcileSubdueFightAsync(owner, state, call, epoch).ConfigureAwait(false);
        if (handled)
        {
            return subdued;
        }
        if (standing.Situation == Situation.Clear)
        {
            SettleUnissuedWork(owner, call);
            var (post, held0) = await PostFightAsync(owner, state, review.Tick, arbiter, call, epoch).ConfigureAwait(false);
            if (held0)
            {
                return post;
            }
            foreach (var method in owner.Methods)
            {
                if (IsFightMethod(method.Method))
                {
                    await ClearFightDoorsAsync(state, method.Plan, call).ConfigureAwait(false);
                    p.Journal.CloseCombatFight(method.Plan, call);
                }
            }
            return new RoundsDefenseResult { Verdict = BuildingReasonNoDeficit };
        }
        if (standing.Situation != Situation.Active || review.VetoCombat() != "")
        {
            return new RoundsDefenseResult { Verdict = BuildingReasonNoDeficit };
        }
        CombatFight? fight = null;
        PlanId fightPlan = default;
        foreach (var method in owner.Methods)
        {
            if (IsFightMethod(method.Method))
            {
                if (p.Journal.LoadCombatFight(method.Plan, call) is not { Open: true } record)
                {
                    continue;
                }
                if (fight is not null)
                {
                    return new RoundsDefenseResult { Verdict = BuildingReasonExistingWork };
                }
                fight = record;
                fightPlan = method.Plan;
                continue;
            }
            var plan = p.Journal.LoadPlan(method.Plan, call);
            if (plan.Spec.Actions.Count == 1 && plan.Spec.Actions[0].Kind == ActionKind.CombatBatch)
            {
                continue;
            }
            if (StorePackage.PlanOpen(plan))
            {
                return new RoundsDefenseResult { Verdict = BuildingReasonExistingWork };
            }
        }
        var started = _reviewer.Clock.Now();
        var identity = Boundary.Identity(state.Snapshot);
        var combat = await _native.ReadCombatAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(combat.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("decide: err != nil || combat.Emergency.Context == nil || combat.Context.GetTick() < int64(review.Tick)");
        }
        if (combat.Context is null || combat.Emergency.Context is null || combat.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("decide: err != nil || combat.Emergency.Context == nil || combat.Context.GetTick() < int64(review.Tick)");
        }
        var (inputs, reason) = CombatFrameInputs(combat, [.. standing.Prey], [.. standing.Hunters]);
        if (inputs is null || !reason.IsZero)
        {
            if (fight is { Memory.Tactic: CombatTactic.Hunt } && reason == NoFight)
            {
                // The prey left the combat read: the hunt has nothing to shoot at, and its gunners are let go.
                await ReleaseHuntAsync(state, owner, fightPlan, "hunt_prey_gone", call).ConfigureAwait(false);
            }
            return new RoundsDefenseResult { Verdict = reason };
        }
        var inp = inputs;
        inp.Needed = await DraftNeeds.PlannedDraftsAsync(p.Journal, call).ConfigureAwait(false);
        inp.Preferences = _reviewer.PreferencesOf(state.Snapshot, call);
        var emergency = combat.Emergency;
        var rows = inp.Rows;
        var broke = await PlanBreakAsync(owner, state, started, arbiter, new Tick(combat.Context?.Tick ?? 0), emergency.Facts, rows, call, epoch).ConfigureAwait(false);
        if (!broke.Verdict.IsZero)
        {
            return broke;
        }
        IReadOnlyList<PawnId> orderable = [];
        var memory = NewFightMemory(default);
        if (fight is not null)
        {
            memory = fight.Memory;
            orderable = FightOrderable(fight, rows);
        }
        var layout = p.Journal.LoadDefenseLayout(new StoreWorld { Colony = state.Snapshot.Colony, Load = state.Snapshot.Load, Map = state.Snapshot.Map }, call);
        CombatLayout? held = null;
        if (layout is not null && layout.CombatReady() && p.Journal.LayoutPlan(state.Snapshot, new Tick(combat.Context?.Tick ?? 0), call) is { Plan: { } layoutPlan })
        {
            held = CombatLayoutOf(layoutPlan);
        }
        var drug = await _reviewer.CombatDrugsAsync(state.Snapshot, call).ConfigureAwait(false);
        var view = CombatView(combat, inp, orderable, held);
        view = view with { Royalty = _reviewer.Census.Remembered() };
        view = view with { Drugs = [.. drug] };
        var tick = view.Tick;
        var stop = CombatStop(combat, memory.Tick);
        await ReconcileCombatBatchesAsync(state, owner, view, call).ConfigureAwait(false);
        var (orders, ask, next) = CombatDecideRules.DecideCombat(view, new GeometryReply(), stop, memory);
        var recorded = new SnapshotCombatStop { Tick = tick, Stop = stop, Orderable = [.. orderable.Select(o => (Domain.PawnId)o)], Ask = ask, MemoryIn = memory };
        if (held is { } layoutKnown)
        {
            recorded.Layout = layoutKnown;
        }
        if (ask is not null)
        {
            recorded.Reply = AnswerGeometry(Boundary.Identity(state.Snapshot), ask, call);
            (orders, _, next) = CombatDecideRules.DecideCombat(view, recorded.Reply, stop, memory);
        }
        recorded.MemoryOut = next;
        var reconsider = CombatShelterRules.ShelterReconsider(view, memory);
        if (reconsider != "")
        {
            Tel.Decide(new Decision
            {
                Kind = "admission",
                Component = "routine-defense",
                Verdict = "held",
                Reason = reconsider,
                Target = owner.Need.Value,
                Attrs = new Dictionary<string, object?> { ["combat_plan"] = fightPlan, ["tactic"] = next.Tactic },
            });
        }
        if (next.Formed == tick && next.Refusal != "" && next.Tactic != CombatTactic.Hold)
        {
            DefensePipeline.DefenseAction("routine-defense", TelemetryLevel.Info, "refused", "hold_refused", owner.Need.Value, new Dictionary<string, object?> { ["need"] = owner.Need.Value, ["gate"] = next.Refusal }, call);
        }
        if (fight is null)
        {
            var result = await AdmitFightAsync(state, started, arbiter, new FightAdmission { Combat = combat, In = inp, Held = held, Reply = recorded.Reply ?? new GeometryReply(), Memory = next }, call, epoch).ConfigureAwait(false);
            if (result.Verdict == BuildingReasonAdmitted)
            {
                recorded.Plan = result.Plan;
                RecordCombatStop(combat, recorded, call);
            }
            return result;
        }
        var id = fightPlan;
        recorded.Plan = id;
        var drafts = new List<PawnId>();
        var unclaimed = UndraftedRoles(next, orderable, view);
        if (unclaimed.Count > 0)
        {
            var plan = p.Journal.LoadPlan(id, call);
            var pending = LoadoutPending(plan);
            unclaimed.RemoveAll(pawn => pending.Contains(pawn));
        }
        if (unclaimed.Count > 0 && arbiter.TryClaim(unclaimed))
        {
            drafts = unclaimed;
            var withDrafts = orderable.ToList();
            withDrafts.AddRange(drafts);
            withDrafts.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
            recorded.Orderable = [.. withDrafts.Select(o => (Domain.PawnId)o)];
            view = CombatView(combat, inp, withDrafts, held);
                view = view with { Royalty = _reviewer.Census.Remembered() };
            view = view with { Drugs = [.. drug] };
            var (o2, more, n2) = CombatDecideRules.DecideCombat(view, recorded.Reply ?? new GeometryReply(), stop, memory);
            orders = o2;
            next = n2;
            if (more is not null && recorded.Ask is null)
            {
                recorded.Ask = more;
                recorded.Reply = AnswerGeometry(Boundary.Identity(state.Snapshot), more, call);
                (orders, more, next) = CombatDecideRules.DecideCombat(view, recorded.Reply, stop, memory);
            }
            if (more is not null)
            {
                orders = [];
                next = memory;
            }
            recorded.MemoryOut = next;
        }
        if (next.Tactic == CombatTactic.Hunt)
        {
            // A hunt's roster is its formation: a gunner it gives no role is let go, and with none left the hunt ends.
            if (next.Roles.Count == 0)
            {
                await ReleaseHuntAsync(state, owner, id, "hunt_formation_empty", call).ConfigureAwait(false);
                return new RoundsDefenseResult { Verdict = WaitFor(Cause.MethodUsed, "hunt_formation_empty") };
            }
            var idle = CombatHuntRules.IdleHunters(fight.Roster, next.Roles.Items);
            if (idle.Count > 0)
            {
                p.Journal.UpdateCombatRoster(id, [], idle, call);
            }
        }
        var (rest, permits) = SplitPermitCalls(orders);
        CommitPermitCalls(state.Snapshot, permits, call, epoch);
        var guards = RoundsMech.CombatMechGuards(combat, inp.HostileIds, inp.Rows);
        drafts.AddRange(guards.Drafts.Select(d => (PawnId)d));
        var allOrders = rest.ToList();
        allOrders.AddRange(guards.Orders);
        if (allOrders.Count + drafts.Count == 0 && permits.Count > 0)
        {
            p.Journal.SaveCombatMemory(id, next, call);
            RecordCombatStop(combat, recorded, call);
            return new RoundsDefenseResult { Verdict = BuildingReasonAdmitted, Plan = id };
        }
        if (allOrders.Count + drafts.Count == 0)
        {
            if (next.Formed != memory.Formed || next.Tactic != memory.Tactic || !next.Roles.SequenceEqual(memory.Roles))
            {
                p.Journal.SaveCombatMemory(id, next, call);
                RecordCombatStop(combat, recorded, call);
                if (memory.Tactic == CombatTactic.Hold && next.Tactic != CombatTactic.Hold)
                {
                    return new RoundsDefenseResult { Verdict = BuildingReasonHoldFallback, Plan = id };
                }
            }
            return new RoundsDefenseResult { Verdict = BuildingReasonExistingWork, Plan = id };
        }
        p.Current(call, epoch);
        var (batch, sent, next3) = await IssueCombatOrdersAsync(state, id, view, drafts, allOrders, next, call).ConfigureAwait(false);
        p.Journal.RecordCombatStop(id, batch, sent, next3, call);
        recorded.Evidence = true;
        RecordCombatStop(combat, recorded, call);
        return new RoundsDefenseResult { Verdict = BuildingReasonCombatOrders, Plan = id };
    }

    /// <summary>The verdict of a combat read with nothing to fight.</summary>
    private static readonly Verdict NoFight = WaitFor(Cause.MethodUsed, "no_fight");

    /// <summary>
    /// Ends an open hunt fight that can no longer hunt: its doors are restored and it closes, so the undraft sweep
    /// undrafts its roster. The review's hunt standing is untouched; its own release triggers re-compare the hunt.
    /// </summary>
    private async Task ReleaseHuntAsync(ControlState state, NeedOwner owner, PlanId plan, string reason, CancellationToken call)
    {
        await ClearFightDoorsAsync(state, plan, call).ConfigureAwait(false);
        _reviewer.Player.Journal.CloseCombatFight(plan, call);
        Tel.Decide(new Decision
        {
            Kind = "admission",
            Component = "routine-defense",
            Verdict = "released",
            Reason = reason,
            Target = owner.Need.Value,
            Attrs = new Dictionary<string, object?> { ["combat_plan"] = plan },
        });
    }

    /// <summary>The roster pawns whose observation row says they are drafted, ordinal sorted.</summary>
    internal static List<PawnId> FightOrderable(CombatFight fight, IReadOnlyDictionary<string, O.PawnState> rows)
    {
        var orderable = new List<PawnId>();
        foreach (var pawn in fight.Roster)
        {
            if (rows.TryGetValue(pawn.Value, out var row) && row.Drafted)
            {
                orderable.Add((PawnId)pawn);
            }
        }
        orderable.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return orderable;
    }

    /// <summary>Cancels the pending and prepared work of the combat Need's methods, other than the post-fight strip, once combat clears.</summary>
    internal void SettleUnissuedWork(NeedOwner owner, CancellationToken call)
    {
        var p = _reviewer.Player;
        foreach (var method in owner.Methods)
        {
            if (method.Method.Value.StartsWith(StripMethodPrefix, StringComparison.Ordinal))
            {
                continue; // the post-fight strip waits for its own dispatch
            }
            var plan = p.Journal.LoadPlan(method.Plan, call);
            foreach (var progress in plan.Progress)
            {
                var v = progress.View;
                if (v.Stage != Stage.Pending && v.Stage != Stage.Prepared)
                {
                    continue;
                }
                p.Journal.Cancel(method.Plan, v.Action, call);
            }
        }
    }

    /// <summary>The method id of a defense plan named by its content: the prefix and the first 16 bytes of the content's hash.</summary>
    internal static MethodId DefenseMethodId(string prefix, StringBuilder hash)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(hash.ToString()));
        return new MethodId($"{prefix}-{Convert.ToHexStringLower(sum.AsSpan(0, 16))}");
    }
}

/// <summary>What one combat read yields for the planners: the hostile ids, the buildings that threaten and the observation rows.</summary>
internal sealed class CombatInputs
{
    public List<string> HostileIds = [];
    public HashSet<string> Hunting = new(StringComparer.Ordinal);
    public List<EmergencyThreat> Buildings = [];
    public Dictionary<string, O.PawnState> Rows = new(StringComparer.Ordinal);
    public List<string> Prey = [];

    /// <summary>The gunners of the chosen hunt roster: the only pawns a hunt may send.</summary>
    public List<string> Hunters = [];
    public HashSet<PawnId> Needed = [];

    /// <summary>The player's preferences: protected pawns are held back from the squad while another defender is eligible.</summary>
    public PreferencesRecord Preferences = new();
    public Dictionary<string, WeaponDef> Weapons = new(StringComparer.Ordinal);
    public Dictionary<string, bool> Ranged = new(StringComparer.Ordinal);
    public Dictionary<string, double> Reach = new(StringComparer.Ordinal);

    /// <summary>Each pawn's primary weapon def name from its equipment row: what a pawn the combat read does not list (no hostile stands in a hunt) is armed with.</summary>
    public Dictionary<string, string> Primary = new(StringComparer.Ordinal);
    public Dictionary<PawnId, PawnProfile> Profiles = [];

    internal void ResolveProfiles(IReadOnlyList<EmergencyPawn> colonists, Armament arms)
    {
        Profiles = [];
        foreach (var pawn in colonists)
        {
            var work = RoundsWorkReads.WorkPawnRow(Rows[pawn.Id.Value], arms.Catalog, arms.Things);
            Profiles[pawn.Id] = PawnRules.BuildProfile(work);
        }
    }
}

partial class RoundsDefensePlanner
{
    /// <summary>
    /// The layout combat fights from: the direction toward home from the plan's killbox opening, and the firing line,
    /// its retreat cells and the choke (the trap lane's last cell) from the plan's DefFiring and DefLane nodes (#3583).
    /// None while the plan has no opening; a plan without a trap lane has no corridor.
    /// </summary>
    internal static CombatLayout? CombatLayoutOf(LayoutPlan plan)
    {
        var opening = LayoutPlan.KillboxOpening(plan);
        if (!opening.Ok)
        {
            return null;
        }
        var tiers = plan.DefenseTierGeometry();
        return new CombatLayout
        {
            Firing = [.. tiers.Firing.Select(f => f.Cell)],
            Retreat = [.. tiers.Firing.Select(f => f.Retreat)],
            Toward = opening.Killbox.Toward,
            Choke = tiers.TrapLane.Count > 0 ? new CombatChoke.Corridor(tiers.TrapLane[^1]) : new CombatChoke.NoCorridor(),
        };
    }

    /// <summary>The inputs of the combat step; a null inputs value carries the wait verdict when the census is incomplete or there is no fight.</summary>
    internal static (CombatInputs? Inputs, Verdict Reason) CombatFrameInputs(BridgeCombat combat, IReadOnlyList<Domain.PawnId> huntPrey, IReadOnlyList<Domain.PawnId> huntHunters)
    {
        var facts = combat.Emergency.Facts;
        var catalog = combat.Catalog ?? throw new ControlException("combatFrameInputs: no definition catalog");
        if (!facts.AllSeen)
        {
            return (null, WaitFor(Cause.MethodUsed, "colonist_census_incomplete"));
        }
        var inp = new CombatInputs { Hunters = [.. huntHunters.Select(h => h.Value)] };
        (inp.HostileIds, inp.Hunting, inp.Buildings) = DefenseTargets(facts.Threats);
        inp.Buildings = [.. inp.Buildings.Select(building => building with { Mortar = catalog.IsMortar(building.Definition) })];
        if (inp.HostileIds.Count + inp.Buildings.Count == 0 && !RoundsDefensePlanner.HasAggressiveBreak(facts) && facts.PodsOpen.Value == 0)
        {
            foreach (var id in huntPrey)
            {
                if (combat.Detail.TryGetValue(id.Value, out var row) && row is not null && !row.Dead)
                {
                    inp.Prey.Add(id.Value);
                }
            }
        }
        if (facts.Colonists.Count == 0 || (inp.HostileIds.Count + inp.Buildings.Count + inp.Prey.Count == 0 && !RoundsDefensePlanner.HasAggressiveBreak(facts) && facts.PodsOpen.Value == 0))
        {
            return (null, WaitFor(Cause.MethodUsed, "no_fight"));
        }
        foreach (var pawn in facts.Colonists)
        {
            combat.Detail.TryGetValue(pawn.Id.Value, out var row);
            inp.Rows[pawn.Id.Value] = row!;
        }
        foreach (var id in inp.HostileIds.Concat(inp.Prey).ToList())
        {
            combat.Detail.TryGetValue(id, out var row);
            inp.Rows[id] = row!;
        }
        foreach (var row in inp.Rows.Values)
        {
            if (row is null)
            {
                throw new ControlException("combatFrameInputs: row == nil");
            }
        }
        var arms = new Armament { Catalog = catalog, Things = combat.Things };
        inp.ResolveProfiles(facts.Colonists, arms);
        foreach (var (id, row) in inp.Rows)
        {
            var weapon = arms.Primary(row.Equipment);
            inp.Primary[id] = arms.PrimaryDefName(row.Equipment);
            inp.Ranged[id] = weapon.Ranged;
            if (weapon.Ranged)
            {
                inp.Reach[id] = weapon.Range;
            }
        }
        foreach (var pawn in combat.Pawns)
        {
            var def = (pawn.Weapon ?? "");
            if (def == "" || inp.Weapons.ContainsKey(def))
            {
                continue;
            }
            inp.Weapons[def] = catalog.WeaponOf(def);
        }
        foreach (var def in inp.Primary.Values)
        {
            if (def.Length > 0 && !inp.Weapons.ContainsKey(def))
            {
                inp.Weapons[def] = catalog.WeaponOf(def);
            }
        }
        return (inp, default);
    }

    /// <summary>The policy view of the fight: the defenders, the threats, the structures and the combat read's rooms, doors and mortars.</summary>
    internal static CombatView CombatView(BridgeCombat combat, CombatInputs inp, IReadOnlyList<PawnId> orderable, CombatLayout? layout)
    {
        var context = combat.Context ?? throw new ObservationContractException("combat frame without its observation context");
        var identity = context.Identity ?? throw new ObservationContractException("combat frame without its map identity");
        var catalog = combat.Catalog ?? throw new ControlException("combatView: no definition catalog");
        var needed = inp.Needed.Select(n => (Domain.PawnId)n).ToHashSet();
        var owned = new HashSet<PawnId>(orderable);
        var armor = new Dictionary<PawnId, double>();
        foreach (var row in combat.Pawns)
        {
            armor[row.Id] = row.Armor;
        }
        var arms = new Armament { Catalog = catalog, Things = combat.Things };
        var defenders = new List<SquadDefenderFacts>();
        var profiles = new List<PawnProfile>();
        foreach (var pawn in combat.Emergency.Facts.Colonists)
        {
            var row = inp.Rows[pawn.Id.Value];
            // The combat mirror lists every spawned colonist while a hostile stands; a hunt or a break alone has no rows.
            DefenderArmor rating = armor.TryGetValue(pawn.Id, out var a) ? new DefenderArmor.Rated(a) : new DefenderArmor.Unmirrored();
            var d = Armament.SquadDefenderFacts(row, needed, arms, rating);
            if (owned.Contains(d.Id))
            {
                d = d with { DraftOwned = false };
            }
            defenders.Add(d);
            // The warden capability is assigned to a local copy after the append, so it never reaches the defender row.
            var profile = inp.Profiles[pawn.Id];
            profiles.Add(profile);
        }
        var (front, _) = PawnRules.FrontLine(profiles);
        var holds = new HashSet<PawnId>(front);
        var heldBack = PreferenceRules.HeldBackFromDraft(inp.Preferences, defenders);
        // A protected pawn already in the fight stays in it; the hold-back is for who is drafted next.
        defenders = [.. defenders.Where(d => !heldBack.Contains(d.Id) || owned.Contains(d.Id)).Select(d => d with { FrontLine = holds.Contains(d.Id) })];
        var threats = new List<SquadThreatFacts>();
        var positional = new List<DefensiveThreatFacts>(inp.HostileIds.Count);
        foreach (var id in inp.HostileIds)
        {
            var row = inp.Rows[id];
            threats.Add(Armament.SquadThreatFacts(row, catalog, inp.Ranged[id]) with { Hunting = inp.Hunting.Contains(id) });
            if (DefensiveThreatFacts(row, catalog) is { } placed)
            {
                positional.Add(placed);
            }
        }
        foreach (var id in inp.Prey)
        {
            threats.Add(Armament.SquadThreatFacts(inp.Rows[id], catalog, inp.Ranged[id]));
        }
        var lines = BuildingLinesOfFire(combat.Lines, inp.Buildings, defenders, inp.Rows, inp.Reach);
        foreach (var building in inp.Buildings)
        {
            lines.TryGetValue(building.Id, out var linesOfFire);
            threats.Add(new SquadThreatFacts { Id = building.Id, Dead = building.Dead, Building = true, Anomaly = new O.PawnAnomalyState.Inactive(), LinesOfFire = linesOfFire?.ToDictionary(shooter => shooter, _ => true) ?? new Dictionary<PawnId, bool>() });
        }
        var structures = new List<HostileStructure>();
        foreach (var b in inp.Buildings)
        {
            if (!b.Dead && b.Cells.Count > 0)
            {
                structures.Add(new HostileStructure { Id = b.Id, Def = b.Definition, Cell = b.Cells[0], Mortar = b.Mortar });
            }
        }
        var damaged = new List<Cell>();
        foreach (var door in combat.Doors)
        {
            damaged.Add(door.Cell);
        }
        return new CombatView
        {
            SiteExit = QuestReads.CombatSiteExitOf(combat.World, new MapId(identity.MapId)),
            Hunt = inp.Prey.Count > 0,
            HuntHunters = [.. inp.Hunters.Select(h => new PawnId(h))],
            Tick = new Tick(context.Tick),
            Pawns = [.. PreyStates(CombatPawnStates(combat, inp.Rows, inp.Weapons), inp, catalog)],
            Defenders = [.. defenders],
            Threats = [.. threats],
            Positional = [.. positional],
            Orderable = [.. orderable],
            Layout = layout,
            Pods = PodArrival(combat),
            Rooms = [.. combat.Rooms],
            DoorStates = combat.DoorStates,
            DamagedDoors = [.. damaged],
            Mortars = [.. combat.Mortars],
            Shells = combat.Shells,
            Structures = [.. structures],
            OutdoorTemperature = combat.OutdoorTemperature,
            Population = combat.Emergency.Facts.Colonists.Count,
        };
    }

    /// <summary>The combat pawn states plus the hunted prey the combat read did not list.</summary>
    internal static List<CombatPawnState> PreyStates(List<CombatPawnState> states, CombatInputs inp, DefinitionCatalog catalog)
    {
        var listed = new HashSet<PawnId>(states.Select(s => s.Id));
        // A hunt has no hostile, so the combat read lists no pawns and the states come from the observation rows: arm them from their equipment.
        for (var i = 0; i < states.Count; i++)
        {
            if (states[i].Weapon is null && inp.Primary.TryGetValue(states[i].Id.Value, out var def) && def.Length > 0 && inp.Weapons.TryGetValue(def, out var facts))
            {
                states[i] = states[i] with { Weapon = def, WeaponFacts = facts };
            }
        }

        foreach (var id in inp.Prey)
        {
            var row = inp.Rows[id];
            if (listed.Contains(new PawnId(id)))
            {
                continue;
            }
            if (ObservedPawnState(row, wild: true, catalog) is { } s)
            {
                states.Add(s);
            }
        }
        return states;
    }

    /// <summary>The newest drop pod arrival of the combat events, with its open tick and landing cells; none without one.</summary>
    internal static PodArrival? PodArrival(BridgeCombat combat)
    {
        M.CombatEventRow? newest = null;
        foreach (var row in combat.Events)
        {
            if (CombatCalls.DropPodArrival(row) && (newest is null || RoundsFrameRules.CombatBefore(newest.At, row.At)))
            {
                newest = row;
            }
        }
        if (newest?.Pods is not { } pods)
        {
            return null;
        }
        var landing = new List<Cell>();
        foreach (var c in pods.LandingCells)
        {
            landing.Add(new Cell(c.X, c.Z));
        }
        return new PodArrival { Open = new Tick(pods.OpenTick), Landing = [.. landing] };
    }

    /// <summary>The shooters with a line of fire to each threatening building: for every known line within the weapon reach, the shooters standing at its origin.</summary>
    internal static Dictionary<PawnId, HashSet<PawnId>> BuildingLinesOfFire(IReadOnlyList<O.LineOfFire> read, IReadOnlyList<EmergencyThreat> buildings, IReadOnlyList<SquadDefenderFacts> defenders, IReadOnlyDictionary<string, O.PawnState> rows, IReadOnlyDictionary<string, double> reaches)
    {
        var lines = new Dictionary<PawnId, HashSet<PawnId>>();
        var shooters = new Dictionary<Cell, List<PawnId>>();
        var weaponRange = new Dictionary<PawnId, double>();
        foreach (var d in defenders)
        {
            rows.TryGetValue(d.Id.Value, out var row);
            if (!d.RangedEquipped || row is null || row.Pawn.Place is not O.EntityPlace.OnMap { Cell: var position })
            {
                continue;
            }
            reaches.TryGetValue(d.Id.Value, out var reach);
            if (reach <= 0)
            {
                continue;
            }
            var at = new Cell(position.X, position.Z);
            if (!shooters.TryGetValue(at, out var list))
            {
                shooters[at] = list = [];
            }
            list.Add(d.Id);
            weaponRange[d.Id] = reach;
        }
        foreach (var line in read)
        {
            if (line is not O.LineOfFire.Traced { LineOfSight: true })
            {
                continue;
            }
            foreach (var b in buildings)
            {
                foreach (var cell in b.Cells)
                {
                    if (cell != line.To || !shooters.TryGetValue(line.From, out var ids))
                    {
                        continue;
                    }
                    foreach (var id in ids)
                    {
                        if (line.Distance <= weaponRange[id])
                        {
                            if (!lines.TryGetValue(b.Id, out var set))
                            {
                                lines[b.Id] = set = [];
                            }
                            set.Add(id);
                        }
                    }
                }
            }
        }
        return lines;
    }

    /// <summary>The positional facts of one hostile's pawn-table row; null when the hostile is off the map, which leaves it out of the positional threats.</summary>
    internal static DefensiveThreatFacts? DefensiveThreatFacts(O.PawnState row, DefinitionCatalog catalog)
    {
        if (row.Pawn.Place is not O.EntityPlace.OnMap { Cell: var position })
        {
            return null;
        }
        return new DefensiveThreatFacts
        {
            Id = new PawnId(row.Pawn.Id),
            Dead = row.Dead,
            Downed = row.Downed,
            Humanlike = row.Humanlike,
            LordJobClass = row.LordJobClass,
            LordToilClass = row.LordToilClass,
            NearestColonist = row.NearestColonist,
            Position = new Cell(position.X, position.Z),
            Mech = catalog.RaceFlags(row.Pawn.DefName ?? "").Mechanoid,
        };
    }

    /// <summary>The unfinished draft-dependent actions: movement and subdue actions whose draft or other requirement is cancelled or unsuccessful.</summary>
    internal static List<ActionId> OrphanedDraftDependents(PlanSpec spec, IReadOnlyList<Progress> progress)
    {
        var views = new Dictionary<ActionId, ProgressView>();
        foreach (var p in progress)
        {
            views[p.View.Action] = p.View;
        }
        bool Dead(ActionId id) => views.TryGetValue(id, out var v) && (v.Stage == Stage.Unsuccessful || v.Stage == Stage.Cancelled);
        var requires = new Dictionary<ActionId, List<ActionId>>();
        foreach (var d in spec.Dependencies)
        {
            if (!requires.TryGetValue(d.Action, out var list))
            {
                requires[d.Action] = list = [];
            }
            list.Add(d.Requires);
        }
        var output = new List<ActionId>();
        foreach (var action in spec.Actions)
        {
            if (!views.TryGetValue(action.Id, out var v) || (v.Stage != Stage.Pending && v.Stage != Stage.Prepared))
            {
                continue;
            }
            ActionId draft;
            if (action.Movement is { } m)
            {
                draft = m.DraftAction;
            }
            else if (action.Subdue is { } a)
            {
                draft = a.DraftAction;
            }
            else
            {
                continue;
            }
            var orphan = Dead(draft);
            if (requires.TryGetValue(action.Id, out var reqs))
            {
                foreach (var req in reqs)
                {
                    orphan = orphan || Dead(req);
                }
            }
            if (orphan)
            {
                output.Add(action.Id);
            }
        }
        return output;
    }

    /// <summary>Splits the engaging threats into hostile and hunting predator ids and the living threatening buildings.</summary>
    internal static (List<string> Ids, HashSet<string> Hunting, List<EmergencyThreat> Buildings) DefenseTargets(IReadOnlyList<EmergencyThreat> threats)
    {
        var ids = new List<string>();
        var buildings = new List<EmergencyThreat>();
        var hunting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var threat in threats)
        {
            if (!threat.Engaging)
            {
                continue;
            }
            if (threat.Building)
            {
                if (!threat.Dead)
                {
                    buildings.Add(threat);
                }
            }
            else if (threat.Kind == ThreatKind.Hostile)
            {
                ids.Add(threat.Id.Value);
            }
            else if (threat.Kind == ThreatKind.HuntingPredator && !threat.DistantThreat)
            {
                ids.Add(threat.Id.Value);
                hunting.Add(threat.Id.Value);
            }
        }
        return (ids, hunting, buildings);
    }
}

internal sealed partial class Armament
{
    /// <summary>The primary weapon's range when it is a ranged weapon; zero otherwise.</summary>
    internal double PrimaryRange(O.PawnEquipment equipment)
    {
        var weapon = Primary(equipment);
        return weapon.Ranged ? weapon.Range : 0;
    }
}
