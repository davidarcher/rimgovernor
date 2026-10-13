// Namespaces of the sibling ports this project compiles against that the csproj does not already import.
// The packages are awaiting their port children (catalog #3056, executor #3055, observation/snapshot/facts
// #3052, bridge value types #3107); until they land these resolve to nothing.
global using System.Collections.Immutable;
global using RimGovernor.Brain.Facts;
global using RimGovernor.Brain.Executor;
// The wire records (Brain.Contracts), aliased as the Go code aliased its pb packages.
global using O = RimGovernor.Contracts.Wire.Observations;
global using C = RimGovernor.Contracts.Wire.Common;
global using K = RimGovernor.Contracts.Wire.Clock;
global using M = RimGovernor.Contracts.Wire.Mirror;
