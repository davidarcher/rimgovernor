namespace RimGovernor.Brain.Runtime;

public sealed partial class SleepingUpkeepPlanner
{
    internal static BuildingResult RefuelThrone(StepArbiter? arbiter, ControlState state,
        Store.ReviewState review, Sto.NeedOwner goal, ReviewReading reading,
        Pol.ThroneStep step, CancellationToken call, CancellationToken epoch) =>
        new() { Verdict = Verdict.AwaitingPlan("unsupported_forecast_throne_refuel",
            "Observed empty light requires exact fuel, worker and outcome alternatives.") };
}