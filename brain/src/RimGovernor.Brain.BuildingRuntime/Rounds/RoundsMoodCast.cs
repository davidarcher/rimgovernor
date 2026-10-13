namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    internal static bool MoodCastReason(MoodMethodReason reason) =>
        reason is MoodMethodReason.NoCause or MoodMethodReason.Exhausted or MoodMethodReason.Unowned;

    internal static string MoodCastPrefix(PawnId target, PawnId caster, string ability) => $"mood-cast-{target}-{caster}-{ability}-";
}

public sealed partial class RoundsMoodReliefPlanner
{
    /// <summary>Casts a psycaster's mood ability on the target when policy picks a caster; returns the plan or none.</summary>
    internal (PlanId Plan, bool Cast) CastMoodRelief(StepArbiter arbiter, IncidentState incident, PawnId target, GenerationSnapshot world, CancellationToken call, CancellationToken epoch)
    {
        var (royalty, pawns) = reviewer.Census.Psycasters();
        Exception? budgetError = null;
        var selected = PolicyFunctions.SelectMoodCast(target, royalty, pawns, (caster, ability) =>
        {
            var prefix = BuildingRuntimePackage.MoodCastPrefix(target, caster, ability);
            try
            {
                var (_, allowed) = BuildingRuntimePackage.AdmitSubject(reviewer.Player.Journal, prefix, BuildingRuntimePackage.IncidentPlans(incident.Methods, prefix), world, call);
                return !allowed;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                budgetError ??= e;
                return true;
            }
        });
        if (budgetError is not null)
        {
            throw budgetError;
        }

        if (selected is not { } choice || !arbiter.TryClaim([choice.Caster]))
        {
            return (default, false);
        }

        var source = AbilitySource.PsycastSource(choice.Ability);
        var aim = AbilityTarget.AbilityPawnTarget(new Domain.PawnId(choice.Target.Value));
        var ability0 = new Ability(new Domain.PawnId(choice.Caster.Value), source, aim);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewAbilityAction(new ActionId($"{id}-0"), ability0);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        reviewer.Player.Current(call, epoch);
        var methodPrefix = BuildingRuntimePackage.MoodCastPrefix(target, choice.Caster, choice.Ability);
        var method = BuildingRuntimePackage.NextMethodId(methodPrefix, BuildingRuntimePackage.IncidentMethodIds(incident.Methods));
        reviewer.Player.Journal.CommitIncidentMethod(incident.Incident.Id, method, "", plan, call);
        return (id, true);
    }
}
