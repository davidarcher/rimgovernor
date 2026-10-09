# Recording the definition catalog

Go tests read a recording of the native `DefinitionCatalog` reply instead of a
running game: `go/internal/observation/testdata/full_catalog.pb.gz` (the
untrimmed catalog with every installed expansion) and the planning-view golden
`planning_views.golden.gz` computed from it. `full_catalog.version`, beside the
recording, names the game it came from:

```
game_version=1.6.4871 rev590
dlcs=ludeon.rimworld.anomaly,ludeon.rimworld.biotech,...
defs_proto_source=Assembly-CSharp 1.6.9676.17735
```

`game_version` is the install's `Version.txt`, `dlcs` the expansions loaded, and
`defs_proto_source` the assembly line of `contracts/proto/defs.proto`. Game
identity is this sidecar; the catalog carries no version field.

## Refresh

Refresh after a game update, after regenerating `defs.proto`, or when native
adds catalog content. It needs the real game, so CI cannot do it. From `go/`,
with a worker root prepared by `acceptance setup` (its game stopped):

```
go run ./cmd/recordcatalog -root <absolute worker root>
```

The command refuses when `RIMGOVERNOR_ACCEPT_EXPANSIONS` is set. It starts the
headless game with every installed expansion, reads the catalog under the
loaded colony's identity, rejects a reply without the Biotech, Odyssey or
Anomaly section, then:

1. writes `full_catalog.pb.gz` (deterministic encoding);
2. writes `full_catalog.version`;
3. reruns `RG_UPDATE_GOLDEN=1 go test ./internal/observation -run TestPlanningViewsMatchTheRecordedGolden`.

It prints whether the new content equals the previous recording. The read
context (colony, load token, tick, native generation) differs on every run and
is not content; compare decoded protos with it cleared, never gzip bytes.

After a refresh run `go run ./cmd/test` and commit the recording, sidecar and
golden together; a golden diff is the review of what the new game changed.
