# Go controller migration tools

The Go module exposes version/help and offline evidence replay. Native startup
and unsupported commands fail explicitly. Production launchers still use Python;
the Go command neither starts a bridge nor opens a colony database.

Use Go **1.27.1**, selected in `.go-version` and required by `go.mod`.
From this directory:

```powershell
$env:GOTOOLCHAIN = 'go1.27.1'
go test ./...
go vet ./...
go build -o ../.rimgovernor/go/rimgovernor.exe ./cmd/rimgovernor
```

Set `CGO_ENABLED=0` for the current module. The pinned MCP SDK and pure-Go SQLite
driver are exercised by in-memory protocol discovery and a temporary WAL database
test that closes, renames, reopens and deletes the database. This proves dependency
availability and closure; store compatibility and crash recovery belong to G01.04b.
The command does not yet link either adapter into a production runtime.

Dependencies and checksums are pinned in `go.mod` and `go.sum`. The MCP Go SDK
v1.7.0 supports maintained Go releases; modernc SQLite v1.58.0 lists Linux and
Windows amd64 support and requires its matching libc version, pinned by the module
graph. See [attribution](../THIRD_PARTY.md#go-dependencies). The intended generator
is repository-owned Go tooling under the same toolchain; G01.02 introduces it with
the first actual wire contract. No external schema generator is installed yet.

CI checks formatting, module integrity/drift, vet, tests and command builds on
Windows and Linux with the pinned toolchain. Linux also runs `go test -race ./...`
with CGO enabled and GCC available. Windows race coverage requires a compatible C
compiler and is not claimed by the current local checks. Go source uses LF on both
platforms so formatting results survive a clean checkout.

The existing Python/dashboard workflow is retained and also validates the migration
inventories and baseline fixtures. Media dependencies are unresolved until G01.09c;
this slice makes no promise of a fully
static production binary. See [G01](../docs/BACKLOG.md#g01--go-controller-rewrite)
for the native, storage, model and cutover gates.

## Offline evidence replay

```powershell
go run ./cmd/rimgovernor replay ../contracts/fixtures/state-baseline.json ../contracts/fixtures/state-baseline.json
```

Supply an expected recording and a candidate recording to compare their JSON.
Exit 0 means they match; exit 1 reports a difference, input error or invalid JSON;
exit 2 reports command usage errors. Files open read-only and close after the
comparison. This is an evidence comparator; it does not execute controller policy,
recover a game session or certify native outcomes. Later consumer chunks supply
their Go decision and receipt recordings for comparison.

Only insignificant JSON whitespace is normalized. Object/array order, IDs,
generations, unknown fields, nulls, string escapes and number lexemes remain
significant. In particular, `1`, `1.0`, and reordered object properties can differ
because persisted Python signatures preserve their representation. No timestamps
or other nondeterministic fields are silently dropped.

Each input is limited to 8 MiB including whitespace and 128 nested containers.
Malformed/trailing JSON, duplicate decoded keys and invalid UTF-8 fail explicitly.
The first mismatch is reported as a zero-based byte offset after whitespace
compaction. Raw evidence never becomes an unchecked domain payload.

`internal/testkit` also provides explicitly injected clocks and recorded ID
sequences for deterministic consumer tests. They do not advance simulation or
invent IDs after the recorded sequence is exhausted.
