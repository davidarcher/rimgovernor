// Namespaces of the sibling ports this project compiles against that the csproj does not already import.
// The packages are awaiting their port children (catalog #3056, executor #3055, observation/snapshot/facts
// #3052, bridge value types #3107); until they land these resolve to nothing.
global using System.Collections.Immutable;
global using RimGovernor.Brain.Facts;
global using RimGovernor.Brain.Executor;
// Generated protobuf (Brain.Wire): the Go aliases o / c / b / bridge pb packages.
global using O = RimGovernor.Protocol.Observations;
global using C = RimGovernor.Protocol.Common;
global using K = RimGovernor.Protocol.Clock;
global using M = RimGovernor.Protocol.Mirror;
