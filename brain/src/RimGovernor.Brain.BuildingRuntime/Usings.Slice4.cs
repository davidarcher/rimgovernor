// Namespaces shared by the rounds_room_reconcile..zone_preview slice.
// Package qualifiers (Pol., store., bridge., executor., observation., telemetry., boundary.) stay
// qualifiers: they resolve to the sibling namespaces RimGovernor.Brain.<Package> from inside this assembly's
// namespace. Wire packages get aliases so the generated Protocol namespaces keep short package names.
// Pol and Sto alias the policy and store namespaces for code inside classes (Rounder) that have members
// named Policy and Store, where the plain qualifier would bind to the member.
global using System.Collections.Immutable;
global using System.Globalization;
global using RimGovernor.Brain.Domain;
global using ClockPb = RimGovernor.Contracts.Wire.Clock;
global using CommonPb = RimGovernor.Contracts.Wire.Common;
global using LifecyclePb = RimGovernor.Contracts.Wire.Lifecycle;
global using ObservationsPb = RimGovernor.Contracts.Wire.Observations;
global using OperationsPb = RimGovernor.Contracts.Wire.Operations;
global using Pol = RimGovernor.Brain.Policy;
global using Sto = RimGovernor.Brain.Store;
