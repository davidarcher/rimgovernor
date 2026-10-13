namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The bare scope read every routine source offers.</summary>
internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// Reads the observation scope (load, map, tick, generation, pause state) a Rounds pass or planner validates
    /// its reads against through lifecycle_read_tick, which the scheduler step's bundle seeds into the step read
    /// cache, so a review after a stop costs no identity round trip.
    /// </summary>
    internal static async Task<Observation.Identity> StepScopeAsync(Observation.ISource native, CancellationToken ct)
    {
        var (reply, _) = await native.TickAsync(ct).ConfigureAwait(false);
        return ObservationApi.DecodeTick(reply);
    }
}
