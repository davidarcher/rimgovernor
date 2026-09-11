# Go controller development

The module provides local observation and explicit player services, version/help
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

## Routine policy components

`policy.DetectRoutine` evaluates typed survival facts and separate recovery
thresholds. Missing facts cannot certify foothold stability or clear active risk.
`RankDevelopment` preserves accepted shared-action commitments while ranking new
projects by deficit, player preference, native-tick age and selection hysteresis.
Unavailable methods can yield their slot within the same review. `StarterLayouts`
proposes bounded shelter and disjoint crop patches while respecting observed
geometry and player exclusions; proposals still require native preflight.

Fresh SQLite state stores maintained goals and method links to the same executable
plans. Reviews use a revision CAS; renewed deficits get a new method epoch after
observed recovery, preserving older plans and receipts. Cancellation and context
invalidation journal linked action cancellations atomically. Uncertain effects
remain observable, and goal guards apply at preparation and dispatch.

Plans persist a validated dependency graph. Preparation and dispatch require each
predecessor's observed completion in the current world; receipts cannot satisfy
dependencies. `Store.AdmitBuildingMethod` reserves a complete building method's
costs and footprints with its goal link and plan in one transaction, using the
same resource policy as Hands and authoritative competing reservations. Actions
remain pending until fresh Hands admission. Completed geometry yields to fresh
native placement observations rather than permanently claiming map coordinates.

`Store.ReviewRoutine` commits explicit deficit/unknown/recovered assessments and
food, wood and temperature latch history with all maintained-goal reviews in one
transaction. A review cursor rejects stale writers. Manual invalidates linked
work without needing valid native facts; world/direction changes and tick rewinds
give subsequent goals new identities while preserving old action evidence and
player cancellations. Unknown threats suspend new routine work until observed safe.
Manual and direction changes retain recovery targets; world replacement and tick
rewinds reset latches. Goal and plan catalogs remain bounded; history retirement
is still required before sustained routine operation.

`bridge.ReadColonyFacts` and `observation.DecodeColony` consume the typed native
core and planning geometry. Missing optional fields remain unknown, and a changed
tick/world is refused. Raw food runway never becomes policy `FoodDays`.
`observation.ObserveColony` brackets this read with paused identities at the same
tick and known native generation, rejects expired/cancelled reads, and publishes
no projection on failure. Callers must separately validate player direction.
Native acceptance compares the core and every selected cell against existing native
reads. To replay a retained official payload through Go and durable review, set
`RIMGOVERNOR_NATIVE_COLONY_CAPTURE` and run
`go test ./internal/observation -run TestColonyNativeCapture -v`.

`buildingruntime.RoutineReviewer.Step` serializes observation and durable review
through the existing player gate, rechecks authority after the read, and retains
unknown needs. `observation.ObserveRoutine` includes the typed emergency census
inside the same paused brackets. Medical/combat need counts use the shared emergency
rules, deduplicate patients/threats, and remain unknown on incomplete or conflicting
evidence. Manual and fresh acquisition invalidate previous routine reviews
without a native read. A disabled reviewer retires existing work without acquiring
authority. The reviewer has no independent background loop. A clock scheduler can
attach the same player's reviewer through `ClockSchedulerConfig.Routine`; it runs
after clock obligations drain and before a new window decision. Running epochs and
cleanup take precedence, and a failed review prevents a new window.

Add `--routine-reviews` to `serve --player-control --clock-control` to attach the
reviewer to the service clock worker. It uses the default routine thresholds and
requires typed colony observations. Startup remains disabled. This option journals
needs; it does not select methods or issue routine orders. Live service acceptance,
remaining fact projection, method selection and execution composition remain in G01.05.
For native review acceptance, pass `--routine-reviews` to
`scripts/native_go_clock_acceptance.py` through the documented container scenario
launcher with the private construction/interruption fixtures and verified Go binary.
It checks the SDK read trace, all fourteen durable goals, unknown food forecast,
Manual invalidation and absence of routine methods. Evidence-validator unit tests
do not substitute for that game run.

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

`GET /api/presentation/notifications` uses the same read contract and includes
letters, messages and alerts with fixed limits of 40, 12 and 40. Successful
responses preserve the canonical `NotificationsReply` sections, including a
section's explicit unavailable outcome. Viewing a notification does not
acknowledge it, dismiss it or resume play.

The dashboard displays these sections independently, retaining last-good data
with a stale indicator during failed refreshes. A changed world or session excludes
old results. Native notification production still requires game-level acceptance.

## Guarded player components

`rimgovernor serve --player-control --profile <absolute-game-profile>` selects
the building and temporary-draft service. Supply the same `--gabs`, `--config`, `--game`, `--state`,
`--listen` and optional `--assets` arguments as the observation service. The profile
must be the shared game profile, so another controller cannot acquire its process
lock. The service starts in Manual; it never restores a live lease from SQLite.

With built dashboard assets, player controls accept a building definition,
material, map coordinates and rotation, or an exact pawn ID for temporary drafting.
Submitting stores intent; enabling its plan separately acquires permission.
**Manual — stop orders** remains available while acquisition is pending. Both
forms share current permission and direction CAS. Form drafts and request IDs
survive background refreshes, and result checks only read the recorded request.
Player controls are hidden when the service runs read-only.

Submit a single building through `POST /api/buildings/plans`, then explicitly
acquire that plan through `POST /api/player/control/acquire`. Manual uses
`POST /api/player/control/manual` and stops local work before waiting for native
cleanup. These routes require JSON and the process token returned by
`GET /api/player/session` in the `X-RimGovernor-Player` header. Tokens remain in
memory. Requests bind exact colony/load/map identity and stable request IDs;
acquisition also checks the current direction.

Draft submission uses `POST /api/drafts/plans` with request ID, expected world and
`draft.pawnId`. No native token or claim is accepted from a player. Its result is
read through `GET /api/drafts/submission?requestId=...`. A completed standalone
draft plan releases its own temporary claim; this is not a persistent draft toggle.
Plan views show ordinary progress and cleanup status independently. See the
[fixed player API](../docs/developers/contracts/go-player-api.md) for exact shapes.

Both building admission checks require fresh, complete threat and basic pawn
health observations. Standing hostiles, hunting predators, critical medical needs
and unknown facts hold new orders. Previously issued attempts remain observable
while held; the controller does not release their reservations or invent a retry.

`bridge.Client.ReadPawns` reads 1–256 exact pawn IDs, including dead pawns, with
optional detail families disabled. It preserves native snapshot and draft-claim
availability. Missing pawns or unsupported claims cannot establish ownership or
release. Native draft service acceptance remains tracked in G01.07a.2f.4.

The owned-draft domain retains cleanup responsibility independently of ordinary
action completion. Native adapters provide temporary drafting, attempt reads and
exact-claim release as separate capabilities. Attempt reads carry original owner
and generation evidence without retaining a lease. Completion requires original
attempt attribution and fresh matching full-owner pawn observations. A verified
original receipt can also bind a historical claim after positive player ownership
replacement; cleanup then supersedes the old claim without changing player state.
A cleanup call uses the exact journaled pawn token and original claim.

The fresh Go schema stores building and owned-draft submissions under shared
request headers with separate typed payloads. Draft admission records the exact
pawn and snapshot token; progress and cleanup evidence commit atomically. Each
cleanup request receives a durable local sequence before release. Reopening the
database restores evidence, while dispatch still requires fresh runtime admission
and live permission. Older Go schema versions are rejected without migration.
Positive colony, load or map replacement can supersede an outstanding cleanup
obligation without claiming the original draft was acquired or released. Same-world
missing ownership cannot establish this transition.

The gated melee plan variant names an explicit preceding draft action for the
same pawn. Its typed admission records both pawn snapshot tokens and the exact
retained claim. Preparation and dispatch atomically require a completed, currently
owned prerequisite; generic preparation cannot bypass it. Historical admission
survives cleanup for receipt reconciliation. The melee bridge validates causal
completion separately from accepted jobs, and combat pawn reads preserve unknown
health and equipment facts. Deterministic admission requires a fresh complete
single-opponent census, healthy capable colonist, current ownership and guarded
native preview. `executor.NewWithMelee` uses two fresh inspections and the shared
writer. Reconciliation retains the original admission after Manual or cleanup;
accepted jobs never establish combat completion. Optional `SessionConfig.Melee`
requires complete draft capabilities and shares their joined cleanup. Player-service
submission and actual Go native defense acceptance remain separate gates.

Pure draft admission requires a healthy selected colonist, known unowned and
undrafted state, no forced or queued job, native eligibility and fresh complete
emergency observations. Known threats can admit this emergency action; unknown
facts hold it. `executor.NewWithDraft` prepares from fresh observations twice
before journaling dispatch. Drafting and cleanup share the building writer;
`CleanupDraft` remains available after ordinary `Stop`. Each call consumes one
fresh decision, preserving uncertainty and the exact persisted cleanup sequence.
An optional complete `SessionConfig.Draft` capability set composes draft execution
and cleanup into the existing session. The worker releases completed standalone
drafts and invalidated claims independently of ordinary action progress. An active
multi-action plan can retain a completed draft while its other work remains valid.
Manual and shutdown run a bounded cleanup sweep through the same writer; unknown
acquisition is observed before release, and an uncertain release remains retryable.
The explicitly selected player service supplies these complete capabilities;
production enablement still depends on the remaining G01 acceptance work.

An uncertain HTTP reply is resolved by reading its request ID through
`GET /api/buildings/submission?requestId=...` or
`GET /api/player/control?requestId=...`. Historical results are separate from
current permission. Repeating a control request never acquires another lease.

The worker observes unresolved attempts after restart, renews only an existing
lease, and does not start the game clock. Actual pawn work requires the player or
the supervised native scenario to advance time. Shutdown retains the native
connection, database and profile owner until all work has joined and native
authority cleanup is confirmed. A failed revoke remains retryable; a lost reply
is resolved by fresh observation before releasing the profile lock. After writers
drain, a fresh positive colony, load or map replacement can retire the old shutdown
target without revoking authority in the replacement world. Unavailable identity
retains ownership for another Close attempt.

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

The internal clock scheduler can perform one finite healthy-colony scheduling step
through the shared session. Its durable window admission binds current review and
native cursor evidence to dispatch, and repeated unchanged decisions retain their
request identity. An optional attached clock worker runs event polling, epoch
renewal and scheduling independently, with joined retryable shutdown. Add `--clock-control` to `serve --player-control` to attach this worker. It uses normal speed, 600-tick windows and a 30-second owned lease; startup remains disabled. Interruptions hold execution without automatic acknowledgement. The player panel displays interruption review and explicit acknowledgement; this never enables orders. Event-history maintenance checkpoints reviewed evidence while retaining interruption holds and acknowledgement replay. Actual Go clock acceptance remains pending. See the
[clock recovery contract](../docs/developers/contracts/go-clock-recovery.md).

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
