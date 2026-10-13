using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

// Port of the work census of internal/buildingruntime/clock_scheduler.go (clockSchedulerWork, roundsWork,
// runningWork, clockSchedulerCombatPlan, armedCombatStops, clockSchedulerKey). The window driver runs it after the
// planning step: the census decides whether a window has anything to spend its ticks on.

internal static class ClockWindowWork
{
    // Allow, work settings, zones, a building's temperature target, a bed's medical flag, a bed's owner, a grower's
    // crop, a claim, an auto-refuel switch, a surgery bill and a foundation removal's designation are immediate
    // designations and need no simulation window.
    private static readonly HashSet<ActionKind> Immediate =
    [
        ActionKind.SupplyAllow, ActionKind.SupplyForbid, ActionKind.WorkAssignment, ActionKind.ZoneCreate,
        ActionKind.BuildingTemperature, ActionKind.BedUse, ActionKind.Assign, ActionKind.GrowerCrop,
        ActionKind.ClaimBuilding, ActionKind.AutoRefuel, ActionKind.Surgery, ActionKind.AutoHomeArea,
        ActionKind.PawnSettings, ActionKind.Area, ActionKind.PolicyPrune, ActionKind.ReadingPolicy,
        ActionKind.DrugPolicy, ActionKind.FoodPolicy, ActionKind.ZoneDelete, ActionKind.ZoneCellEdit,
        ActionKind.StockpilePatch, ActionKind.FoundationRemoval, ActionKind.FloorRemoval,
    ];

    // Construction, native plant labor and the routine-dispatched action families (defense, medical, haul, equip)
    // all use the healthy-colony clock window; anything else is unsupported.
    private static readonly HashSet<ActionKind> Supported =
    [
        ActionKind.Acquisition, ActionKind.AcquisitionWithdraw, ActionKind.ProductionBill, ActionKind.OwnedDraft,
        ActionKind.Subdue, ActionKind.Tend, ActionKind.Rescue, ActionKind.Capture, ActionKind.UseItem,
        ActionKind.Haul, ActionKind.Equip, ActionKind.DropEquipment, ActionKind.GearReplace, ActionKind.ApparelPolicy,
        ActionKind.RecoveryService, ActionKind.Movement, ActionKind.Husbandry, ActionKind.PrisonerInteraction,
        ActionKind.Repair, ActionKind.Clean, ActionKind.MineAcquisition, ActionKind.Deconstruction,
        ActionKind.RemoveRoof, ActionKind.BuildRoof, ActionKind.AreaPlantCut, ActionKind.CutPlant, ActionKind.Strip,
        ActionKind.RulesAttach, ActionKind.MoveBuilding, ActionKind.UninstallBuilding, ActionKind.CoverClearance,
        ActionKind.WastepackHaul, ActionKind.MoodRelief, ActionKind.Excavation, ActionKind.DialogAnswer,
        ActionKind.Trade, ActionKind.CommsTradeRequest, ActionKind.QuestAccept, ActionKind.QuestShuttle,
        ActionKind.HackDesignation, ActionKind.GiveItem, ActionKind.Ritual, ActionKind.IdeoligionReform,
        ActionKind.Ability, ActionKind.Ignite, ActionKind.Gathering, ActionKind.RemoveProductionBill,
        ActionKind.DoorControl, ActionKind.WallRemoval, ActionKind.OpenCasket, ActionKind.CaravanDeparture,
    ];

    /// <summary>
    /// Whether <paramref name="plan"/> still has work that needs game time, with the fingerprint of its actions (Go
    /// <c>clockSchedulerWork</c>). A plan that is not the current revision, or an action kind the clock has no rule for,
    /// is refused rather than guessed at.
    /// </summary>
    public static (bool Work, List<ClockWorkItem> Items) Census(PlanState plan, GenerationSnapshot current)
    {
        if (plan.Spec.Id != current.Plan || plan.Spec.Revision != current.Revision || plan.Progress.Count != plan.Spec.Actions.Count)
        {
            throw new EvidenceException("clock work: plan is not the current revision");
        }
        var work = false;
        var items = new List<ClockWorkItem>(plan.Progress.Count);
        foreach (var p in plan.Progress)
        {
            var v = p.View;
            var kind = p.Action.Kind;
            items.Add(new ClockWorkItem(v.Action, kind, v.Stage, v.Attempt, v.Unresolved));
            // An applied building is pawn work until census-aware retirement ends its plan: its blueprint or frame
            // needs ticks.
            if (v.Stage == Stage.Completed && kind == ActionKind.Building && !plan.Retired)
            {
                work = true;
                continue;
            }
            if (v.Stage is Stage.Cancelled or Stage.Unsuccessful || (!v.Unresolved && v.Stage == Stage.Completed))
            {
                continue;
            }
            if (Immediate.Contains(kind))
            {
                continue;
            }
            if (p.Action.Building is null && !Supported.Contains(kind))
            {
                throw new HeldException("clock work: unsupported action kind " + Wire.Name(kind));
            }
            work = true;
        }
        return (work, items);
    }

    /// <summary><see cref="Census"/> over the authorized routine plans of the catalog: every plan but the root's own (Go <c>roundsWork</c>).</summary>
    public static async Task<(bool Work, List<ClockWorkItem> Items)> RoundsWorkAsync(StoreJournal journal, bool roundsMethods, GenerationSnapshot root, IReadOnlyList<PlanState> plans, CancellationToken ct)
    {
        var work = false;
        var fingerprint = new List<ClockWorkItem>();
        var authorizer = new PlanAuthorizer(journal, roundsMethods);
        foreach (var method in plans)
        {
            if (method.Spec.Id == root.Plan)
            {
                continue;
            }
            var target = root with { Plan = method.Spec.Id, Revision = method.Spec.Revision };
            try
            {
                await authorizer.AuthorizeRoundsPlanAsync(root, target, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;
            }
            var (remaining, items) = Census(method, target);
            work |= remaining;
            fingerprint.AddRange(items);
        }
        return (work, fingerprint);
    }

    /// <summary>
    /// The dispatched or awaiting-observation watched actions of the root plan the running window carries (<c>Live</c>)
    /// and the coupled orders ready under it (Go <c>runningWork</c>). A plan the census cannot read counts none.
    /// </summary>
    public static async Task<(int Live, IReadOnlyList<ActionId> Coupled)> RunningAsync(StoreJournal journal, GenerationSnapshot snapshot, Tick tick, CancellationToken ct)
    {
        var plan = await journal.LoadPlanAsync(snapshot.Plan, ct).ConfigureAwait(false);
        List<ClockWorkItem> items;
        try
        {
            items = Census(plan, snapshot).Items;
        }
        catch (Exception e) when (e is EvidenceException or HeldException)
        {
            return (0, []);
        }
        var coupled = plan.Spec.CoupledPending([.. plan.Progress], snapshot, tick);
        var live = 0;
        foreach (var item in items)
        {
            if (ClockLatched.WatchedKind(item.Kind) && item.Attempt != default && item.Stage is Stage.Dispatched or Stage.AwaitingObservation)
            {
                live++;
            }
        }
        return (live, coupled);
    }

    /// <summary>
    /// Whether the current rounds binds an ActiveCombat incident in deficit whose admitted plan still has open work:
    /// the only evidence under which live hostiles are watched rather than refused. <c>FightOpen</c> is whether a
    /// fight owns the combat and <c>Prey</c> the hunt origin's live prey (Go <c>clockSchedulerCombatPlan</c>).
    /// </summary>
    public static async Task<(bool Plan, bool FightOpen, EquatableArray<PawnId> Prey)> CombatPlanAsync(StoreJournal journal, GenerationSnapshot current, CancellationToken ct)
    {
        var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        if (!review.Enabled || review.Snapshot != current)
        {
            return (false, false, default);
        }
        var fights = await Task.Run(() => journal.OpenCombatFights(ct), ct).ConfigureAwait(false);
        var (incident, need, found) = await Task.Run(() => RoundsIncident(journal, review, PolicyApi.ActiveCombat, ct), ct).ConfigureAwait(false);
        if (!found || need != Situation.Active)
        {
            return (false, false, default);
        }
        foreach (var method in incident.Methods)
        {
            // A fight owns the combat: its orders go out at each stop, not as plan work. Closed, it holds the
            // incident until its claims are released, with no stops armed. A hunt origin's fight is watched through
            // its live prey.
            if (fights.TryGetValue(method.Plan, out var fight))
            {
                return (true, fight.Open, fight.Open ? new EquatableArray<PawnId>(ImmutableArray.CreateRange(incident.Incident.HuntPrey.Select(p => new PawnId(p.Value)))) : default);
            }
            var plan = await journal.LoadPlanAsync(method.Plan, ct).ConfigureAwait(false);
            if (Standard.WorkOpen(plan.Progress))
            {
                return (true, false, default);
            }
        }
        return (false, false, default);
    }

    /// <summary>The combat stop events a combat window arms: every stop event while an admitted fight plan owns the combat, none otherwise (Go <c>armedCombatStops</c>).</summary>
    public static IReadOnlyList<PbClock.CombatEvent> ArmedStops(bool fightOpen) => fightOpen ? ClockCombatStops.Events : [];

    /// <summary>
    /// The idempotency key of one logical window: a digest of the admission evidence, the work fingerprint and the
    /// start's speed, lease and policy (Go <c>clockSchedulerKey</c>). The Go key digested a JSON encoding; the digest
    /// here is over a canonical text of the same fields, so the two do not match, and a journal row written by Go is not
    /// recognized as the same logical window (a new request id is allocated instead of retained).
    /// </summary>
    public static string Key(ClockWindowAdmission admission, IReadOnlyList<ClockWorkItem> work, ClockStart start)
    {
        var text = new StringBuilder("clock-window\nversion 2\n");
        text.Append(admission).Append('\n');
        foreach (var item in work)
        {
            text.Append(item).Append('\n');
        }
        text.Append(start.Speed).Append('|').Append(start.TestAcceleration).Append('|').Append(start.LeaseMs).Append('\n');
        text.Append(start.Policy is null ? "" : RimGovernor.Contracts.Wire.WireJson.Serialize(start.Policy));
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return "clock-window-" + Convert.ToHexStringLower(sum);
    }
}
