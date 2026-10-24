namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// A planner's colony facts read (no extra definitions): the retained review frame's colony facts, by reference,
    /// while that review serves the planner's current identity (<see cref="RoundsCensusStore.ServedColony"/>),
    /// otherwise native. A read without planning gets the planning section the native answers it with.
    /// </summary>
    internal async Task<PbObservations.ColonyFactsReply> ColonyFactsAsync(FacadeNativeClient native, PbCommon.Identity identity, bool planning, CancellationToken ct)
    {
        if (RoundsCensus.SameNativeSource(native, Native) && await ServedColonyAsync(identity, ct).ConfigureAwait(false) is { } served)
        {
            return new PbObservations.ColonyFactsReply { Outcome = planning ? served : served with { Planning = Colony.NotRequestedPlanning() } };
        }
        return await native.ReadColonyFactsAsync(identity, planning, ct).ConfigureAwait(false);
    }

    private async Task<PbObservations.ColonyFactsSnapshot?> ServedColonyAsync(PbCommon.Identity identity, CancellationToken ct)
    {
        Identity expected;
        try
        {
            expected = await StepScope.StepScopeAsync(Native, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
        if (expected.Colony.Value != (identity.ColonyId ?? "") || expected.Load.Value != (identity.LoadToken ?? "") || expected.Map.Value != identity.MapId)
        {
            return null;
        }
        return Census.ServedColony(expected);
    }
}
