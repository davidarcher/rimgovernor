# Remote acceptance v1 fixtures

[Contract and field ownership](../remote-acceptance.md)

Synthetic examples only: no game was run. The bundle is deliberately empty and
cannot decrypt or bootstrap; commit, release and run IDs are illustrative.
Manifest references use real SHA-256 hashes of the checked-in file bytes.
Keep JSON files LF-terminated; changing referenced bytes requires updating
all downstream references. These examples are not landing evidence.

Read `inventory.json`, `bundle.json`, `run.json`, `selection.json`, each shard's
`attempts.json`, then `aggregate.json` and the imported `result.json`.
The two `suite-s*.json` files project selection into the existing suite format.
Native result/bootstrap leaf files retain their own format, not the versioned
remote envelope. Public runner metadata here is illustrative, not a measurement.
