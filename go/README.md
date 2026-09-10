# Go controller migration tools

The Go module currently exposes `rimgovernor version` and help. Native startup and
all other commands fail explicitly. Production launchers still use Python; this
command neither starts a bridge nor opens a colony database.

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

Offline replay and Windows/Linux CI gates are the next G01.01 subchunks. Media
dependencies are unresolved until G01.09c; this slice makes no promise of a fully
static production binary. See [G01](../docs/BACKLOG.md#g01--go-controller-rewrite)
for the native, storage, model and cutover gates.
