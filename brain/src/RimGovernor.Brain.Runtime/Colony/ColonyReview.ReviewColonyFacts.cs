namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>
    /// A planner's colony facts read (no extra definitions): the retained review frame's colony facts, by reference,
    /// while that review serves the planner's current identity (<see cref="CensusStore.ServedColony"/>),
    /// otherwise the newest native frame.
    /// </summary>
    internal async Task<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot> ColonyFactsAsync(GameClient native, RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct)
    {
        if (ReviewCensus.SameNativeSource(native, Native) && await ServedColonyAsync(identity, ct).ConfigureAwait(false) is { } served)
        {
            return served;
        }
        return await native.ReadColonyFactsAsync(identity, ct).ConfigureAwait(false);
    }

    private async Task<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot?> ServedColonyAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct)
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
