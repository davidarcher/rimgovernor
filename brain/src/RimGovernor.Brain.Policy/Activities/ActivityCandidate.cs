namespace RimGovernor.Brain.Policy.Activities;

// Ephemeral inputs to ColonyForecast, not a second plan or persistence owner.
internal sealed record ActivityCandidate<T>(T Binding, ForecastActivityProblem Problem, double Units);
