using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsGearSource
{
    (O.ColonyFactsReply Reply, BridgeResult Result) ReadColonyFacts(CancellationToken ct, C.Identity identity, bool full);

    (IReadOnlyList<GearBenchRead> Benches, BridgeResult Result) ReadGearBenches(CancellationToken ct, C.Identity identity);
}

/// <summary>Composes MaintainEquipment's rounds step: apparel policy, outfit pruning and gear replacement orders.</summary>
public sealed partial class RoundsGearPlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    internal IRoundsGearSource Native { get; init; } = null!;

    public static RoundsGearPlanner NewRoundsGearPlanner(Rounder reviewer, IRoundsGearSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsGearPlanner: reviewer or native is missing");
        }

        return new RoundsGearPlanner { Reviewer = reviewer, Native = native };
    }
}

public readonly record struct RoundsGearResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

internal interface IFrameThingsSource
{
    Things FrameThings(CancellationToken ct, C.Identity identity);
}

internal static partial class BuildingRuntimePackage
{
    internal const int MaxGearMethods = 16;

    internal static (GearObservation Observation, bool Known) GearObservationFacts(O.ColonyFactsSnapshot observed, Tables tables, GearDefinitions defs)
    {
        var fact = ObservationPackage.GearFacts(observed, tables, defs);
        return (fact.ValueOrDefault!, fact.IsKnown);
    }

    internal static GearDefinitions GearDefinitionsOf(CancellationToken call, object source, C.Identity identity) => ObservationPackage.ReadGearDefinitions(call, source, identity);

    internal static (string Definition, bool Found) GearCandidateDefinition(GearObservation census, PawnId pawn, string target)
    {
        foreach (var p in census.Pawns)
        {
            if (p.Pawn != pawn)
            {
                continue;
            }

            if (!p.Candidates.TryGetValue(out var candidates))
            {
                return ("", false);
            }

            foreach (var c in candidates)
            {
                if (c.Target == target)
                {
                    return (c.Definition.Value, true);
                }
            }
        }

        return ("", false);
    }

    internal static MethodId PolicyMethod(StandardState goal, string prefix, string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{goal.Standard.Id}/{goal.Standard.Episode}/{value}"));
        return new MethodId($"{prefix}-{Convert.ToHexStringLower(digest.AsSpan(0, 16))}");
    }

    internal static bool SeenMethod(StandardState goal, MethodId method)
    {
        foreach (var m in goal.Methods)
        {
            if (m.Method == method)
            {
                return true;
            }
        }

        return false;
    }

    internal static Things FrameThingsOf(CancellationToken ct, object native, C.Identity identity) =>
        native is IFrameThingsSource source ? source.FrameThings(ct, identity) : new Things();

    internal static string GearDef(Things things, C.Ref reference) =>
        things.TryRow(reference, out var row) ? row.DefName : "";
}

public sealed partial class RoundsGearPlanner
{
    /// <summary>Stamps the creep-joiner downsides on the census when the source can read the combat pawns; otherwise leaves it as read.</summary>
    internal GearObservation StampCreepjoiners(CancellationToken call, C.Identity identity, ControlState state, GearObservation census, GearDefinitions defs)
    {
        if (Native is not IRoundsEquipSource source)
        {
            return census;
        }

        var ids = census.Pawns.Select(p => p.Pawn.Value).ToList();
        var (reply, _) = source.ReadCombatPawns(call, identity, ids);
        var pawns = reply.Observed;
        if (pawns is null)
        {
            throw new ControlException("stampCreepjoiners: no observed pawns");
        }

        try
        {
            Boundary.Context(pawns.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("stampCreepjoiners: " + e.Message, e);
        }

        return ObservationPackage.StampGearCreepjoiners(census, pawns, defs.Catalog.CreepJoinerDownsides());
    }

    /// <summary>Admits up to <see cref="BuildingRuntimePackage.MaxGearMethods"/> gear methods in one step, returning the last admitted result.</summary>
    internal RoundsGearResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        RoundsGearResult admitted = default;
        for (var i = 0; i < MaxGearMethods; i++)
        {
            var result = StepOne(call, epoch, arbiter);
            if (result.Verdict != BuildingReasonAdmitted)
            {
                if (admitted.Plan.Value.Length != 0)
                {
                    return admitted;
                }

                return result;
            }

            admitted = result;
        }

        return admitted;
    }

    internal RoundsGearResult StepOne(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsGearResult { Verdict = BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("stepOne: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsGearResult { Verdict = BuildingReasonNoReview };
        }

        var (recordedCall, recorded) = RecordPlannerStep(call, ConcernId.MaintainEquipment, state.Snapshot, review.Tick);
        call = recordedCall;
        using var recordedScope = recorded;
        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainEquipment);
        if (!workable)
        {
            return new RoundsGearResult { Verdict = BuildingReasonNoDeficit };
        }

        var busy = new HashSet<PawnId>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var pruning = false;
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(call, method.Plan);
            if (!StorePackage.PlanOpen(plan))
            {
                continue;
            }

            foreach (var action in plan.Spec.Actions)
            {
                if (action.GearReplace is { } wear)
                {
                    busy.Add(wear.Pawn);
                    claimed.Add(wear.Thing);
                }
                else if (action.ApparelPolicy is { } settings)
                {
                    busy.Add(settings.Pawn);
                }
                else if (action.PolicyPrune is not null)
                {
                    pruning = true;
                }
                else
                {
                    return new RoundsGearResult { Verdict = BuildingReasonExistingWork };
                }
            }
        }

        var started = Reviewer.Clock.Now();
        var identity = Boundary.Identity(state.Snapshot);
        var (reply, _) = Reviewer.ColonyFacts(call, Native, identity, true);
        var observed = reply.Observed;
        if (observed is null)
        {
            throw new ControlException("stepOne: no observed colony facts");
        }

        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("stepOne: observed context", e);
        }

        if (observed.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("stepOne: observed facts older than the review");
        }

        var gear = observed.Planning?.Observed?.Gear;
        if (gear is null || observed.ColonistCount is null || (uint)gear.Pawns.Count != observed.ColonistCount)
        {
            return new RoundsGearResult { Verdict = WaitFor(Cause.MethodUsed, "gear_observation") };
        }

        var things = FrameThingsOf(call, Native, identity);
        var defs = GearDefinitionsOf(call, Native, identity);
        var (census, known) = GearObservationFacts(observed, new Tables { Things = things }, defs);
        if (!known)
        {
            return new RoundsGearResult { Verdict = FieldUnavailable("gear_census") };
        }

        census = StampCreepjoiners(call, identity, state, census, defs);
        census = census with
        {
            Pawns = census.Pawns.Select(pawn =>
            {
                var candidates = pawn.Candidates.ValueOrDefault ?? [];
                var available = candidates.Where(c => !claimed.Contains(c.Target)).ToList();
                return pawn with { Blocked = pawn.Blocked || busy.Contains(pawn.Pawn), Candidates = Known<IReadOnlyList<GearCandidate>>(available) };
            }).ToList(),
        };
        RoundsGearResult policies = default;
        foreach (var pawn in census.Pawns)
        {
            if (pawn.Blocked)
            {
                continue;
            }

            var (value, needed) = PolicyFunctions.DesiredApparelPolicy(pawn);
            if (!needed)
            {
                continue;
            }

            if (SeenMethod(goal, PolicyMethod(goal, "apparel-policy", value.Encoded)) || !arbiter.TryClaim([value.Pawn]))
            {
                continue;
            }

            policies = CommitPolicyPlan(call, epoch, state, started, ref goal, "apparel-policy", value.Encoded, id => DomainAction.NewApparelPolicyAction(id, value));
        }

        if (policies.Plan.Value.Length != 0)
        {
            return policies;
        }

        var drop = census.OutfitsToPrune();
        if (drop.Count > 0 && !pruning)
        {
            var prune = PolicyPrune.NewPolicyPrune(PolicyKind.OutfitPolicies, drop);
            var result = CommitPolicyPlan(call, epoch, state, started, ref goal, "outfit-prune", "[" + string.Join(" ", prune.Ids) + "]", id => DomainAction.NewPolicyPruneAction(id, prune));
            if (result.Plan.Value.Length != 0)
            {
                return result;
            }
        }

        var seen = goal.Methods.Select(m => m.Method).ToList();
        var request = new GearPlanningRequest { Observation = Known(census), Seen = seen, Benches = Unknown<IReadOnlyList<GearBench>>() };
        SnapshotPackage.NoteGearMethod(request);
        var choice = PolicyFunctions.SelectGearMethod(request);
        var planId = PlanIds.MintPlanId();
        DomainAction action2;
        switch (choice.Kind)
        {
            case GearMethodKind.Replace:
                var (definition, ok) = GearCandidateDefinition(census, choice.Pawn, choice.Target);
                if (!ok)
                {
                    throw new ControlException("stepOne: no candidate definition for the chosen target");
                }

                if (!arbiter.TryClaim([new PawnId(choice.Pawn.Value)]))
                {
                    return new RoundsGearResult { Verdict = ClaimHeld("colonist") };
                }

                var replace = GearReplace.NewGearReplace(new PawnId(choice.Pawn.Value), choice.Target, definition);
                action2 = DomainAction.NewGearReplaceAction(new ActionId($"{planId}-0"), replace);
                break;
            default:
                if (busy.Count > 0)
                {
                    return new RoundsGearResult { Verdict = BuildingReasonExistingWork };
                }

                return new RoundsGearResult { Verdict = WaitFor(Cause.MethodUsed, "gear_bill") };
        }

        var spec = PlanSpec.NewPlan(planId, new PlanRevision(1), [action2]);
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("stepOne: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, choice.Id, spec);
        return new RoundsGearResult { Verdict = BuildingReasonAdmitted, Plan = planId };
    }

    /// <summary>Commits one single-action policy plan under a method id derived from the goal and the value; a method already seen is a no-op.</summary>
    internal RoundsGearResult CommitPolicyPlan(CancellationToken call, CancellationToken epoch, ControlState state, DateTimeOffset started, ref StandardState goal, string prefix, string value, Func<ActionId, DomainAction> build)
    {
        var p = Reviewer.Player;
        var method = PolicyMethod(goal, prefix, value);
        if (SeenMethod(goal, method))
        {
            return default;
        }

        var id = PlanIds.MintPlanId();
        var action = build(new ActionId(id.Value + "-0"));
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("commitPolicyPlan: session state changed or the read took too long");
        }

        goal = p.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return new RoundsGearResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
