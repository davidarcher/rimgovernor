# Go controller development

The module provides local observation and explicit building services, version/help
and offline replay.
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

The presentation read interface uses `GET /api/presentation/camera`,
`/api/presentation/selection` and `/api/presentation/colonists`, without query
parameters or request bodies. The roster is limited to the current map. Successful
responses use the canonical presentation reply's ProtoJSON shape, including
optional presence and decimal strings for 64-bit integers. A service without the
provider returns 404; unavailable or stale observations use the local API's
sanitized error shape. These reads cannot select, move the camera or send input.

## Guarded building components

`rimgovernor serve --building-control --profile <absolute-game-profile>` selects
the building service. Supply the same `--gabs`, `--config`, `--game`, `--state`,
`--listen` and optional `--assets` arguments as the observation service. The profile
must be the shared game profile, so another controller cannot acquire its process
lock. The service starts in Manual; it never restores a live lease from SQLite.

With built dashboard assets, the Building controls panel accepts a definition,
material, map coordinates and rotation. **Submit building plan** saves the request;
**Enable this plan** separately acquires permission. **Manual — stop orders**
remains available while acquisition is pending. Drafts and request IDs survive
background refreshes, and result checks only read the recorded request. The panel
is hidden when the service runs read-only.

Submit a single building through `POST /api/buildings/plans`, then explicitly
acquire that plan through `POST /api/buildings/control/acquire`. Manual uses
`POST /api/buildings/control/manual` and stops local work before waiting for native
cleanup. These routes require JSON and the process token returned by
`GET /api/buildings/session` in the `X-RimGovernor-Player` header. Tokens remain in
memory. Requests bind exact colony/load/map identity and stable request IDs;
acquisition also checks the current direction.

Both building admission checks require fresh, complete threat and basic pawn
health observations. Standing hostiles, hunting predators, critical medical needs
and unknown facts hold new orders. Previously issued attempts remain observable
while held; the controller does not release their reservations or invent a retry.

An uncertain HTTP reply is resolved by reading its request ID through
`GET /api/buildings/submission?requestId=...` or
`GET /api/buildings/control?requestId=...`. Historical results are separate from
current permission. Repeating a control request never acquires another lease.

The worker observes unresolved attempts after restart, renews only an existing
lease, and does not start the game clock. Actual pawn work requires the player or
the supervised native scenario to advance time. Shutdown retains the native
connection, database and profile owner until all work has joined.

The internal building runtime combines exact native preview/map facts, complete
SQLite reservation recovery and one-attempt execution. Authority and building
writes use separately held typed capabilities; the read-only service has neither.
The executor records dispatch before effects and resolves lost replies through
attempt lookup and correlated observations. Receipts never establish completed
pawn construction. Unsuccessful outcomes remain distinct from unknown effects.

`Executor.Stop` cancels work and joins native dispatch plus receipt persistence.
A failed drain requires retaining the process lock, bridge and database until a
later successful drain. [Native service acceptance](../docs/developers/testing/construction-recovery.md#go-http-building-service)
verifies ordinary pawn completion and disabled same-database restart through the
HTTP service. Supervised Go clock control remains a separate G01.10 integration.

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
acquiring a write lease. It normally requires correlated completed construction.
Use `--expected-outcome cancelled` or `interrupted` to require that exact observed
terminal outcome instead; unknown or pending evidence never passes.
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
