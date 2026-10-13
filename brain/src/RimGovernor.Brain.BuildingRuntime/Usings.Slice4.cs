// Namespaces shared by the rounds_room_reconcile..zone_preview slice of internal/buildingruntime.
// Go package qualifiers (Pol., store., bridge., executor., observation., telemetry., boundary.) stay
// qualifiers: they resolve to the sibling namespaces RimGovernor.Brain.<Package> from inside this assembly's
// namespace. Wire packages get aliases so the generated Protocol namespaces keep their Go package names.
// Pol and Sto alias the policy and store namespaces for code inside classes (Rounder) that have members
// named Policy and Store, where the plain qualifier would bind to the member.
global using System.Collections.Immutable;
global using System.Globalization;
global using RimGovernor.Brain.Domain;
global using static RimGovernor.Brain.BuildingRuntime.BuildingRuntimePackage;
// Go's policy package functions and concern ids live in PolicyFunctions (port convention of the policy slices).
global using static RimGovernor.Brain.Policy.PolicyFunctions;
global using ClockPb = RimGovernor.Protocol.Clock;
global using CommonPb = RimGovernor.Protocol.Common;
global using LifecyclePb = RimGovernor.Protocol.Lifecycle;
global using ObservationsPb = RimGovernor.Protocol.Observations;
global using OperationsPb = RimGovernor.Protocol.Operations;
global using Pol = RimGovernor.Brain.Policy;
global using Sto = RimGovernor.Brain.Store;
