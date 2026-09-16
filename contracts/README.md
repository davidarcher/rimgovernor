# Contracts

Typed wire contracts between the Go controller and the native mod, plus the
native ownership references that explain what those contracts cover.

## Generated contracts

[Schema generation](schema-generation.md) defines the canonical Protobuf inputs
under `proto/`, the strict boundary rules and the versioned outputs under
`generated/`. `.github/workflows/protobuf.yml` regenerates and cross-checks the
C# and Go outputs on every change; hand-editing generated code is never correct.
[Native Protobuf cutover](native-protobuf-cutover.md) lists which native tools
run on those fixed schemas and how the boundary validates arguments.

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
`food-supply.json`) that the Go observation, bridge and routine unit tests
decode. They establish parsing, not native outcomes. `tests/NativeContractProbes`
compiles the generated C# against the native project as a contract check.
