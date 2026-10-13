using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads of the defense planner: the geometry question, the combat read and the burn fuel read.</summary>
public interface IRoundsDefenseSource : IBurnFuelSource
{
    (M.CombatGeometry Geometry, BridgeResult Result) CombatGeometry(M.CombatGeometryRequest request, CancellationToken ct);

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
        var (incident, need, found) = RoundsIncident(p.Journal, review, PolicyApi.ActiveCombat, call);
        if (found)
        {
            var (subdued, handled) = await ReconcileSubdueFightAsync(incident!, state, call, epoch).ConfigureAwait(false);
            if (handled)
            {
                return subdued;
            }
        }
        if (found && need == Situation.Clear)
        {
            SettleUnissuedWork(incident!, call);
            var (post, held0) = await PostFightAsync(incident!, state, review.Tick, arbiter, call, epoch).ConfigureAwait(false);
            if (held0)
            {
                return post;
            }
            foreach (var method in incident!.Methods)
            {
                if (method.Method.Value.StartsWith(CombatMethodPrefix, StringComparison.Ordinal))
                {
                    await ClearFightDoorsAsync(state, method.Plan, call).ConfigureAwait(false);
                    p.Journal.CloseCombatFight(method.Plan, call);
                }
            }
            return new RoundsDefenseResult { Verdict = BuildingReasonNoDeficit };
        }
        if (!found || need != Situation.Active || review.VetoIncident(incident!.Incident) != "")
        {
            return new RoundsDefenseResult { Verdict = BuildingReasonNoDeficit };
        }
        CombatFight? fight = null;
        PlanId fightPlan = default;
        foreach (var method in incident.Methods)
        {
            if (method.Method.Value.StartsWith(CombatMethodPrefix, StringComparison.Ordinal))
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
        if (combat.Context is null || combat.Emergency.Context is null || combat.Context.Tick.GetValueOrDefault() < review.Tick.Value)
        {
            throw new ControlException("decide: err != nil || combat.Emergency.Context == nil || combat.Context.GetTick() < int64(review.Tick)");
        }
        var (inputs, reason) = CombatFrameInputs(combat, [.. incident.Incident.HuntPrey]);
        if (inputs is null || !reason.IsZero)
        {
            return new RoundsDefenseResult { Verdict = reason };
        }
        var inp = inputs;
        inp.Needed = await DraftNeeds.PlannedDraftsAsync(p.Journal, call).ConfigureAwait(false);
        var emergency = combat.Emergency;
        var rows = inp.Rows;
        var broke = await PlanBreakAsync(incident, state, started, arbiter, new Tick(combat.Context?.Tick ?? 0), emergency.Facts, rows, call, epoch).ConfigureAwait(false);
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
        var held = Unknown<CombatLayout>();
        if (layout is { Complete: true })
        {
            if (p.Journal.LayoutPlan(state.Snapshot, new Tick(combat.Context?.Tick ?? 0), call) is { Plan: { } layoutPlan })
            {
                var opening = PolicyApi.KillboxOpening(layoutPlan);
                if (opening.Ok)
                {
                    var combatLayout = new CombatLayout { Firing = [.. layout.Firing], Retreat = [.. layout.Retreat], Toward = opening.Killbox.Toward };
                    if (layout.TrapLane.Count > 0)
                    {
                        combatLayout = combatLayout with { Choke = Known(layout.TrapLane[^1]) };
                    }
                    held = Known(combatLayout);
                }
            }
        }
        var items = await _reviewer.ItemFactsAsync(state.Snapshot, call).ConfigureAwait(false);
        var burn = await CombatBurnSiteAsync(_native, identity, memory, combat.Context?.Tick ?? 0, items, call).ConfigureAwait(false);
        var drug = await _reviewer.CombatDrugsAsync(state.Snapshot, call).ConfigureAwait(false);
        var view = CombatView(combat, inp, orderable, held);
        view = view with { Burn = burn };
        view = view with { Royalty = _reviewer.Census.Remembered() };
        view = view with { Drugs = [.. drug] };
        var tick = view.Tick;
        var stop = CombatStop(combat, memory.Tick);
        await ReconcileCombatBatchesAsync(state, incident, view, call).ConfigureAwait(false);
        var (orders, ask, next) = PolicyApi.DecideCombat(view, new GeometryReply(), stop, memory);
        var recorded = new SnapshotCombatStop { Tick = tick, Stop = stop, Orderable = [.. orderable.Select(o => (Domain.PawnId)o)], Ask = ask, MemoryIn = memory };
        if (held.TryGetValue(out var layoutKnown))
        {
            recorded.Layout = layoutKnown;
        }
        if (ask is not null)
        {
            recorded.Reply = AnswerGeometry(Boundary.Identity(state.Snapshot), ask, call);
            (orders, _, next) = PolicyApi.DecideCombat(view, recorded.Reply, stop, memory);
        }
        recorded.MemoryOut = next;
        var reconsider = PolicyApi.ShelterReconsider(view, memory);
        if (reconsider != "")
        {
            Tel.Decide(new Decision
            {
                Kind = "admission",
                Component = "routine-defense",
                Verdict = "held",
                Reason = reconsider,
                Target = incident.Incident.Id.Value,
                Attrs = new Dictionary<string, object?> { ["combat_plan"] = fightPlan, ["tactic"] = next.Tactic },
            });
        }
        if (next.Formed == tick && next.Refusal != "" && next.Tactic != CombatTactic.Hold)
        {
            DefenseAction("routine-defense", TelemetryLevel.Info, "refused", "hold_refused", incident.Incident.Id.Value, new Dictionary<string, object?> { ["incident"] = incident.Incident.Id.Value, ["gate"] = next.Refusal }, call);
        }
        if (fight is null)
        {
            var result = await AdmitFightAsync(incident, state, started, arbiter, new FightAdmission { Combat = combat, In = inp, Held = held, Reply = recorded.Reply ?? new GeometryReply(), Stop = stop, Memory = next }, call, epoch).ConfigureAwait(false);
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
            view = view with { Burn = burn };
            view = view with { Royalty = _reviewer.Census.Remembered() };
            view = view with { Drugs = [.. drug] };
            var (o2, more, n2) = PolicyApi.DecideCombat(view, recorded.Reply ?? new GeometryReply(), stop, memory);
            orders = o2;
            next = n2;
            if (more is not null && recorded.Ask is null)
            {
                recorded.Ask = more;
                recorded.Reply = AnswerGeometry(Boundary.Identity(state.Snapshot), more, call);
                (orders, more, next) = PolicyApi.DecideCombat(view, recorded.Reply, stop, memory);
            }
            if (more is not null)
            {
                orders = [];
                next = memory;
            }
            recorded.MemoryOut = next;
        }
        var (rest, permits) = SplitPermitCalls(orders);
        CommitPermitCalls(incident, permits, call, epoch);
        var guards = CombatMechGuards(combat, inp.HostileIds, inp.Rows);
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
        var (stopRecord, next3) = await IssueCombatOrdersAsync(state, id, view, drafts, allOrders, next, call).ConfigureAwait(false);
        stopRecord = stopRecord with { Stop = stop };
        p.Journal.RecordCombatStop(id, stopRecord, next3, call);
        recorded.Evidence = true;
        RecordCombatStop(combat, recorded, call);
        return new RoundsDefenseResult { Verdict = BuildingReasonCombatOrders, Plan = id };
    }

    /// <summary>The roster pawns whose observation row says they are drafted, ordinal sorted.</summary>
    internal static List<PawnId> FightOrderable(CombatFight fight, IReadOnlyDictionary<string, O.PawnState> rows)
    {
        var orderable = new List<PawnId>();
        foreach (var pawn in fight.Roster)
        {
            if (rows.TryGetValue(pawn.Value, out var row) && row is not null && Boundary.FactBool(row.Drafted is not null, row.Drafted.GetValueOrDefault()) == Known(true))
            {
                orderable.Add((PawnId)pawn);
            }
        }
        orderable.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return orderable;
    }

    /// <summary>Cancels the pending and prepared work of the incident's methods, other than the post-fight strip, before the incident closes.</summary>
    internal void SettleUnissuedWork(IncidentState incident, CancellationToken call)
    {
        var p = _reviewer.Player;
        foreach (var method in incident.Methods)
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

    /// <summary>The method id of a defense plan: the prefix and the first 16 bytes of the hash of the plan's content and the count of admitted methods.</summary>
    internal static MethodId DefenseMethodId(string prefix, int admitted, StringBuilder hash)
    {
        hash.Append(CultureInfo.InvariantCulture, $"#{admitted}\n");
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
    public HashSet<PawnId> Needed = [];
    public Dictionary<string, WeaponDef> Weapons = new(StringComparer.Ordinal);
    public Dictionary<string, Fact<bool>> Ranged = new(StringComparer.Ordinal);
    public Dictionary<string, double> Reach = new(StringComparer.Ordinal);
    public Dictionary<PawnId, PawnProfile> Profiles = [];

    internal void ResolveProfiles(IReadOnlyList<EmergencyPawn> colonists, Armament arms)
    {
        Profiles = [];
        foreach (var pawn in colonists)
        {
            var work = ObservationApi.WorkPawnRow(Rows[pawn.Id.Value], arms.Catalog, arms.Things);
            Profiles[new PawnId(pawn.Id.Value)] = PolicyApi.BuildProfile(work);
        }
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The inputs of the combat step; a null inputs value carries the wait verdict when the census is incomplete or there is no fight.</summary>
    internal static (CombatInputs? Inputs, Verdict Reason) CombatFrameInputs(BridgeCombat combat, IReadOnlyList<Domain.PawnId> huntPrey)
    {
        var facts = combat.Emergency.Facts;
        var catalog = combat.Catalog ?? throw new ControlException("combatFrameInputs: no definition catalog");
        if (!facts.ColonistsComplete.TryGetValue(out var colonistsComplete) || !colonistsComplete)
        {
            return (null, WaitFor(Cause.MethodUsed, "colonist_census_incomplete"));
        }
        var inp = new CombatInputs();
        (inp.HostileIds, inp.Hunting, inp.Buildings) = DefenseTargets(facts.Threats);
        inp.Buildings = [.. inp.Buildings.Select(building => building with { Mortar = catalog.IsMortar(building.Definition) })];
        if (inp.HostileIds.Count + inp.Buildings.Count == 0 && !RoundsDefensePlanner.HasAggressiveBreak(facts) && facts.PodsOpen.Value == 0)
        {
            foreach (var id in huntPrey)
            {
                if (combat.Detail.TryGetValue(id.Value, out var row) && row is not null && Boundary.FactBool(row.Dead is not null, row.Dead.GetValueOrDefault()) != Known(true))
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
            var (weapon, known) = arms.Primary(row.Equipment);
            if (!known)
            {
                continue;
            }
            inp.Ranged[id] = Known(weapon.Ranged);
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
        return (inp, default);
    }

    /// <summary>The policy view of the fight: the defenders, the threats, the structures and the combat read's rooms, doors and mortars.</summary>
    internal static CombatView CombatView(BridgeCombat combat, CombatInputs inp, IReadOnlyList<PawnId> orderable, Fact<CombatLayout> layout)
    {
        var catalog = combat.Catalog ?? throw new ControlException("combatView: no definition catalog");
        var needed = inp.Needed.Select(n => (Domain.PawnId)n).ToHashSet();
        var owned = new HashSet<PawnId>(orderable);
        var armor = new Dictionary<PawnId, double>();
        var melee = new Dictionary<PawnId, Fact<double>>();
        foreach (var row in combat.Pawns)
        {
            if (row.Armor is not null)
            {
                armor[new PawnId((row.Id ?? ""))] = row.Armor.GetValueOrDefault();
            }
            if (row.MeleePower is not null && row.Health is not null)
            {
                melee[new PawnId((row.Id ?? ""))] = Known(row.MeleePower.GetValueOrDefault() * row.Health.GetValueOrDefault());
            }
        }
        var defenders = new List<SquadDefenderFacts>();
        var profiles = new List<PawnProfile>();
        foreach (var pawn in combat.Emergency.Facts.Colonists)
        {
            var row = inp.Rows[pawn.Id.Value];
            inp.Ranged.TryGetValue(pawn.Id.Value, out var ranged);
            var d = SquadDefenderFacts(row, needed, ranged);
            if (armor.TryGetValue(d.Id, out var a))
            {
                d = d with { Armor = Known(a) };
            }
            d = d with { MeleePower = melee.TryGetValue(d.Id, out var mp) ? mp : default };
            if (owned.Contains(d.Id))
            {
                d = d with { DraftOwned = Known(false) };
            }
            defenders.Add(d);
            // Go assigns the warden capability to its local copy after the append, so it never reaches the defender row; ported as is.
            var profile = inp.Profiles[new PawnId(pawn.Id.Value)];
            profiles.Add(profile);
        }
        var (front, _) = PolicyApi.FrontLine(profiles);
        var holds = new HashSet<PawnId>(front);
        defenders = [.. defenders.Select(d => d with { FrontLine = holds.Contains(d.Id) })];
        var threats = new List<SquadThreatFacts>();
        var races = catalog.AnimalRaces();
        var positional = new List<DefensiveThreatFacts>(inp.HostileIds.Count);
        foreach (var id in inp.HostileIds)
        {
            var row = inp.Rows[id];
            inp.Ranged.TryGetValue(id, out var ranged);
            var facts = SquadThreatFacts(row, races, ranged);
            facts = facts with { Hunting = Known(inp.Hunting.Contains(id)) };
            facts = facts with { MeleePower = melee.TryGetValue(new PawnId(id), out var mp) ? mp : default };
            threats.Add(facts);
            positional.Add(DefensiveThreatFacts(row));
        }
        foreach (var id in inp.Prey)
        {
            inp.Ranged.TryGetValue(id, out var ranged);
            threats.Add(SquadThreatFacts(inp.Rows[id], races, ranged));
        }
        var lines = BuildingLinesOfFire(combat.Lines, inp.Buildings, defenders, inp.Rows, inp.Reach);
        foreach (var building in inp.Buildings)
        {
            lines.TryGetValue(building.Id, out var linesOfFire);
            threats.Add(new SquadThreatFacts { Id = building.Id, Dead = building.Dead, Building = true, LinesOfFire = linesOfFire?.ToDictionary(shooter => shooter, _ => true) ?? new Dictionary<PawnId, bool>() });
        }
        var structures = new List<HostileStructure>();
        foreach (var b in inp.Buildings)
        {
            if (!(b.Dead.TryGetValue(out var dead) && dead) && b.Cells.Count > 0)
            {
                structures.Add(new HostileStructure { Id = new PawnId(b.Id.Value), Def = b.Definition, Cell = b.Cells[0], Mortar = b.Mortar });
            }
        }
        var damaged = new List<Cell>();
        foreach (var door in combat.Doors)
        {
            damaged.Add(new Cell(door.Cell?.X ?? 0, door.Cell?.Z ?? 0));
        }
        return new CombatView
        {
            SiteExit = ObservationApi.CombatSiteExitOf(combat.World, new MapId(combat.Context?.Identity?.MapId ?? 0)),
            Hunt = inp.Prey.Count > 0,
            Tick = new Tick(combat.Context?.Tick ?? 0),
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
            OutdoorTemperatureC = combat.OutdoorTemperatureC,
            HiveTemperatureC = combat.HiveTemperatureC,
            Population = Known(combat.Emergency.Facts.Colonists.Count),
        };
    }

    /// <summary>The combat pawn states plus the hunted prey the combat read did not list.</summary>
    internal static List<CombatPawnState> PreyStates(List<CombatPawnState> states, CombatInputs inp, DefinitionCatalog catalog)
    {
        var listed = new HashSet<PawnId>(states.Select(s => s.Id));
        foreach (var id in inp.Prey)
        {
            var row = inp.Rows[id];
            if (listed.Contains(new PawnId(id)))
            {
                continue;
            }
            var s = new CombatPawnState { Id = new PawnId(id), Downed = row.Downed.GetValueOrDefault(), Dead = row.Dead.GetValueOrDefault(), Wild = true };
            if (row.Pawn?.Position is { X: not null, Z: not null } position)
            {
                s = s with { Cell = Known(new Cell(position.X.GetValueOrDefault(), position.Z.GetValueOrDefault())) };
            }
            states.Add(ThreatFacts(s, row, catalog));
        }
        return states;
    }

    /// <summary>The newest drop pod arrival of the combat events, with its open tick and landing cells; unknown without one.</summary>
    internal static Fact<PodArrival> PodArrival(BridgeCombat combat)
    {
        M.CombatEventRow? newest = null;
        foreach (var row in combat.Events)
        {
            if (BridgeApi.DropPodArrival(row) && (newest is null || BridgeApi.CombatBefore(newest.At, row.At)))
            {
                newest = row;
            }
        }
        if (newest is null)
        {
            return Unknown<PodArrival>();
        }
        var landing = new List<Cell>();
        foreach (var c in newest.LandingCells)
        {
            landing.Add(new Cell(c.X.GetValueOrDefault(), c.Z.GetValueOrDefault()));
        }
        return Known(new PodArrival { Open = new Tick(newest.OpenTick.GetValueOrDefault()), Landing = [.. landing] });
    }

    /// <summary>The shooters with a line of fire to each threatening building: for every known line within the weapon reach, the shooters standing at its origin.</summary>
    internal static Dictionary<PawnId, HashSet<PawnId>> BuildingLinesOfFire(IReadOnlyList<LineOfFire> read, IReadOnlyList<EmergencyThreat> buildings, IReadOnlyList<SquadDefenderFacts> defenders, IReadOnlyDictionary<string, O.PawnState> rows, IReadOnlyDictionary<string, double> reaches)
    {
        var lines = new Dictionary<PawnId, HashSet<PawnId>>();
        var shooters = new Dictionary<Cell, List<PawnId>>();
        var weaponRange = new Dictionary<PawnId, double>();
        foreach (var d in defenders)
        {
            rows.TryGetValue(d.Id.Value, out var row);
            if (!d.RangedEquipped.TryGetValue(out var ranged) || !ranged || row is null || row.Pawn?.Position is null)
            {
                continue;
            }
            reaches.TryGetValue(d.Id.Value, out var reach);
            if (reach <= 0)
            {
                continue;
            }
            var at = new Cell(row.Pawn.Position.X.GetValueOrDefault(), row.Pawn.Position.Z.GetValueOrDefault());
            if (!shooters.TryGetValue(at, out var list))
            {
                shooters[at] = list = [];
            }
            list.Add(d.Id);
            weaponRange[d.Id] = reach;
        }
        foreach (var line in read)
        {
            if (!line.Known || !line.LineOfSight)
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

    internal static DefensiveThreatFacts DefensiveThreatFacts(O.PawnState row)
    {
        var facts = new DefensiveThreatFacts
        {
            Id = new PawnId(row.Pawn?.Id ?? ""),
            Dead = Boundary.FactBool(row.Dead is not null, row.Dead.GetValueOrDefault()),
            Downed = Boundary.FactBool(row.Downed is not null, row.Downed.GetValueOrDefault()),
        };
        if (row.Humanlike is not null)
        {
            facts = facts with { Humanlike = Known(row.Humanlike.GetValueOrDefault()) };
        }
        if (row.LordJobClass is not null && !Boundary.IssueField(row.Issues, "lord_job_class"))
        {
            facts = facts with { LordJobClass = Known((row.LordJobClass ?? "")) };
        }
        if (row.LordToilClass is not null && !Boundary.IssueField(row.Issues, "lord_toil_class"))
        {
            facts = facts with { LordToilClass = Known((row.LordToilClass ?? "")) };
        }
        if (row.NearestColonistDistance is not null && !Boundary.IssueField(row.Issues, "nearest_colonist_distance"))
        {
            facts = facts with { NearestColonistDistance = Known(row.NearestColonistDistance.GetValueOrDefault()) };
        }
        if (row.Pawn?.Position is { X: not null, Z: not null } position)
        {
            facts = facts with { Position = Known(new Cell(position.X.GetValueOrDefault(), position.Z.GetValueOrDefault())) };
        }
        return facts;
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
                if (threat.Dead.TryGetValue(out var dead) && !dead)
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
    internal double PrimaryRange(O.PawnEquipment? equipment)
    {
        var (weapon, known) = Primary(equipment);
        return !known || !weapon.Ranged ? 0 : weapon.Range;
    }
}
