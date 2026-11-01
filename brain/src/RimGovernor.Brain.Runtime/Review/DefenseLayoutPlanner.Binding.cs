using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Runtime;

public sealed partial class DefenseLayoutPlanner
{
    /// <summary>The step's verdict subject while a newly bound choice waits for the work of the choice it replaced to retire.</summary>
    internal const string DefenseRebound = "defense_rebound";

    /// <summary>The verdict subject while a spent turret barrel has no refuel the maintenance choice can order.</summary>
    internal const string DefenseTurretFuel = "turret_fuel";

    /// <summary>
    /// Binds the comparison's choice and runs its chain (task 7.4). A choice other than Retain enters the plan as the DefChoice
    /// node (DefenseBindingRules.Bind) and the open builds and removals of the choice it replaces retire: their unissued
    /// actions are cancelled, so their claims release, while orders Hands already issued finish. A bound choice the
    /// comparison retains (ties go to Retain, so a bound-but-unbuilt choice is never re-selected while it is still best) takes
    /// its next step (DefenseBindingRules.Next) over what stands: its free pieces, then the removal of what its other pieces
    /// replace (held, and an open removal's unissued actions cancelled, while its observed protection is below what it owes),
    /// then the service its protection names. Not done when nothing is bound.
    /// </summary>
    internal async Task<(DefenseLayoutResult Result, bool Done)> ChainAsync(NeedOwner goal, ControlState state, ReviewReading read, DefenseCut cut, DefenseDecision decision, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var tick = read.Reading.Identity.Tick;
        var open = OpenChain(goal, call);
        var chosen = decision.Chosen;
        double owed;
        if (decision.Kind != DefenseCandidateKind.Retain)
        {
            owed = DefenseBindingRules.Owed(decision.Rows.First(r => r.Alternative.Candidate.Kind == DefenseCandidateKind.Retain));
            // The transitions another planner bound since the cut was read stay on the plan (task 8.2).
            var plan = DefenseBindingRules.Bind(cut.Plan, chosen.Alternative.Candidate, owed);
            plan = plan.WithTransitionsOf(p.Journal.LayoutPlan(state.Snapshot, tick, call)?.Plan ?? plan);
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw DefenseControlErr(381);
            }
            p.Journal.RecordLayoutPlan(state.Snapshot, tick, plan, call);
            cut.Plan = plan;
            var retired = RetireUnissued(open, call);
            ForgetDecision();
            DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "defense_bound", chosen.Key, new Dictionary<string, object?>
            {
                ["kind"] = Wire.Name(decision.Kind),
                ["node"] = DefenseBindingRules.NodePrefix + chosen.Key,
                ["owed"] = owed,
                ["unbuilt"] = chosen.Unbuilt.Count,
                ["removes"] = string.Join(" ", chosen.Removes),
                ["retired_methods"] = open.Count,
                ["cancelled"] = retired,
            }, call);
            if (open.Count > 0)
            {
                return (new DefenseLayoutResult { Verdict = AwaitingPlan(DefenseRebound, chosen.Key) }, true);
            }
        }
        else if (DefenseBindingRules.Bound(cut.Plan) is { Tier: { } tier })
        {
            owed = tier.Owed;
        }
        else
        {
            return (new DefenseLayoutResult(), false);
        }
        var step = DefenseBindingRules.Next(chosen, DefenseChoiceRules.StandingOf(read.Reading.Facts), owed);
        var removals = open.Where(m => m.Method.Value.StartsWith(DefenseBindingRules.RemovalPrefix, StringComparison.Ordinal)).ToList();
        if (step is not DefenseStep.Remove && removals.Count > 0)
        {
            // A lost replacement piece reopens its build first; the removal it waited on is held before anything comes down.
            var held = RetireUnissued(removals, call);
            if (held > 0)
            {
                DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "waiting", DefenseBindingRules.RemovalHeld, chosen.Key, new Dictionary<string, object?>
                {
                    ["step"] = StepText(step), ["cancelled"] = held,
                }, call);
            }
        }
        switch (step)
        {
            case DefenseStep.Build b:
                if (open.Any(m => m.Method.Value.StartsWith(DefenseBindingRules.ChoicePrefix, StringComparison.Ordinal)))
                {
                    return (new DefenseLayoutResult { Verdict = BuildingReasonExistingWork }, true);
                }
                return (await AdmitChoiceAsync(goal, state, read, chosen with { Unbuilt = b.Pieces }, call, epoch).ConfigureAwait(false), true);
            case DefenseStep.Remove r:
                if (removals.Count > 0)
                {
                    return (new DefenseLayoutResult { Verdict = BuildingReasonExistingWork }, true);
                }
                return (AdmitRemoval(goal, state, read, chosen, r.Removals, call, epoch), true);
            case DefenseStep.Held h:
                return (new DefenseLayoutResult { Verdict = AwaitingPlan(h.Subject, h.State), Tier = DefenseBindingRules.BoundTier.Value }, true);
        }
        // Ready: every piece stands; issued work still running continues.
        return open.Count > 0 ? (new DefenseLayoutResult { Verdict = BuildingReasonExistingWork }, true) : (new DefenseLayoutResult(), false);
    }

    private static string StepText(DefenseStep step) =>
        step is DefenseStep.Build b ? string.Create(CultureInfo.InvariantCulture, $"build:{b.Pieces.Count}")
        : step is DefenseStep.Held h ? h.State
        : step is DefenseStep.Remove r ? string.Create(CultureInfo.InvariantCulture, $"remove:{r.Removals.Count}")
        : "ready";

    /// <summary>The bound choice's builds and removals whose plans are still open.</summary>
    private List<NeedMethodBinding> OpenChain(NeedOwner goal, CancellationToken call)
    {
        var p = _reviewer.Player;
        return [.. goal.Methods.Where(m => DefenseBindingRules.ChainMethod(m.Method.Value) && StorePackage.PlanOpen(p.Journal.LoadPlan(m.Plan, call)))];
    }

    /// <summary>Cancels the pending and prepared actions of <paramref name="methods"/>: their claims release, issued orders finish.</summary>
    private int RetireUnissued(IEnumerable<NeedMethodBinding> methods, CancellationToken call)
    {
        var p = _reviewer.Player;
        var cancelled = 0;
        foreach (var m in methods)
        {
            foreach (var progress in p.Journal.LoadPlan(m.Plan, call).Progress)
            {
                if (progress.View.Stage is Stage.Pending or Stage.Prepared)
                {
                    p.Journal.Cancel(m.Plan, progress.View.Action, call);
                    cancelled++;
                }
            }
        }
        return cancelled;
    }

    /// <summary>
    /// Admits the removal of the standing buildings the bound choice's remaining pieces replace as one Method
    /// (<c>defense-removal-&lt;digest&gt;</c>), its refusals spending that budget. A protected building is no removal: the
    /// chain holds naming it.
    /// </summary>
    private DefenseLayoutResult AdmitRemoval(NeedOwner goal, ControlState state, ReviewReading read, DefenseChoiceRow chosen, IReadOnlyList<DefenseRemoval> removals, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var facts = read.Reading.Facts;
        var things = new List<Deconstruction>();
        foreach (var r in removals)
        {
            if (facts.CurrentConstruction.Buildings.FirstOrDefault(b => b.Building.Definition == r.Definition && b.Cells.Contains(r.Cell)) is not { } b)
            {
                throw new RequiredFactException("defense removal building");
            }
            if (facts.Preferences.Protects(b.Id))
            {
                return new DefenseLayoutResult { Verdict = AwaitingPlan(DefenseBindingRules.RemovalHeld, "protected_thing:" + b.Id), Tier = DefenseBindingRules.BoundTier.Value };
            }
            if (things.All(t => t.Target != b.Id))
            {
                things.Add(new Deconstruction(b.Id, b.Building.Definition, b.Building.Cell));
            }
        }
        var (budget, admitted) = ReviewRefusalBudget.AdmitOwnerSubject(p.Journal, goal, DefenseBindingRules.RemovalPrefix, state.Snapshot, call);
        if (!admitted)
        {
            return new DefenseLayoutResult { Verdict = budget, Tier = DefenseBindingRules.BoundTier.Value };
        }
        var text = new StringBuilder();
        foreach (var t in things.OrderBy(t => t.Target, StringComparer.Ordinal))
        {
            text.Append(t.Target).Append(';');
        }
        var method = new MethodId(DefenseBindingRules.RemovalPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 8)));
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>(things.Count);
        for (var i = 0; i < things.Count; i++)
        {
            actions.Add(DomainAction.NewDeconstructionAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), things[i]));
        }
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw DefenseControlErr(382);
        }
        var commit = p.Journal.CommitOwnerWork(goal, method, "defense_removal", new PlanSpec(id, new PlanRevision(1), actions), call);
        if (commit.Outcome != Sto.ClaimOutcome.Admitted)
        {
            return new DefenseLayoutResult { Verdict = BuildingPlanner.OwnerCommitVerdict(commit), Tier = DefenseBindingRules.BoundTier.Value };
        }
        ForgetDecision();
        DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "defense_removal", chosen.Key, new Dictionary<string, object?>
        {
            ["method"] = method.Value, ["removals"] = string.Join(" ", things.Select(t => t.Definition + "@" + t.Target)),
        }, call);
        return new DefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id, Tier = DefenseBindingRules.BoundTier.Value };
    }

    /// <summary>
    /// Rearms the spent gun turrets through the maintenance choice's refuel (DefenseRearm over ServicingChoiceRules): one
    /// RecoveryService refuel Method per chosen turret and pawn, a running refuel observed rather than reordered, and a barrel
    /// no alternative refuels named by its decision. Not done when no barrel is spent.
    /// </summary>
    internal (DefenseLayoutResult Result, bool Done) Refuel(NeedOwner goal, ControlState state, ReviewReading read, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var colony = read.Reading;
        var empty = DefenseRearm.EmptyTurrets(colony.Tables.Buildings.Table.Values());
        if (empty.Count == 0)
        {
            return (new DefenseLayoutResult(), false);
        }
        var f = colony.Facts;
        var (underway, refused) = HomeUpkeepReader.ServicingHistory(p.Journal, state.Snapshot, goal.Need, [DefenseRearm.RefuelMethod], f.ForecastObserved.WorkPawns.Pawns,
            [(HomeUpkeepReader.RefuelJob, RimGovernor.Brain.GameFacts.Observations.ServicingKind.Refuel)], call);
        var census = f.ForecastObserved.Coupled?.Servicing is { } servicing ? HomeUpkeepReader.ServicingCensusOf(servicing, colony.Tables.Buildings.Table.At) : null;
        var decisions = DefenseRearm.Refuels(empty, census, underway, refused);
        var methods = DefenseRearm.Methods(goal.Need, decisions).Where(m => p.Journal.OwnerMethodPlan(goal, m.Method, call) is null).ToList();
        var tier = DefenseTurretsPolicy.TierTurrets.Value;
        if (methods.Count == 0)
        {
            return decisions.Any(d => d.Underway is not null)
                ? (new DefenseLayoutResult { Verdict = BuildingReasonExistingWork, Tier = tier }, true)
                : (new DefenseLayoutResult { Verdict = AwaitingPlan(DefenseTurretFuel, string.Join("; ", decisions.Select(ServicingChoiceRules.Describe))), Tier = tier }, true);
        }
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw DefenseControlErr(383);
        }
        PlanId last = default;
        foreach (var m in methods)
        {
            last = PlanIds.MintPlanId();
            var commit = p.Journal.CommitOwnerWork(goal, m.Method, m.Reason, m.Plan(last), call);
            if (commit.Outcome != Sto.ClaimOutcome.Admitted)
            {
                return (new DefenseLayoutResult { Verdict = BuildingPlanner.OwnerCommitVerdict(commit), Tier = tier }, true);
            }
            DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "turret_refuel", m.Method.Value, new Dictionary<string, object?> { ["reason"] = m.Reason }, call);
        }
        return (new DefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = last, Tier = tier }, true);
    }
}
