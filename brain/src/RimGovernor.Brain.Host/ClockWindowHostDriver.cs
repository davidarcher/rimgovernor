using RimGovernor.Brain.BuildingRuntime;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Hands the window driver of BuildingRuntime to the host's scheduler loop. The driver lives beside the clock
/// coordinator it commands (BuildingRuntime cannot see the host's seam); this is the whole of the seam's side of it.
/// The composition builds the driver with <see cref="ClockWindowDriver.CreateAsync"/> and returns this over it as
/// <see cref="PlannerParts.Windows"/>.
/// </summary>
public sealed class ClockWindowHostDriver(ClockWindowDriver driver) : IClockWindowDriver
{
    /// <inheritdoc />
    public Task<bool> AfterStepAsync(ClockSchedulerResult result, CancellationToken cancellationToken) =>
        driver.AfterStepAsync(result, cancellationToken);

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await driver.StopAsync(cancellationToken).ConfigureAwait(false);
        driver.Dispose();
    }
}
