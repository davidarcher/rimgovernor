# Placement previews

The shared native request and reply types are the Placement records
(`brain/src/RimGovernor.Brain.GameFacts/Placement.cs`), with semantic validation and collection
bounds in [placement coverage](wire/placement.md). The wire shape is described in
[wire contracts](wire/README.md).

A preview reports native placement facts; it does not authorize a write or prove
construction completed. Preserve identity, tick, ordered candidate outcomes,
required field presence, complete costs and occupied cells. Refused or unavailable
facts cannot become free construction or an empty site. The brain must apply
resource, ownership and safety policy before guarded execution.

The brain bridge and observation adapters consume the official messages. Native
adapter integration and gameplay acceptance remain tracked in
[GitHub issues](https://github.com/davidarcher/rimgovernor/issues).
