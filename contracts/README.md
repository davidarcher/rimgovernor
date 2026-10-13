# Contracts

Typed wire contracts between the Go controller and the native mod, plus the
native ownership references that explain what those contracts cover.

## Generated contracts

[Wire contracts](schema-generation.md) describes the plain C# records and their JSON shape (#3100), plus the
`proto/` inputs and `generated/` outputs that remain for the Go side until #3102 (`task protobuf:build` and
`task protobuf:test` still regenerate and cross-check them; hand-editing generated code is never correct).
[Native Protobuf cutover](native-protobuf-cutover.md) is the short history of the move off Protobuf.

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

## Fixtures and probes

`fixtures/` holds small sanitized native reply samples (`colony-core.json`,
`food-supply.json` and the things table rows its stocks reference,
`food-things.json`) that the Go observation, bridge and routine unit tests
decode. They establish parsing, not native outcomes. `tests/NativeContractProbes`
compiles the generated C# against the native project as a contract check.
