namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The bare scope read every routine source offers.</summary>
internal static class StepScope
{
    /// <summary>
    /// Reads the observation scope (load, map, tick, generation, pause state) a Rounds pass or planner validates
    /// its reads against through lifecycle_read_tick, which the scheduler step's bundle seeds into the step read
    /// cache, so a review after a stop costs no identity round trip.
    /// </summary>
    internal static async Task<Observation.Identity> StepScopeAsync(Observation.ISource native, CancellationToken ct)
    {
        var reply = await native.TickAsync(ct).ConfigureAwait(false);
        return DecodeReads.DecodeTick(reply);
    }

    /// <summary>
    /// The scope a family read validates against: the one the clock step read before its wave when the read has it
    /// (<see cref="FamilyReadScope.Scope"/>, #3556), so the wave's entries cost no tick hop each; otherwise read now.
    /// </summary>
    internal static Task<Observation.Identity> StepScopeAsync(FamilyReadScope scope, Observation.ISource native, CancellationToken ct) =>
        scope.Scope is { } known ? Task.FromResult(known) : StepScopeAsync(native, ct);
}
