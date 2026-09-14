# Placement previews

The shared native request and reply types are defined in
[placement.proto](proto/placement.proto), with semantic validation and collection
bounds in [placement coverage](proto/placement-coverage.md). Use official generated
messages and ProtoJSON as described in [schema generation](schema-generation.md).

A preview reports native placement facts; it does not authorize a write or prove
construction completed. Preserve identity, tick, ordered candidate outcomes,
required field presence, complete costs and occupied cells. Refused or unavailable
facts cannot become free construction or an empty site. The controller must apply
resource, ownership and safety policy before guarded execution.

Go bridge and observation adapters consume the official messages. The model
interpreter owns a separate bounded proposal format and resolves definitions,
materials and anchors against supplied facts before domain construction. Native
adapter integration and gameplay acceptance remain tracked in
[GitHub issues](https://github.com/davidarcher/rimgovernor/issues).
