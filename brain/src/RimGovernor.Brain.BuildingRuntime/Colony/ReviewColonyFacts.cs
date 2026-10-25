namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// A planner's colony facts read (no extra definitions): the retained review frame's colony facts, by reference,
    /// while that review serves the planner's current identity (<see cref="RoundsCensusStore.ServedColony"/>),
    /// otherwise the newest native frame.
    /// </summary>
    internal async Task<PbObservations.ColonyFactsSnapshot> ColonyFactsAsync(FacadeNativeClient native, PbCommon.Identity identity, CancellationToken ct)
    {
        if (RoundsCensus.SameNativeSource(native, Native) && await ServedColonyAsync(identity, ct).ConfigureAwait(false) is { } served)
        {
            return served;
        }
        return await native.ReadColonyFactsAsync(identity, ct).ConfigureAwait(false);
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
