using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>What the admission of a fight carries: the combat read, the pawn inputs, the held layout, the geometry reply and the memory.</summary>
internal sealed class FightAdmission
{
    public BridgeCombat Combat = null!;
    public CombatInputs In = null!;
    public Fact<CombatLayout> Held;
    public GeometryReply Reply = null!;
    public CombatMemory Memory = null!;
}

public sealed partial class RoundsDefensePlanner
{
    /// <summary>The prefix of the map's fight, <c>engage-&lt;map&gt;</c> (#3311).</summary>
    internal const string EngageMethodPrefix = "engage-";

    /// <summary>The map's fight Method, named by its target: <c>engage-&lt;map&gt;</c>.</summary>
    internal static MethodId EngageMethod(GenerationSnapshot snapshot) =>
        new(EngageMethodPrefix + snapshot.Map.Value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Whether <paramref name="method"/> holds a fight: the map's engagement or a breaking colonist's subdue.</summary>
    internal static bool IsFightMethod(MethodId method) =>
        method.Value.StartsWith(EngageMethodPrefix, StringComparison.Ordinal) || method.Value.StartsWith(SubdueMethodPrefix, StringComparison.Ordinal);

    /// <summary>Admits a combat fight plan for the squad the memory names, drafts and loads out the pawns and issues the first orders.</summary>
    internal async Task<RoundsDefenseResult> AdmitFightAsync(ControlState state, DateTimeOffset started, StepArbiter arbiter, FightAdmission a, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var memory = a.Memory;
        if (memory.Roles.Count == 0)
        {
            var refusals = a.In.Hunters.Count > 0 || a.In.Prey.Count > 0 ? PolicyApi.HuntFormationRefusals(CombatView(a.Combat, a.In, [], a.Held)) : [];
            DefenseAction("routine-defense", TelemetryLevel.Info, "refused", "no_squad_roles", EngageMethod(state.Snapshot).Value, new Dictionary<string, object?> { ["tactic"] = memory.Tactic.ToString(), ["gate"] = memory.Refusal, ["hunt_prey"] = a.In.Prey.Count, ["hunt_hunters"] = a.In.Hunters.Count, ["hunt_refusals"] = string.Join(";", refusals), ["hunt_primary"] = string.Join(";", a.In.Hunters.Select(h => h + "=" + (a.In.Primary.TryGetValue(h, out var def) ? "def:" + def : "no_equipment_read") + "/ranged:" + (a.In.Ranged.TryGetValue(h, out var r) ? r.ToString() : "none"))), ["combat_pawns"] = a.Combat.Pawns.Count }, call);
            return new RoundsDefenseResult { Verdict = BuildingReasonNoSquad };
        }
        var pawns = new List<PawnId>(memory.Roles.Count);
        foreach (var role in memory.Roles)
        {
            pawns.Add(role.Pawn);
        }
        if (!arbiter.TryClaim(pawns))
        {
            return new RoundsDefenseResult { Verdict = WaitFor(Cause.MethodUsed, "combat_pawn_claim") };
        }
        var loadout = await FightLoadoutAsync(state, CombatView(a.Combat, a.In, pawns, a.Held), a.In.Rows, pawns, a.Combat.Catalog ?? throw new ControlException("fight admission: no definition catalog"), call).ConfigureAwait(false);
        var method = EngageMethod(state.Snapshot);
        var id = PlanIds.MintPlanId();
        var (actions, held) = LoadoutActions(id, loadout);
        var drafts = new List<PawnId>(pawns.Count);
        foreach (var pawn in pawns)
        {
            if (!held.Contains(pawn))
            {
                drafts.Add(pawn);
            }
        }
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("admitFight: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitCombatFight(state.Snapshot, method, spec, memory, [.. drafts.Select(d => (Domain.PawnId)d)], call);
        var admitted = new RoundsDefenseResult { Verdict = BuildingReasonAdmitted, Plan = id };
        var view = CombatView(a.Combat, a.In, drafts, a.Held);
        view = view with { Drugs = [.. await _reviewer.CombatDrugsAsync(state.Snapshot, call).ConfigureAwait(false)] };
        var (decided, ask, next) = PolicyApi.DecideCombat(view, a.Reply, new StopEvent(), memory);
        IReadOnlyList<CombatOrder> orders = decided;
        if (ask is not null)
        {
            orders = [];
            next = memory;
        }
        (orders, _) = SplitPermitCalls(orders);
        next = WithoutPermitMarks(next, memory);
        if (drafts.Count + orders.Count == 0)
        {
            p.Journal.SaveCombatMemory(id, next, call);
            return admitted;
        }
        IReadOnlyList<BridgeCombatOrderResult>? results;
        try
        {
            (results, orders) = await SendCombatBatchAsync(state, id, $"{id}-admit", drafts, orders, call).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Tel.Decide(new Decision
            {
                Kind = "admission",
                Component = "routine-defense",
                Level = TelemetryLevel.Warn,
                Verdict = "refused",
                Reason = "batch_failed",
                Target = id.Value,
                Attrs = new Dictionary<string, object?> { ["combat_plan"] = id.Value, ["error"] = ex },
            });
            return admitted;
        }
        (results, next) = RecordDrafts(id, drafts, results, next, call);
        next = BuildingRuntimePackage.CombatStopMemory(view, orders, results, next);
        if (orders.Count == 0)
        {
            p.Journal.SaveCombatMemory(id, next, call);
            return admitted;
        }
        var batchPlan = CombatBatchPlan(id, $"{id}-admit", drafts, orders);
        p.Journal.RecordCombatStop(id, batchPlan.Id, orders.Count, next, call);
        return admitted;
    }

    internal async Task ClearFightDoorsAsync(ControlState state, PlanId plan, CancellationToken call)
    {
        var kept = _reviewer.Player.Journal.LoadCombatRestoration(call);
        if (kept is null)
        {
            return;
        }
        if (kept.Owner != plan)
        {
            throw new ControlException("combat restoration is owned by another plan");
        }
        await RestoreCombatSettingsAsync(state, kept, call).ConfigureAwait(false);
    }

    /// <summary>Records which drafted pawns the native applied; returns the results after the draft rows and the memory without the refused.</summary>
    internal (IReadOnlyList<BridgeCombatOrderResult>? Results, CombatMemory Memory) RecordDrafts(PlanId plan, IReadOnlyList<PawnId> pawns, IReadOnlyList<BridgeCombatOrderResult>? results, CombatMemory memory, CancellationToken call)
    {
        if (results is null || pawns.Count == 0)
        {
            return (results, memory);
        }
        var add = new List<Domain.PawnId>();
        var drop = new List<Domain.PawnId>();
        for (var i = 0; i < pawns.Count; i++)
        {
            if (results[i].Applied)
            {
                add.Add(new Domain.PawnId(pawns[i].Value));
            }
            else
            {
                drop.Add(new Domain.PawnId(pawns[i].Value));
                memory = memory.Forget(pawns[i]);
            }
        }
        _reviewer.Player.Journal.UpdateCombatRoster(plan, add, drop, call);
        return (results.Skip(pawns.Count).ToList(), memory);
    }

    internal async Task<(PlanId Batch, int Sent, CombatMemory Memory)> IssueCombatOrdersAsync(ControlState state, PlanId plan, CombatView view, IReadOnlyList<PawnId> drafts, IReadOnlyList<CombatOrder> orders, CombatMemory memory, CancellationToken call)
    {
        var label = $"{plan}-stop-{view.Tick}";
        var (results, sent) = await SendCombatBatchAsync(state, plan, label, drafts, orders, call).ConfigureAwait(false);
        (results, memory) = RecordDrafts(plan, drafts, results, memory, call);
        var next = BuildingRuntimePackage.CombatStopMemory(view, sent, results, memory);
        var batchPlan = CombatBatchPlan(plan, label, drafts, sent);
        return (batchPlan.Id, sent.Count, next);
    }

    internal async Task<(IReadOnlyList<BridgeCombatOrderResult>? Results, IReadOnlyList<CombatOrder> Orders)> SendCombatBatchAsync(ControlState state, PlanId fight, string action, IReadOnlyList<PawnId> drafts, IReadOnlyList<CombatOrder>? orders, CancellationToken call)
    {
        orders = await CaptureCombatSettingsAsync(state, fight, orders ?? [], call).ConfigureAwait(false);
        if (drafts.Count + orders.Count == 0)
        {
            return (null, []);
        }
        var plan = CombatBatchPlan(fight, action, drafts, orders);
        var p = _reviewer.Player;
        var saved = p.Journal.CommitCombatBatch(state.Snapshot, plan, call);
        var progress = saved.Progress[0];
        if (!saved.Retired && !progress.View.Unresolved && (progress.View.Stage == Stage.Pending || progress.View.Stage == Stage.Prepared))
        {
            // Dispatch only a fresh batch; a recovered one carries its settled or unresolved outcome.
            var items = _hands.RunBatch(plan.Id, [plan.Actions[0].Id], call);
            RecordCombatDispatch(items, call);
            if (items.Count != 1)
            {
                throw new ControlException("combat batch run returned an unexpected result count");
            }
            var settled = items[0].Result.Progress ?? throw new ControlException("combat batch run returned no progress");
            if (items[0].Error is { } error && !settled.View.Unresolved)
            {
                throw error;
            }
            progress = settled;
        }
        p.Journal.RetireCombatBatch(plan.Id, call);
        if (progress.View.Receipt.TryGetValue(out var receipt) && (receipt == Domain.Receipt.Refused || receipt == Domain.Receipt.Unsent))
        {
            throw new ControlException($"combat batch {receipt}");
        }
        if (!progress.View.Combat.TryGetValue(out var results))
        {
            return (null, orders);
        }
        var output = new List<BridgeCombatOrderResult>();
        foreach (var v in results.Orders)
        {
            output.Add(new BridgeCombatOrderResult { Index = v.Index, PawnId = v.PawnId, Applied = v.Applied, Refusal = v.Refusal, JobDef = v.JobDef });
        }
        return (output, orders);
    }

    /// <summary>Answers the policy's geometry question from the native; see <see cref="BuildingRuntimePackage.AnswerGeometry"/>.</summary>
    internal GeometryReply AnswerGeometry(C.Identity identity, GeometryRequest? ask, CancellationToken ct) =>
        BuildingRuntimePackage.AnswerGeometry(
            request => _native.CombatGeometry(request, ct),
            (what, target, ex) => DefenseAction("routine-defense", TelemetryLevel.Warn, "failed", what, target, new Dictionary<string, object?> { ["error"] = ex }, ct),
            identity,
            ask);
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Answers the policy's geometry question from the native: the standable cells, proposals, scored cover lines and sight lines.</summary>
    internal static GeometryReply AnswerGeometry(Func<M.CombatGeometryRequest, M.CombatGeometry> read, Action<string, string, Exception> failed, C.Identity identity, GeometryRequest? ask)
    {
        var reply = new GeometryReply { Answered = true };
        if (ask is not null && ask.Propose == FormationRole.RescuePath)
        {
            return AnswerRescuePath(read, failed, identity, ask);
        }
        if (ask is null || (ask.Hostiles.Count == 0 && (ask.Propose != default || ask.Cells.Count == 0)))
        {
            return reply;
        }
        static C.Cell Wire(Cell cell) => new() { X = cell.X, Z = cell.Z };
        var line = ask.Line.Select(Wire).ToList();
        if (line.Count >= BridgeConstants.CombatGeometryMaxCells)
        {
            line = line.Take(BridgeConstants.CombatGeometryMaxCells - 1).ToList();
        }
        M.CombatGeometryPropose? propose = null;
        if (ask.Propose == FormationRole.CoverBehindLine)
        {
            var cover = new M.CombatCoverBehindLine();
            cover = cover with { Line = [.. cover.Line, .. line] };
            propose = new M.CombatGeometryPropose { Role = cover };
        }
        else if (ask.Propose == FormationRole.AdjacentToChoke)
        {
            propose = new M.CombatGeometryPropose { Role = new M.CombatAdjacentToChoke { Choke = Wire(ask.Choke), OurSide = Wire(ask.OurSide) } };
        }
        else if (ask.Propose == FormationRole.FiringCells)
        {
            var targets = ask.Targets.Select(Wire).ToList();
            if (targets.Count > BridgeConstants.CombatGeometryMaxHostiles)
            {
                targets = targets.Take(BridgeConstants.CombatGeometryMaxHostiles).ToList();
            }
            var firing = new M.CombatFiringCells { From = Wire(ask.From), Radius = BridgeConstants.CombatGeometryMaxRadius };
            firing = firing with { Targets = [.. firing.Targets, .. targets] };
            propose = new M.CombatGeometryPropose { Role = firing };
        }
        reply = reply with { Role = ask.Propose };
        var named = ask.Line.ToList();
        foreach (var cell in ask.Cells)
        {
            if (!named.Contains(cell))
            {
                named.Add(cell);
            }
        }
        var limit = BridgeConstants.CombatGeometryMaxCells;
        if (propose is not null)
        {
            limit--;
        }
        if (named.Count > limit)
        {
            named = named.Take(limit).ToList();
        }
        if (propose is null && named.Count == 0)
        {
            return reply;
        }
        var cells = named.Select(Wire).ToList();
        var hostiles = ask.Hostiles.Select(h => h.Value).ToList();
        var request = BridgeApi.CombatGeometryAsk(identity, cells, hostiles, "");
        request = request with { Propose = propose };
        M.CombatGeometry geometry;
        try
        {
            geometry = read(request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed("geometry_read", "", ex);
            return reply;
        }
        var standable = new List<Cell>();
        var proposals = new List<Cell>();
        foreach (var row in geometry.Cells)
        {
            if (row.Cell is { } cell && row.Standable.GetValueOrDefault())
            {
                standable.Add(new Cell(cell.X, cell.Z));
            }
        }
        foreach (var row in geometry.Proposed)
        {
            if (row.Cell is { } cell)
            {
                proposals.Add(new Cell(cell.X, cell.Z));
                standable.Add(new Cell(cell.X, cell.Z));
            }
        }
        var sightLines = new List<SightLine>();
        var scoredCells = new List<ScoredCell>();
        foreach (var row in geometry.Cells.Concat(geometry.Proposed).ToList())
        {
            var scoredAt = new Cell(row.Cell?.X ?? 0, row.Cell?.Z ?? 0);
            var coverLines = new List<CoverLine>();
            foreach (var l in row.Lines)
            {
                var hostile = new PawnId((l.HostileId ?? ""));
                coverLines.Add(new CoverLine { Hostile = hostile, Cover = l.Cover.GetValueOrDefault(), HostileCover = l.HostileCover.GetValueOrDefault(), LineOfFire = l.LineOfFire.GetValueOrDefault() });
                sightLines.Add(new SightLine { Cell = scoredAt, Hostile = hostile, LineOfFire = l.LineOfFire.GetValueOrDefault(), ColonistInPath = l.ColonistInPath.GetValueOrDefault() });
            }
            scoredCells.Add(new ScoredCell { Cell = scoredAt, Lines = [.. coverLines] });
        }
        return reply with { Standable = [.. standable], Proposals = [.. proposals], Lines = [.. sightLines], Scored = [.. scoredCells] };
    }

    internal static GeometryReply AnswerRescuePath(Func<M.CombatGeometryRequest, M.CombatGeometry> read, Action<string, string, Exception> failed, C.Identity identity, GeometryRequest ask)
    {
        var reply = new GeometryReply { Answered = true, Role = FormationRole.RescuePath };
        if (ask.Hostiles.Count == 0 || ask.Pawn.IsEmpty)
        {
            return reply;
        }
        var hostiles = ask.Hostiles.Select(h => h.Value).ToList();
        var propose = new M.CombatGeometryPropose { Role = new M.CombatRescuePath { To = new C.Cell { X = ask.To.X, Z = ask.To.Z } } };
        M.CombatGeometry geometry;
        try
        {
            geometry = read(BridgeApi.CombatGeometryProposeAsk(identity, propose, hostiles, ask.Pawn.Value));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed("rescue_path_read", ask.Pawn.Value, ex);
            return reply;
        }
        var route = new List<RouteCell>();
        foreach (var row in geometry.Proposed)
        {
            if (row.Cell is { } cell)
            {
                route.Add(new RouteCell { Cell = new Cell(cell.X, cell.Z), LineOfFire = row.HostileLineOfFire.GetValueOrDefault(), Door = row.Door.GetValueOrDefault() });
            }
        }
        return reply with { Route = [.. route] };
    }

    /// <summary>The rows of a combat read as policy pawn states; falls back to the observation rows when the combat read lists no pawns.</summary>
    internal static List<CombatPawnState> CombatPawnStates(BridgeCombat combat, IReadOnlyDictionary<string, O.PawnState> rows, IReadOnlyDictionary<string, WeaponDef> weapons)
    {
        var output = new List<CombatPawnState>();
        if (combat.Pawns.Count > 0)
        {
            foreach (var row in combat.Pawns)
            {
                weapons.TryGetValue((row.Weapon ?? ""), out var facts);
                var s = new CombatPawnState
                {
                    Id = new PawnId((row.Id ?? "")),
                    Downed = row.Downed.GetValueOrDefault(),
                    Dead = row.Dead.GetValueOrDefault(),
                    Target = new PawnId((row.TargetId ?? "")),
                    Stance = CombatStance(row.Stance.GetValueOrDefault()),
                    Weapon = (row.Weapon ?? ""),
                    WeaponFacts = facts ?? new(),
                    WeaponRange = facts?.Reach ?? 0,
                    FireMode = BridgeApi.FireModeName(row.FireMode.GetValueOrDefault()),
                    Job = row.Job ?? "",
                    TargetMortar = row.TargetMortar.GetValueOrDefault(),
                    ShieldBelt = row.ShieldBelt.GetValueOrDefault(),
                    MedicalSkill = row.MedicalSkill.GetValueOrDefault(),
                    MoveSpeed = row.MoveSpeed.GetValueOrDefault(),
                    StunTicks = row.StunTicksLeft.GetValueOrDefault(),
                    GoJuice = row.GoJuiceHigh.GetValueOrDefault(),
                    Luciferium = row.LuciferiumAddicted.GetValueOrDefault(),
                    Animal = row.Side.GetValueOrDefault() == M.CombatSide.ColonyAnimal,
                };
                if (row.CarriedDrugs is { } drugs)
                {
                    s = s with { CarriedDrugs = Known<EquatableArray<string>>([.. drugs.Defs]) };
                }
                if (row.Cell is { } cell)
                {
                    s = s with { Cell = Known(new Cell(cell.X, cell.Z)) };
                }
                if (row.ShieldEnergy is not null)
                {
                    s = s with { Shield = Known(row.ShieldEnergy.GetValueOrDefault()) };
                }
                s = s with { Prisoner = row.Side.GetValueOrDefault() == M.CombatSide.Prisoner };
                s = s with { Wild = row.Side.GetValueOrDefault() == M.CombatSide.WildAnimal };
                if (row.Health is not null)
                {
                    s = s with { Health = Known(row.Health.GetValueOrDefault()) };
                }
                rows.TryGetValue((row.Id ?? ""), out var pawnRow);
                output.Add(ThreatFacts(s, pawnRow, combat.Catalog ?? throw new ControlException("combat pawn states: no definition catalog")));
            }
            return output;
        }
        var ids = rows.Keys.ToList();
        ids.Sort(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var row = rows[id];
            var s = new CombatPawnState { Id = new PawnId(id), Downed = row.Downed, Dead = row.Dead };
            if (row.Pawn.Position is { } position)
            {
                s = s with { Cell = Known(new Cell(position.X, position.Z)) };
            }
            output.Add(ThreatFacts(s, row, combat.Catalog ?? throw new ControlException("combat pawn states: no definition catalog")));
        }
        return output;
    }

    internal static void RecordCombatStop(BridgeCombat combat, SnapshotCombatStop s, CancellationToken ct)
    {
        var dir = Environment.GetEnvironmentVariable(SnapshotApi.DirEnv);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        try
        {
            SnapshotApi.RecordCombatStop(dir, combat.Frame, s);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DefenseSnapshotSkip("defense", "combat", ex, ct);
        }
    }

    /// <summary>Adds the race, health and sapper facts of an observation row to a combat pawn state.</summary>
    internal static CombatPawnState ThreatFacts(CombatPawnState s, O.PawnState? row, DefinitionCatalog catalog)
    {
        if (row is null)
        {
            return s;
        }
        s = s with { Kind = row.KindDefName };
        var (mech, insect) = catalog.RaceFlags(row.Pawn.DefName ?? "");
        s = s with { Mech = mech, Insect = insect };
        if (row.Health is { } h)
        {
            s = s with { BloodLoss = Known(h.BloodLoss), BleedRatePerDay = Known(h.BleedRatePerDay) };
            if (h.HoursUntilDeathFromBloodLoss is not null)
            {
                s = s with { HoursUntilBleedDeath = Known(h.HoursUntilDeathFromBloodLoss.GetValueOrDefault()) };
            }
        }
        var toil = row.LordToilClass ?? "";
        s = s with { Sapper = toil.Contains("Sapper", StringComparison.Ordinal) || toil.Contains("Breach", StringComparison.Ordinal) || (row.Job?.DefName ?? "") == "Mine" };
        return s;
    }

    internal static CombatStance CombatStance(M.CombatStance s) => s switch
    {
        M.CombatStance.Idle => Pol.CombatStance.Idle,
        M.CombatStance.Warmup => Pol.CombatStance.Warmup,
        M.CombatStance.Cooldown => Pol.CombatStance.Cooldown,
        M.CombatStance.Moving => Pol.CombatStance.Moving,
        M.CombatStance.Melee => Pol.CombatStance.Melee,
        _ => Pol.CombatStance.Unknown,
    };

    /// <summary>The newest stop event of the combat read after the tick, or the empty event.</summary>
    internal static StopEvent CombatStop(BridgeCombat combat, Tick since)
    {
        M.CombatEventRow? newest = null;
        foreach (var row in combat.Events)
        {
            if (row.Stop is null || (row.At?.Tick).GetValueOrDefault() <= since.Value)
            {
                continue;
            }
            if (newest is null || BridgeApi.CombatBefore(newest.At, row.At))
            {
                newest = row;
            }
        }
        if (newest is null)
        {
            return new StopEvent();
        }
        return new StopEvent { Kind = newest.Stop.GetValueOrDefault(), Pawn = new PawnId((newest.ThingId ?? "")), Target = new PawnId((newest.TargetId ?? "")) };
    }

    /// <summary>The memory after a stop's order results: each refused order is remembered by its refusal. Unknown results change nothing.</summary>
    internal static CombatMemory CombatStopMemory(CombatView view, IReadOnlyList<CombatOrder> orders, IReadOnlyList<BridgeCombatOrderResult>? results, CombatMemory memory)
    {
        if (results is null)
        {
            return memory;
        }
        var cells = new Dictionary<PawnId, Cell>();
        foreach (var p in view.Pawns)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                cells[p.Id] = c;
            }
        }
        for (var i = 0; i < orders.Count; i++)
        {
            var order = orders[i];
            var refusal = results[i].Refusal;
            if (results[i].Applied)
            {
                continue;
            }
            if (order.Kind == CombatOrderKind.Door && refusal == BridgeConstants.CombatRefusalNotADoor)
            {
                memory = memory.RefuseDoor(order.Cell);
            }
            else if (order.Kind == CombatOrderKind.MortarFire)
            {
                memory = memory.RefuseFire(order, refusal == BridgeConstants.CombatRefusalNoShell || refusal == BridgeConstants.CombatRefusalUnknownShell);
            }
            else if (!order.Pawn.IsEmpty)
            {
                if (PolicyApi.AnimalOrderKind(order.Kind))
                {
                    memory = memory.RefuseAnimal(order, refusal);
                }
                else if (cells.TryGetValue(order.Pawn, out var from) && refusal == BridgeConstants.CombatRefusalCannotHit)
                {
                    memory = memory.RefuseHit(order, from);
                }
                else if (refusal == BridgeConstants.CombatRefusalNoShell || refusal == BridgeConstants.CombatRefusalUnknownShell)
                {
                    memory = memory.RefuseShell(order);
                }
                else if (order.Kind == CombatOrderKind.Move && refusal == BridgeConstants.CombatRefusalUnreachable)
                {
                    memory = memory.RefuseCell(order.Cell).Forget(order.Pawn);
                }
                else
                {
                    memory = memory.Forget(order.Pawn);
                }
            }
        }
        return memory;
    }

    internal static List<CombatCommand> CombatBatchCommands(IReadOnlyList<PawnId> drafts, IReadOnlyList<CombatOrder> orders)
    {
        var commands = new List<CombatCommand>();
        foreach (var p in drafts)
        {
            commands.Add(new CombatCommand { Kind = "draft", Pawn = p });
        }
        foreach (var o in orders)
        {
            commands.Add(new CombatCommand
            {
                Kind = Wire.Name(o.Kind),
                Pawn = o.Pawn,
                Target = o.Target,
                Cell = o.Cell,
                Aim = o.Aim,
                Door = Wire.Name(o.Door),
                FireMode = o.FireMode,
                Clear = o.Clear,
                Shell = o.Shell,
                Drug = o.Drug,
            });
        }
        return commands;
    }

    /// <summary>The one-action plan of a combat batch; the plan, action and key are the hash of the action label and the commands.</summary>
    internal static PlanSpec CombatBatchPlan(PlanId fight, string action, IReadOnlyList<PawnId> drafts, IReadOnlyList<CombatOrder> orders)
    {
        var commands = CombatBatchCommands(drafts, orders);
        var text = new StringBuilder(action).Append('\n');
        foreach (var c in commands)
        {
            text.Append(CultureInfo.InvariantCulture, $"{c.Kind}|{c.Pawn.Value}|{c.Target.Value}|{c.Cell.X},{c.Cell.Z}|{c.Aim.X},{c.Aim.Z}|{c.Door}|{c.FireMode}|{c.Clear}|{c.Shell}|{c.Drug}\n");
        }
        var key = "combat-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        var batch = new CombatBatch(fight, key, commands);
        var a = DomainAction.NewCombatBatchAction(new ActionId(key), batch);
        return new PlanSpec(new PlanId(key), new PlanRevision(1), [a]);
    }

    internal static void RecordCombatDispatch(IReadOnlyList<BatchItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (!item.Result.NativeCalled)
            {
                continue;
            }
            var v = (item.Result.Progress ?? throw new ControlException("combat dispatch returned no progress")).View;
            Tel.Decide(new Decision
            {
                Kind = "dispatch",
                Component = "routine-defense",
                Target = item.Action.Value,
                Verdict = "dispatched",
                Reason = "native_called",
                Attrs = new Dictionary<string, object?> { ["dispatch_tick"] = v.Tick.Value, ["attempt"] = (long)v.Attempt.Value, ["stage_after"] = v.Stage.ToString() },
            });
        }
    }

    internal static List<PawnId> UndraftedRoles(CombatMemory m, IReadOnlyList<PawnId> orderable, CombatView view)
    {
        var down = new Dictionary<PawnId, bool>();
        foreach (var p in view.Pawns)
        {
            down[p.Id] = p.Dead || p.Downed;
        }
        var output = new List<PawnId>();
        foreach (var role in m.Roles)
        {
            if (!orderable.Contains(role.Pawn) && !(down.TryGetValue(role.Pawn, out var isDown) && isDown) && !output.Contains(role.Pawn))
            {
                output.Add(role.Pawn);
            }
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return output;
    }
}
