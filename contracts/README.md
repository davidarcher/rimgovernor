# Contracts

Typed wire contracts between the brain and the native mod, plus the
native ownership references that explain what those contracts cover.

## Wire contracts

[Wire contracts](wire/README.md) describes the plain C# `sealed record`s in
`brain/src/RimGovernor.Brain.Contracts` (namespace `RimGovernor.Contracts.Wire`) and their
System.Text.Json shape. Wire edits are made by editing the records; there is no code generation.
The per-family documents beside it give the presence, bounds and outcome semantics.

## Native ownership references

Established by source inspection of `integrations/rimgovernor-native`; they
describe current behavior and constraints, not gameplay acceptance.

- [Implemented operation variants](native-operation-variants.md): selectors,
  defaults, aliases and dry-run behavior of the production exports.
- [Mutable static state](native-static-state.md): lifecycle ownership and
  invalidation hazards of native static fields.
- [Saved-state ownership](native-state-ownership.md): persisted keys, owners and
  reconstruction/disconnect constraints.
- [Runtime and packaging](native-runtime-packaging.md): assembly, loader and
  package boundaries plus provenance.
- [Placement previews](placement-previews.md): preview semantics for placement.

## Probes

`tests/NativeContractProbes` compiles the wire records against the native project as a contract
check.
