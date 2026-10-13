using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsGearSource : Observation.IColonySource, IColonyFactsReader
{
    Task<(IReadOnlyList<GearBenchRead> Benches, BridgeResult Result)> ReadGearBenchesAsync(C.Identity identity, CancellationToken ct);
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

public readonly record struct RoundsGearResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

internal interface IFrameThingsSource
{
    Task<Things> FrameThingsAsync(C.Identity identity, CancellationToken ct);
}

internal static partial class BuildingRuntimePackage
{
    internal const int MaxGearMethods = 16;

    internal static (GearObservation Observation, bool Known) GearObservationFacts(O.ColonyFactsSnapshot observed, Tables tables, GearDefinitions defs)
    {
        var fact = ObservationPackage.GearFacts(observed, tables, defs);
        return (fact.ValueOrDefault!, fact.IsKnown);
    }

    internal static async Task<GearDefinitions> GearDefinitionsOfAsync(object source, C.Identity identity, CancellationToken call) => await ObservationPackage.ReadGearDefinitions(source, identity, call).ConfigureAwait(false);

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

    /// <summary>
    /// Go <c>ApparelPolicy.Encoded</c>: the policy's JSON spec (Go field names, definitions sorted), the seed of its
    /// method id. Written with <see cref="Utf8JsonWriter"/> (no reflection); doubles print shortest round-trip, which is
    /// Go's <c>strconv</c> form over the 0..1 HP range.
    /// </summary>
    internal static string ApparelPolicyEncoded(ApparelPolicy policy)
    {
        var s = policy.Spec;
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("Pawn", s.Pawn.Value);
            w.WriteString("Token", s.Token);
            w.WriteString("Name", s.Name);
            w.WriteStartArray("Definitions");
            foreach (var d in s.Definitions)
            {
                w.WriteStringValue(d);
            }
            w.WriteEndArray();
            w.WriteNumber("MinHP", s.MinHP);
            w.WriteNumber("MaxHP", s.MaxHP);
            w.WriteNumber("MinQuality", s.MinQuality);
            w.WriteNumber("MaxQuality", s.MaxQuality);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
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
            if (m.MethodId == method)
            {
                return true;
            }
        }

        return false;
    }

    internal static async Task<Things> FrameThingsOfAsync(object native, C.Identity identity, CancellationToken ct) =>
        native is IFrameThingsSource source ? await source.FrameThingsAsync(identity, ct).ConfigureAwait(false) : new Things();

    internal static string GearDef(Things things, C.Ref reference) =>
        things.TryRow(reference, out var row) ? row.Thing_?.DefName ?? "" : "";
}

public sealed partial class RoundsGearPlanner
{
    /// <summary>Stamps the creep-joiner downsides on the census when the source can read the combat pawns; otherwise leaves it as read.</summary>
    internal async Task<GearObservation> StampCreepjoinersAsync(C.Identity identity, ControlState state, GearObservation census, GearDefinitions defs, CancellationToken call)
    {
        if (Native is not IRoundsEquipSource source)
        {
            return census;
        }

        var ids = census.Pawns.Select(p => p.Pawn.Value).ToList();
        var (reply, _) = await source.ReadCombatPawnsAsync(identity, ids, call).ConfigureAwait(false);
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

        return ObservationPackage.StampGearCreepjoiners(census, pawns, (defs.Catalog ?? throw new ControlException("stampCreepjoiners: no definition catalog")).CreepJoinerDownsides());
    }

    /// <summary>Admits up to <see cref="BuildingRuntimePackage.MaxGearMethods"/> gear methods in one step, returning the last admitted result.</summary>
    internal async Task<RoundsGearResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        RoundsGearResult admitted = default;
        for (var i = 0; i < MaxGearMethods; i++)
        {
            var result = await StepOneAsync(arbiter, call, epoch).ConfigureAwait(false);
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

    internal async Task<RoundsGearResult> StepOneAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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

        using var recorded = PlannerSnapshot.RecordPlannerStep(PolicyFunctions.MaintainEquipment, state.Snapshot, review.Tick);
        try
        {
            return await StepOneRecordedAsync(arbiter, p, state, review, call, epoch).ConfigureAwait(false);
        }
        finally
        {
            recorded.Finish();
        }
    }

    private async Task<RoundsGearResult> StepOneRecordedAsync(StepArbiter arbiter, Player p, ControlState state, Sto.Rounds review, CancellationToken call, CancellationToken epoch)
    {
        var (goal, workable) = p.Journal.Workable(review, PolicyFunctions.MaintainEquipment, call);
        if (!workable || goal is null)
        {
            return new RoundsGearResult { Verdict = BuildingReasonNoDeficit };
        }

        var busy = new HashSet<Domain.PawnId>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var pruning = false;
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
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
        var reply = await Reviewer.ColonyFactsAsync(Native, identity, true, call).ConfigureAwait(false);
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
        if (gear is null || !observed.HasColonistCount || (uint)gear.Pawns.Count != observed.ColonistCount)
        {
            return new RoundsGearResult { Verdict = WaitFor(Cause.MethodUsed, "gear_observation") };
        }

        var things = await FrameThingsOfAsync(Native, identity, call).ConfigureAwait(false);
        var defs = await GearDefinitionsOfAsync(Native, identity, call).ConfigureAwait(false);
        var (census, known) = GearObservationFacts(observed, new Tables(Buildings.Of(), Pawns.Of(), things), defs);
        if (!known)
        {
            return new RoundsGearResult { Verdict = FieldUnavailable("gear_census") };
        }

        census = await StampCreepjoinersAsync(identity, state, census, defs, call).ConfigureAwait(false);
        census = census with
        {
            Pawns = census.Pawns.Select(pawn =>
            {
                var candidates = pawn.Candidates.TryGetValue(out var known0) ? known0 : default;
                var available = candidates.Where(c => !claimed.Contains(c.Target)).ToList();
                return pawn with { Blocked = pawn.Blocked || busy.Contains(new Domain.PawnId(pawn.Pawn.Value)), Candidates = Known(EquatableArray.From(available)) };
            }).ToList(),
        };
        RoundsGearResult policies = default;
        foreach (var pawn in census.Pawns)
        {
            if (pawn.Blocked)
            {
                continue;
            }

            if (!PolicyFunctions.DesiredApparelPolicy(pawn, out var desired) || desired is not { } value)
            {
                continue;
            }

            if (SeenMethod(goal, PolicyMethod(goal, "apparel-policy", ApparelPolicyEncoded(value))) || !arbiter.TryClaim([new PawnId(value.Pawn.Value)]))
            {
                continue;
            }

            policies = CommitPolicyPlan(state, started, ref goal, "apparel-policy", ApparelPolicyEncoded(value), id => DomainAction.NewApparelPolicyAction(id, value), call, epoch);
        }

        if (policies.Plan.Value.Length != 0)
        {
            return policies;
        }

        var drop = census.OutfitsToPrune();
        if (drop.Count > 0 && !pruning)
        {
            var prune = new PolicyPrune(PolicyDatabase.Outfit, drop);
            var result = CommitPolicyPlan(state, started, ref goal, "outfit-prune", "[" + string.Join(" ", prune.Ids) + "]", id => DomainAction.NewPolicyPruneAction(id, prune), call, epoch);
            if (result.Plan.Value.Length != 0)
            {
                return result;
            }
        }

        var seen = goal.Methods.Select(m => m.MethodId).ToList();
        var request = new GearPlanningRequest { Observation = Known(census), Seen = seen, Benches = Unknown<EquatableArray<GearBench>>() };
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

                if (!arbiter.TryClaim([choice.Pawn]))
                {
                    return new RoundsGearResult { Verdict = ClaimHeld("colonist") };
                }

                var replace = new GearReplace(new Domain.PawnId(choice.Pawn.Value), choice.Target, definition);
                action2 = DomainAction.NewGearReplaceAction(new ActionId($"{planId}-0"), replace);
                break;
            default:
                if (busy.Count > 0)
                {
                    return new RoundsGearResult { Verdict = BuildingReasonExistingWork };
                }

                return new RoundsGearResult { Verdict = WaitFor(Cause.MethodUsed, "gear_bill") };
        }

        var spec = new PlanSpec(planId, new PlanRevision(1), [action2]);
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("stepOne: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, choice.Id, spec, call);
        return new RoundsGearResult { Verdict = BuildingReasonAdmitted, Plan = planId };
    }

    /// <summary>Commits one single-action policy plan under a method id derived from the goal and the value; a method already seen is a no-op.</summary>
    internal RoundsGearResult CommitPolicyPlan(ControlState state, DateTimeOffset started, ref StandardState goal, string prefix, string value, Func<ActionId, DomainAction> build, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var method = PolicyMethod(goal, prefix, value);
        if (SeenMethod(goal, method))
        {
            return default;
        }

        var id = PlanIds.MintPlanId();
        var action = build(new ActionId(id.Value + "-0"));
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("commitPolicyPlan: session state changed or the read took too long");
        }

        goal = p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return new RoundsGearResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
