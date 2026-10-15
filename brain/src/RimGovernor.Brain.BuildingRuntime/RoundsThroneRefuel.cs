namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsSleepingUpkeepPlanner
{
    internal static RoundsBuildingResult RefuelThrone(StepArbiter? arbiter, ControlState state,
        Store.Rounds review, Store.StandardState goal, Observation.RoundsReading reading,
        Pol.ThroneStep step, CancellationToken call, CancellationToken epoch) =>
        new() { Verdict = Verdict.AwaitingPlan("unsupported_forecast_throne_refuel",
            "Observed empty light requires exact fuel, worker and outcome alternatives.") };
}