using RimGovernor.Brain.BuildingRuntime;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Hands the run driver of BuildingRuntime to the host's scheduler loop. The driver lives beside the session it
/// commands (BuildingRuntime cannot see the host's seam); this is the whole of the seam's side of it. The composition
/// builds the driver with <see cref="ClockRunDriver.Create"/> and returns this over it as
/// <see cref="PlannerParts.Clock"/>.
/// </summary>
public sealed class ClockRunHostDriver(ClockRunDriver driver) : IClockRunDriver
{
    /// <inheritdoc />
    public Task<bool> AfterStepAsync(ClockSchedulerResult result, CancellationToken cancellationToken) =>
        driver.AfterStepAsync(result, cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => driver.StopAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose() => driver.Dispose();
}
