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
    internal (PlanId Plan, bool Cast) CastMoodRelief(CancellationToken call, CancellationToken epoch, StepArbiter arbiter, IncidentState incident, PawnId target, GenerationSnapshot world)
    {
        var (royalty, pawns) = reviewer.Census.Psycasters();
        Exception? budgetError = null;
        var (choice, ok) = PolicyFunctions.SelectMoodCast(target, royalty, pawns, (caster, ability) =>
        {
            var prefix = BuildingRuntimePackage.MoodCastPrefix(target, caster, ability);
            try
            {
                var (_, allowed) = BuildingRuntimePackage.AdmitSubject(call, reviewer.Player.Journal, prefix, BuildingRuntimePackage.IncidentPlans(incident.Methods, prefix), world);
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

        if (!ok || !arbiter.TryClaim([new PawnId(choice.Caster)]))
        {
            return (default, false);
        }

        var source = PsycastSource.NewPsycastSource(choice.Ability);
        var aim = AbilityTarget.NewAbilityPawnTarget(new PawnId(choice.Target));
        var ability0 = Ability.NewAbility(new PawnId(choice.Caster), source, aim);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewAbilityAction(new ActionId($"{id}-0"), ability0);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        reviewer.Player.Current(call, epoch);
        var methodPrefix = BuildingRuntimePackage.MoodCastPrefix(target, choice.Caster, choice.Ability);
        var method = BuildingRuntimePackage.NextMethodId(methodPrefix, BuildingRuntimePackage.IncidentMethodIds(incident.Methods));
        reviewer.Player.Journal.CommitIncidentMethod(call, incident.Incident.Id, method, "", plan);
        return (id, true);
    }
}
