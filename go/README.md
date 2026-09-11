# Go controller development

The module provides a read-only local service, version/help and offline replay.
Production launchers still use Python while the controller adapters
are implemented. Go will start with fresh state; importing Python databases and
matching historical save formats are not rewrite gates.

Use Go **1.27.1** from `.go-version`. From this directory:

```powershell
$env:GOTOOLCHAIN = 'go1.27.1'
$env:CGO_ENABLED = '0'
go test ./...
go vet ./...
go build -o ../.rimgovernor/go/rimgovernor.exe ./cmd/rimgovernor
```

The shared Protobuf checks compile generated C# with .NET SDK 8.0.424 and locked
NuGet packages; binary/ProtoJSON exchanges run on .NET Framework and Linux Mono.
Linux Go race checks use CGO/GCC. Windows
race checks are not claimed without a compatible C compiler.

[Canonical schemas and generation](../contracts/schema-generation.md) use official
Protobuf tools. The generated Go wire module and its proof module have separate
checks documented in [the Go generation guide](../tools/protobuf/go/README.md).
Generated parsing preserves transport presence; domain validation enforces bounds,
scope and authority. Native adapters and observed game effects need separate tests.

Module dependencies and checksums are pinned in `go.mod`/`go.sum`; see the
[source notices](../THIRD_PARTY.md). The MCP and pure-Go SQLite adapters are
exercised with real SDK sessions and temporary databases as their slices land.
Media dependencies are selected with their actual presentation consumers.

## Local interpretation

`interpreter.NewLocal` checks the configured LM Studio instance before each
interpretation and uses the smaller of its loaded context window and the configured
budget. Missing or ambiguous instances fail explicitly. It does not load or switch
models. Proposals remain unsubmitted until a player runtime admits them.

## Read-only service

Build the executable above, then use `rimgovernor serve --read-only` with absolute
`--gabs`, `--config` and `--state` paths plus the configured `--game` ID. It attaches
through GABS to the running game and opens a fresh Go SQLite database. An optional
absolute `--assets` directory serves a built dashboard containing `index.html`.
`--listen` defaults to `127.0.0.1:0`; startup prints the selected local URL. Only
loopback IP addresses and numeric ports are accepted.

The service exposes health/state/plan reads and retains last-good observations
when refresh fails, marking them stale. It starts in Manual and cannot issue game
orders. Interrupting the process cancels and joins polling before closing its SDK,
database and asset handles. Native read acceptance is tracked separately in G01.03;
this command does not switch the production launcher from Python.

## Guarded building components

The internal building runtime combines exact native preview/map facts, complete
SQLite reservation recovery and one-attempt execution. Authority and building
writes use separately held typed capabilities; the read-only service has neither.
The executor records dispatch before effects and resolves lost replies through
attempt lookup and correlated observations. Receipts never establish completed
pawn construction. Unsuccessful outcomes remain distinct from unknown effects.

`Executor.Stop` cancels work and joins native dispatch plus receipt persistence.
A failed drain requires retaining the process lock, bridge and database until a
later successful drain. Native write acceptance and the explicit player runtime
entry point remain tracked under G01.06.

## Isolated building acceptance

Build `./internal/buildingruntime/cmd/buildingsmoke` for the native scenario host.
`--mode place --execute` requires absolute `--gabs`, `--config`, `--profile`,
`--state`, `--request` and fresh `--output` paths plus the configured `--game`.
The profile is the shared running game's real profile directory. The state file
must be new. The request is one official ProtoJSON `PlacementCandidate` naming
an observed site and material. Place performs one guarded dispatch and closes its
lease; acceptance of a receipt does not establish completed construction.

The scenario advances ordinary pawn work, then runs `--mode observe` with the same
state/profile/game paths and a new output directory, omitting `--execute` and
`--request`. This reopens the Go journal and observes the exact attempt without
acquiring a write lease. It succeeds only on correlated completed construction.
`--force-takeover` is for an explicitly coordinated GABS fixture handoff. Both
modes retain raw call evidence and a report; neither starts a game or advances ticks.

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
