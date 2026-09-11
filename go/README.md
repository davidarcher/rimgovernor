# Go controller development

The module currently provides version/help, offline replay and shared wire-contract
generation. Production launchers still use Python while the controller adapters
are implemented. Go will start with fresh state; importing Python databases and
matching historical save formats are not rewrite gates.

Use Go **1.27.1** from `.go-version`. From this directory:

```powershell
$env:GOTOOLCHAIN = 'go1.27.1'
$env:CGO_ENABLED = '0'
go test ./...
go vet ./...
go run ./cmd/contractgen -root .. -check contracts/generation.json
go build -o ../.rimgovernor/go/rimgovernor.exe ./cmd/rimgovernor
```

Python 3.12+ runs the generated Python boundary tests. CI also builds generated C#
with .NET SDK 8.0.424 and locked NuGet packages; its shared request cases run on
.NET Framework (Windows) and .NET 8. Linux Go race checks use CGO/GCC. Windows
race checks are not claimed without a compatible C compiler.

[Canonical schemas and generation](../contracts/schema-generation.md) own the
Go/C#/Python request models. Required/null/unknown fields, integer spellings,
UTF-16 bounds and malformed JSON are checked before returning typed values.
Native consumer wiring and observed game effects remain separate work.

Module dependencies and checksums are pinned in `go.mod`/`go.sum`; see the
[source notices](../THIRD_PARTY.md). The MCP and pure-Go SQLite adapters are
exercised with real SDK sessions and temporary databases as their slices land.
Media dependencies are selected with their actual presentation consumers.

## Optional evidence replay

```powershell
go run ./cmd/rimgovernor replay expected.json actual.json
```

Exit 0 means matching JSON evidence, 1 means a difference/input error, and 2 means
invalid command usage. This reads files only; it does not run policy or certify
native outcomes. Only insignificant whitespace is ignored. IDs, order, numeric
spellings, escapes, unknown fields and nulls remain significant. Inputs are bounded
to 8 MiB and 128 containers; malformed JSON, duplicates and invalid UTF-8 fail.

Injected clocks and ID sequences in `internal/testkit` support deterministic
behavior tests. Follow [G01](../docs/BACKLOG.md#g01--go-controller-rewrite) for
active owners, dependencies and completion gates.
