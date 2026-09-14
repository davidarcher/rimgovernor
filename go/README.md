# Go controller development

The module provides local observation and explicit player services, version/help
and offline replay.
`launch.cmd`/`launch-go.ps1` and the `go-controller` Docker target (below) start
this binary directly; there is no Python interpreter, venv or `controller/`
package anywhere in the repository as of G01.13
([issue #33](https://github.com/davidarcher/rimgovernor/issues/33)). Go starts
with fresh state; importing historical Python databases and matching historical
save formats are not rewrite gates.

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

## Go launch and packaging, the production default (G01.11, G01.12)

`go/cmd/rimgovernor`'s `serve` command is a real standalone binary: it needs no
Python interpreter, venv or `controller/` package, and `launch.cmd` now starts
it by default. It covers building, owned draft/melee execution, the routine
planner families wired in `serve_clock.go`, lifecycle save/load, presentation
and media (camera, pawn images, notifications, video streaming), and the ~30
typed player submission endpoints under `internal/httpapi/player.go` (research,
tend, rescue, caravan departure/hold/route, husbandry, recovery service, bed
assignment, movement, building temperature, surgery, quest accept/fulfill,
settlement gift, trade, zone create/edit, build/adopt room, cancel/relocate
construction and more). The dashboard detects the Go backend
(`GET /api/health` reports `backend: "go"`) and serves a dedicated observation
and structured-control UI (`ObservationDashboard`/`PlayerControls`) instead of
the Python controller's free-text chat UI: natural-language player chat is
decoded end to end by `internal/interpreter/decode.go` but is **not yet wired
into `serve`'s HTTP server** (`POST /api/chat` returns 501) — see
[the migration review](../docs/developers/go-migration-review.md) for exact
remaining boundaries. Caravan departure and travel, quest accept/fulfill,
settlement gifting and trade all have native acceptance harnesses verified
against a real headless RimWorld instance (G01.07f;
[issue #28](https://github.com/davidarcher/rimgovernor/issues/28)).

**Windows**, from the repository root:

```powershell
.\launch.cmd                    # production default: player control, browser opens
.\launch-go.ps1 -ReadOnly       # observation-only dashboard, no writes
.\launch-go.ps1 ...              # add --routine-*-plans/--routine-methods flags for routine execution
```

`serve --player-control --clock-control --routine-reviews --routine-methods`
composes every implemented and tested routine planner family (G01.10) instead
of requiring an operator to enumerate the ~30 individual
`--routine-*-plans`/`--routine-methods` flags: naming zero of those flags turns
all of them on. Naming even one opts back out to exactly the named families,
for targeted/debug runs. `GET /api/routines` reports which families a running
process composed, whether reviews/methods execution are enabled, and the
durable review cursor's last reviewed tick — a runtime-queryable view of the
capability table below. Startup reconciliation (durable holds and goal
admission on process start) is generation/goal-keyed rather than per-flag, so
it already covers whatever set of families a given invocation composes.

**Known defect:** targeted G01.12 acceptance of `--clock-control
--routine-reviews` found the native clock can fail to ever start
(`clock_start` never issued despite an outstanding work plan and acquired
control) — see [issue #45](https://github.com/davidarcher/rimgovernor/issues/45).
`--player-control` alone (this launcher's default, no `--clock-control`) is
unaffected. Related: [issue #42](https://github.com/davidarcher/rimgovernor/issues/42)
covers the clock restarting in short, thrashing bursts once running.

It builds the binary if missing, builds the dashboard's static assets
(`dashboard/dist`, built with `pnpm`, no Python) and requires prepared
GABS/config/profile inputs (`docs/players/setup.md`) — GABS is a native
executable dependency of the controller itself, not a Python one.

**Docker**: `containers/Dockerfile`'s `go-controller` target builds the Go
binary and packages the same dashboard assets, with no Python runtime or
`pip install` step:

```powershell
docker build -f containers/Dockerfile --target go-controller -t rimgovernor-go:local .
docker run --rm rimgovernor-go:local version   # confirms the image without game files
docker run --rm rimgovernor-go:local help
```

`containers/go-controller.compose.yaml` runs a real `serve --read-only`
session with bind-mounted GABS/config/state inputs, following the
`containers/compose.yaml` worker's bind-mount conventions. Because `--listen`
only accepts a loopback address, it uses `network_mode: host` instead of
bridge port publishing (the compose file's comments explain why and its
Windows/macOS Docker Desktop caveat).

**No licensed game files here**: without a real GABS build and prepared save,
`serve` fails at the native bridge handshake (`bridge transport failure:
initialize: ...`). That failure, `go build ./...`, `go vet ./...`,
`go test ./cmd/...` and an image build/`docker run` reaching that same clean
failure are compilation/protocol/wiring checks, not gameplay evidence — they
confirm the packaging path is wired correctly, not that a colony runs.

**Remaining scope**, tracked separately from this item:
- Natural-language player chat and local-model command interpretation:
  `interpreter/decode.go` decodes build, research,
  tend, rescue, draft, caravan departure/hold/route, husbandry, recovery
  service, bed assignment, movement, building temperature, surgery, quest
  accept/fulfill, settlement gift, zone creation, zone edit (add/remove
  cells, delete; crop/filter edits are deferred pending SettingsField zone
  evidence coverage), population policy, expedition policy, per-pawn
  population decision, per-resource production policy
  (`modify_resource_policy`/`set_resource_reserve`), maintained goal
  activation/cancellation (`create_goal`/`cancel_goal`), room shells
  (`build_room`), room adoption (`adopt_room`), construction cancellation
  (`cancel_construction`), construction relocation
  (`relocate_construction`) and read-only world evaluation
  (`evaluate_world`) proposals, but
  is not yet wired into the Go binary's serve loop) — G01.08.
  `set_population_policy` is the first interpreted command that is colony
  configuration rather than a plan of native actions: it issues no native
  call, so it carries no `domain.Action`, no bridge boundary and no
  executor state machine. `interpreter.Proposal` returns the typed
  `domain.PopulationPolicy` with an empty plan, and
  `store.SubmitPopulationPolicy` keeps request-ID replay safety plus one
  current value per colony/load/map, outside the plan/action tables —
  `POST /api/player/population-policy/replace`,
  `GET /api/player/population-policy?colonyId=&loadToken=&mapId=` and
  `GET /api/player/population-policy/submission?requestId=`.
  `set_expedition_policy` follows the same configuration-only path, with one
  difference: it is a **partial patch**, mirroring Python's
  `model_dump(exclude_unset=True)` merge. A request names only the limits it
  changes; `store.SubmitExpeditionPolicy` merges it over the limits in force
  (or over `domain.DefaultExpeditionPolicy` for a world that has never had
  one), validates the merged whole including the
  `minimumDestinationTemperature <= maximumDestinationTemperature` check, and
  stores both the patch and the policy it produced so a replay reports what
  that request did without re-merging it onto a newer current value.
  `interpreter.Proposal.ExpeditionPolicy` therefore carries a
  `domain.ExpeditionPolicyPatch`, not a whole policy —
  `POST /api/player/expedition-policy/update` (named `/update`, not
  `/replace`, precisely because unnamed limits are preserved),
  `GET /api/player/expedition-policy?colonyId=&loadToken=&mapId=` and
  `GET /api/player/expedition-policy/submission?requestId=`.
  `domain.ExpeditionPolicy` is the writable player-facing whole and is
  deliberately **not** unified with `policy.CaravanDeparturePolicy` or
  `policy.WorldEvaluationPolicy`, which stay hardcoded read-only subsets
  consumed by internal admission/evaluation functions; making those read
  this store is a separate refactor.
  `set_population_decision` is the third record-only command and the first
  that names an observed entity: one player-sourced direction per pawn
  (`rescue`, `capture`, `recruit` or `ignore`), Python's per-pawn
  `ColonyGoal(source='PLAYER', …)` keyed by `population.goal_id(pawn)`. The
  pawn is bounded against `Snapshot.Pawns` like draft/tend/rescue;
  `store.SubmitPopulationDecision` keeps request-ID replay safety plus one
  current directive per colony/load/map/pawn, and requires an established
  population policy for the three custody decisions (reporting `ErrNotFound`
  otherwise) while `ignore` never does, matching the Python handler —
  `POST /api/player/population-decision/replace`,
  `GET /api/player/population-decision?colonyId=&loadToken=&mapId=` and
  `GET /api/player/population-decision/submission?requestId=`.
  It carries no `domain.Action` on purpose: rescue and capture already have
  one-shot player commands (`rescue`) and their own autopilot upkeep
  (`policy.CustodyDeficit`/`SelectCustodyMethod` dispatched by
  `buildingruntime.RoutinePopulationCustodyPlanner`), and recruitment has
  `RoutinePrisonerInteractionPlanner`. Teaching that autopilot custody
  selection to prefer or suppress individuals named here is a separate,
  behaviour-changing slice.
  `modify_resource_policy` and `set_resource_reserve` are the first player
  commands that are *both* persistent configuration *and* a real native
  dispatch. They share one handler, one persistent per-resource policy and one
  `SetProductionPolicy` write, exactly as Python's shared
  `isinstance(request,(ModifyResourcePolicy,SetResourceReserve))` branch does.
  Each command patches one half of one resource — spending preserving the
  reserve, reserve preserving the spending — so
  `interpreter.Proposal.ResourcePolicy` carries a
  `domain.ResourcePolicyPatch`, not a whole policy, for the same reason
  `set_expedition_policy` does: the interpreter does not hold the other
  resources' established values. `store.SubmitResourcePolicy` merges the patch
  over the resource's directive in force (or `{reserve 0, spending normal}`),
  folds the world's **whole** set into one `domain.ProductionPolicy`
  (`ResourceProductionPolicy`: strictly positive reserves become floors,
  any non-`normal` spending becomes a stopped def — `production_budgets`
  verbatim) and commits a one-action plan through `createPlan` directly. That
  is deliberately the `CreateZone` shape, not a new pipeline: the entire
  autopilot production-policy stack (`domain.ProductionPolicyAction`,
  `bridge/production_policy*.go`, `executor/production_policy*.go`,
  `store.PrepareProductionPolicy`) is reused **unchanged**, and the
  autopilot-goal-bound `CommitGoalMethod` admission
  `RoutineProductionPolicyPlanner` commits through is bypassed rather than
  widened. The `Commitments`/`Drills` rows another system owns are still read
  fresh and resent verbatim at dispatch, so a player patch can never clobber
  them. `resource` is bounded against `Snapshot.ResourceDefinitions`, the Go
  form of the Python handler's observed `known` set —
  `POST /api/player/resource-policy/update`,
  `GET /api/player/resource-policy?colonyId=&loadToken=&mapId=` and
  `GET /api/player/resource-policy/submission?requestId=`. Python's second
  source of floors (outstanding construction-bundle costs from
  `plan.control['costs']`, sent as `commitments`) stays unported, the same
  disclosed narrowing `policy.ProductionFloors` already carries.
  `create_goal` and `cancel_goal` add no goal machinery at all; they make the
  machinery that already exists reachable from a player path. Every kind
  Python's `CreateGoal` whitelists is already an autopilot-managed goal with
  fixed `policy.GoalID` constants and deterministic deficit assessment
  (`policy.DetectRoutine`), and `domain.Goal` already carried a `PlayerGoal`
  source. So activation is exactly: create (or reuse) a player-sourced
  `domain.Goal` for that kind and review it at `NeedDeficit` — explicit player
  direction *is* the deficit assertion, overriding what the autopilot's own
  review currently observes. `store.SubmitGoalCreate` keeps request-ID replay
  safety plus one goal identity per colony/load/map/kind. Reusing a live goal
  lets `domain.ReviewGoal`'s own epoch rule do what Python's
  `reopen_methods`/`attempts` does; a cancelled or invalidated goal is never
  resurrected (cancellation is terminal in `ReviewGoal`), so the binding moves
  to a fresh identity and the superseded goal keeps its history. Nothing about
  the autopilot's lifecycle is widened: `admitRoutineDevelopment` already
  exempted non-autopilot goals from the development arbitration gate,
  `routineCommitments` already counted `PlayerGoal` open work against the
  autopilot's concurrent-project capacity, and `retireRoutineGoals` only ever
  retires invalidated autopilot goals. `cancel_goal` calls the unchanged
  `store.CancelGoal` body through `CancelPlayerGoal`, which adds a world bound
  and the same local CAS revision — method cancellation and progress-journal
  cancellation are untouched. Python's fuzzy `resolve_goal_id` is deliberately
  **not** ported: the identity is bounded against `Snapshot.ObservedGoals`
  exactly as `edit_zone` bounds its `zoneId`, so a prefix or a kind name is
  refused rather than guessed —
  `POST /api/player/goals/activate`, `POST /api/player/goals/cancel`,
  `GET /api/player/goals?colonyId=&loadToken=&mapId=` and
  `GET /api/player/goals/submission?requestId=`.
  **Deferred, deliberately:** Python's per-goal `target` configuration —
  `EnsureFoodSupply`'s `food_days`, `MaintainResource`'s
  `resource`/`quantity`/`deep_extraction` and `MaintainWaste`'s
  `unwanted`/`bury`. `domain.Goal` has no free-form target dict, and the Go
  equivalents of these values (`policy.RoutinePolicy`'s
  `FoodTargetDays`/`FoodMinDays` and `ResourceTargets`) are process-level
  operator CLI flags captured once when `NewRoutineReviewer` is constructed,
  not per-world stored state; `MaintainWaste` has no Go target at all, because
  native authority owns waste eligibility (`policy.WasteItem.Eligible`).
  Making any of them player-settable means converting `RoutinePolicy` from
  immutable process config into per-world stored config the routine review
  re-reads each pass — a change to the autopilot's own configuration model
  larger than this command, and one autopilot does not consume from the store
  today. `MaintainWaste` also still has no composed dispatch method (G01.07e);
  activating it makes the goal visible and player-sourced, not dispatchable.
  Both gaps are separately tracked.
  `build_room` is the first player command that is construction-shaped. It adds
  no native plumbing: the placement dispatch, preview, receipt verification and
  executor state machine the autopilot's own shelter routine uses
  (`bridge.PreviewBuilding`/`placement.go`, `domain.BuildingAction`,
  `buildingruntime.RoutineBuildingPlanner`) already do everything a room needs.
  What was missing in Go was the geometry: Python's `spatial.room_placements`
  expands a `RoomShell` rectangle into the perimeter-wall-plus-one-door
  placement list, and nothing here had ported it (the routine shelter planner
  generates its own fixed starter perimeter inline, always a south door, always
  `Wall`/`Door` in `WoodLog`). `domain.RoomShell` now carries that port —
  `Door()` is `spatial.room_entrance`'s side midpoint and `Placements()` is
  `room_placements`, door first, then the remaining perimeter in Python's
  `RoomBounds.cells()` order, walls facing north, interior cells deliberately
  absent (a shell, not a floor). Unlike the Python contract it holds one
  material rather than a preference list, because `domain.Building` resolves
  exactly one stuff and later entries could never be dispatched. Submission
  follows `SubmitZoneCreate`'s shape — player-command-driven, bypassing the
  autopilot-goal-bound `admitZoneMethod`-family gates, committing its own plan
  immediately — with one difference: that plan holds *many* actions, one
  ordinary `BuildingAction` per perimeter cell (252 at the 64×64 maximum, within
  the 256-action bound). That is also why `interpreter.Proposal` carries
  `BuildRoom`/`BuildRoomIntent` instead of a `Plan`: `Config.MaxActions` is at
  most 16 and even a 7×7 room expands to 24 placements, so the shell travels and
  is expanded once at submission. Wall/door definitions and material are bounded
  against `Snapshot.Definitions` exactly as a `build` placement's, and every
  expanded cell against `Snapshot.Cells` exactly as a `create_zone` footprint's —
  `POST /api/build-rooms/plans` and
  `GET /api/build-rooms/submission?requestId=`.
  `store.LookupBuildRoomIntent` resolves a world-scoped `intent_id` back to its
  committed plan and placements, the Go stand-in for Python's
  `plan.control['player_intents']` mapping.
  `cancel_construction` withdraws one such construction and is the first player
  command that is *un*-construction-shaped. Unlike `build_room` it does add new
  native plumbing, because nothing in Go had ever issued the
  `operations.proto` `CancelConstruction` operation (as with `EditZone`, the
  proto existed with zero Go callers; unlike `CreateZone`/`BuildRoom`, which
  reused placement plumbing): `domain.ConstructionCancel` and a new
  `ConstructionCancelAction`, `bridge/construction_cancel.go`,
  `store/constructioncancel` with its typed admission, and
  `executor/construction_cancel.go`. The problem it solves is that a pending
  construction order has **no stable native identity** — a blueprint's thing ID
  changes when it becomes a frame and vanishes when it is built, and
  `ProgressView.Construction` is only populated for *completed* buildings — so
  the action carries the placement's stable `(defName, cell, stuff)` triple and
  the executor resolves it to a live thing ID plus CAS token at inspection, via
  `home/list_buildings` over a 1×1 region filtered to `blueprint`/`frame`.
  That is Python's `construction_cancellation.capture_targets` query, ambiguity
  refusal included. Cancellation covers the **whole named intent**, matching
  Python's one-`intent_id`-per-call semantics (there is no per-placement
  granularity on either side); `store.LookupBuildRoomIntent` above is the hook,
  and per placement the store decides what is still cancellable: never
  dispatched or refused means nothing was placed, completed/cancelled/
  unsuccessful is preserved untouched, and a dispatch whose receipt is missing
  or unknown refuses the whole request ("uncertain receipt; reconcile it before
  cancellation"). If *every* placement has already resolved that is a success,
  not an error: the submission commits with `observedAbsent` true and no plan
  at all, Python's `'observed_absent': True` result. That outcome is why this
  family keeps its own `cancel_construction_submissions` table with nullable
  `plan_id`/`action_id` rather than the shared `submissions` header, which
  requires exactly one committed action per request. The executor mirrors
  `zone_edit.go`'s two-phase shape (inspect twice, typed-prepare, dispatch,
  verify the order is absent or `CONSTRUCTION_STAGE_CANCELLED`) with one extra
  terminating branch: a target already gone at inspection cancels the action
  outright instead of dispatching against a stale identity, so a replacement
  placed since is never destroyed. In the interpreter the intent is bounded
  against `Snapshot.ObservedConstructionIntents` exactly as `cancel_goal` bounds
  its goal; Python's `validate_player_authorization`/`requests_relocation`
  chat-message scanning is deliberately **not** ported, being specific to that
  conversational flow —
  `POST /api/cancel-constructions/plans` and
  `GET /api/cancel-constructions/submission?requestId=`.
  `adopt_room` claims an **already-built** native room — walls, door and roof
  already complete, whether the autopilot built it, an earlier `build_room` did,
  or the player did by hand — as satisfying `EnsureInitialShelter`. It is the
  only command in this family that issues no native call *and* opens no work:
  Python's own reply is "Existing construction preserved; no new construction
  issued", and the Go reply repeats it verbatim. It reuses the `RoomBounds`,
  `Rotation` entrance and `ValidateRoomIntent` vocabulary `build_room`
  introduced; `domain.RoomAdoption` adds only what adoption needs, which is
  Python `AdoptRoom`'s `geometry` validator ported whole, nonrectangular support
  included: `interior_cells`/`entrance_cell` are supplied together or not at
  all, every cell lies strictly inside the inspected bounds with no duplicate,
  the entrance cell is outside the interior with the cell beyond it also outside
  (a boundary facing out, not an interior aisle), and the interior is one
  connected region — `connectedCells` is the port of `shell_site.connected_cells`.
  The rectangular form derives the same side-midpoint door `RoomShell.Door`
  places, so adopting a room this controller built names that room's own door.

  **Goal completion with evidence** is the design question this slice answers.
  `domain.Goal` has no evidence or target field — the same constraint the
  `create_goal` slice documented for its own per-goal configuration — so
  Python's `goal.evidence['adoption']` (native room id, cell count, native role)
  has nowhere to live on the goal. Rather than widen a type the whole autopilot
  goal lifecycle validates, the evidence is stored beside the goal in the
  adoption's own table, the config-only shape `set_population_policy` and
  `modify_resource_policy` already use. The goal itself is completed through the
  **unchanged** lifecycle: `Goal.Validate` refuses `GoalSatisfied` unless
  `Need == NeedRecovered` and `RecoveryObserved`, and `domain.ReviewGoal` reaches
  exactly that from a fresh goal in one call — reviewing at `NeedRecovered` with
  no open work sets both together. So adoption is `NewGoal` then one
  `ReviewGoal(…, NeedRecovered, false, false)`, the player's explicit inspection
  standing as the recovery observation exactly as `create_goal` treats their
  direction as the deficit assertion. No invariant is weakened and no new status
  is invented. A shelter goal with **open work** is refused rather than completed
  ("cancel it before adopting a finished room"): a room still being built is not
  a room the player can have inspected as finished. `player_goals` is now the one
  per-world, per-kind binding shared by both commands (it carries a `command`
  column and no foreign key, since `request_id` may name either request table),
  so adoption completes the same goal `create_goal` would have activated instead
  of leaving a second, contradictory player goal. `adopted_shelters` is Python's
  `plan.control['preferred_shelter']`; its
  `suppressed_goals['EnsureInitialShelter']` clear has no counterpart, this
  controller having no goal-suppression map. Python's "existing construction
  history must remain intact" refusal is ported against
  `build_room_submissions`, which is where an intent's construction history
  lives here —
  `POST /api/player/adopt-room/claim`,
  `GET /api/player/adopt-room?colonyId=&loadToken=&mapId=` and
  `GET /api/player/adopt-room/submission?requestId=`.

  **Deferred in `adopt_room`, deliberately:** Python's submission-time
  re-observation (`room_adoption.verified_room`/`verify_entrance`) is **not**
  ported, for two independent reasons. First, there is no seam for it: a
  `buildingruntime.Player` holds a journal and a `WorldSource` (colony/load/map
  identity only), never a bridge client, so *every* player command in this
  family takes already-observed geometry on the player's word — `build_room`
  says so explicitly — and giving this one command native reads would widen the
  whole player surface. Second, the pawn-route half cannot be built at all yet:
  `home/spatial_access` exists natively only as the legacy tool
  (`integrations/rimgovernor-native/src/Bridge/SpatialAccessTool.cs`), and
  although `observations.proto` declares `ReadSpatialAccess`, no typed
  `rimgovernor/observations_read_spatial_access` tool is registered, so a Go
  bridge adapter for it would call nothing. The roof/enclosure and doorway
  checks *are* portable against existing typed reads
  (`rimgovernor/observations_list_rooms` with `include_boundary`/
  `include_outdoors` gives `RoomState.proper_room`, `open_roof_count`,
  `psychologically_outdoors` and `doorway`; note the typed contract has no
  `doorDef`, so the `doorway` flag plus a completed non-blueprint/non-frame
  building at the cell is the typed equivalent of Python's def-name match), but
  wiring them with no caller would be dead code. What the store *does* enforce
  is that the player's own evidence is self-consistent: the reported native cell
  count must equal the interior they described. Re-verification belongs with a
  reviewer that already holds a bridge client — the port of Python's
  `validate_adoption`, which re-runs `verify_entrance` on each review — and is
  tracked separately.

  `relocate_construction` moves an already-named construction somewhere else,
  and is the first command here that is two of the others at once: it names an
  existing intent the way `cancel_construction` does *and* carries a whole shell
  the way `build_room` does, and bounds both halves accordingly (the intent
  against `Snapshot.ObservedConstructionIntents`, the replacement's definitions,
  material and every expanded cell against `Snapshot.Definitions`/`Cells`). The
  design question it answers is **ordering across two commands**, and the answer
  is `domain.ActionDependency`, the generic action-ordering primitive
  `domain.NewPlan` has always accepted and nothing had used across a command
  boundary: it is plan-local, so the withdrawal of the old orders and the
  placement of the new ones travel in **one** committed plan rather than two,
  with `PlanSpec.CheckDependencies`/`ErrDependency` holding every replacement
  placement out of admission until the withdrawals have completed in the current
  world. That is Python's `PlanStep.after=[{'step':removal_id,'when':'complete'}]`
  with the same coarse granularity: the withdrawals run in a chain and every
  placement requires the last of them, mirroring Python's monolithic
  removal-then-replacement rather than pairing cells — pairing each new cell to
  each old one is 63504 edges at the 64×64 maximum, against domain's 4096 bound,
  where the chain is at most 503.

  There are two paths, Python's own split. On the **fast path** nothing of the
  original was ever dispatched, so there is no native order to withdraw and the
  plan holds the replacement's placements alone (`fastPath` on the wire); Python
  re-invokes `BuildRoom` under the same `intent_id` there, and this is the same
  thing without the re-entrancy. On the **combined path** something was issued,
  and then *every* issued placement must still be cleanly withdrawable — a
  completed or independently removed one refuses the whole relocation rather
  than moving half a room, Python's "completed or missing construction is
  preserved", reusing `cancellableTargets` unchanged from the cancellation
  slice. Both paths additionally cancel the source's **never-dispatched**
  placements in this controller's own journal, which is the one respect in
  which relocation is more than cancel-plus-build: `cancel_construction` leaves
  them alone because withdrawing native orders is all it promises, but a
  relocation that left them live would go on to build the old room beside the
  new one.

  **Intent supersession is append-only.** `build_room_submissions` is never
  rewritten — that row is what `request_id` replay resolves to, and rewriting it
  would break BuildRoom's own guarantee — so each relocation instead records the
  plan it supersedes in `relocate_construction_submissions`, unique per
  `(world, intent, source_plan)`, and `store.LookupConstructionIntent` walks that
  chain forwards to the current head. `SubmitCancelConstruction` now resolves
  through it rather than calling `LookupBuildRoomIntent` directly, so cancelling
  a relocated intent withdraws where the construction stands, not where it was
  first ordered; with no relocation on record the head *is* the build-room
  submission, so nothing changes for an intent that was never moved —
  `POST /api/relocate-constructions/plans` and
  `GET /api/relocate-constructions/submission?requestId=`.

  **Simplified, deliberately:** Python's replacement is a `RoomShell | Buildings`
  union and its handler refuses a replacement of a different kind than the
  original ("Relocation must preserve the construction kind"). Here the
  replacement is a room shell and nothing else, because `build_room` is the only
  player command in this family that issues construction, so the kind can never
  differ and the check is a tautology the Go decode already enforces at its field
  type. `PlaceBuildings`, Python's raw-placement-list sibling, is not in this
  issue's tracked backlog and is not built here; if it ever lands, the kind check
  comes back with it. Python's "dependent work references this construction"
  refusal *is* ported (`dependentOnSource`) but is currently **vacuous**:
  `ActionDependency` is plan-local, `build_room` commits its plan with no
  dependencies at all, and a relocation's own edges point at its withdrawals
  rather than at its placements, so nothing can hold such an edge today. It is
  kept because it is two loops over data already in hand and it is what makes
  adding a dependent command later safe by default. Python's
  `validate_player_authorization`/chat-revision re-check is not ported, for the
  same reason the cancellation slice did not port it.
- Media/camera/portrait/video/recording and save/load are closed and wired
  into `serve`'s HTTP server — G01.09/G01.10 (`internal/httpapi/presentation.go`,
  `presentation_media.go`, `video_stream.go`, `lifecycle.go`).
- World progression remaining scope: closed, except the documented
  `SetTradeLines`/`AcceptTrade`/`EndTrade` acceptance-harness gap below —
  G01.07f ([issue #28](https://github.com/davidarcher/rimgovernor/issues/28)).
  `evaluate_expedition` (`controller/rimgovernor/expedition_policy.py`) is
  now ported: `WorldRoute` (`contracts/proto/observations.proto`) and
  `RoutePreparation` (`contracts/proto/operations.proto`) carry destination
  temperature, hostility, goodwill and faction id, populated in
  `NativeCaravanCatalog.RouteFacts`/`NativeCaravanOperations.Preview`
  (`integrations/rimgovernor-native/src/Bridge/Protocol`) and enforced by
  `policy.EvaluateCaravanDeparture` (`go/internal/policy/caravan_departure_admit.go`)
  as `CaravanDestinationTemperatureOutOfRange`, `CaravanDestinationHostile`
  and `CaravanDestinationGoodwillInsufficient` refusals — this vertical only
  ever represents a 'form' action, so these always take Python's
  non-'return' (blocking) branch. `route.get('foodRotDays')` is threaded
  through as `CaravanDepartureFacts.RouteFoodRotDays` but, matching Python,
  stays a non-blocking, informational-only signal: it never produces a
  refusal here.
  Caravan departure/travel, quest accept/fulfill, settlement gift and trade
  themselves are closed, each with domain/policy/store/executor/bridge and
  httpapi wiring. Trade's `set_lines`/`accept`/`end` sub-operations resolve
  their open session by one of two durable bindings, never a native read
  (`executor.resolveTradeDependency`): the plan's own same-plan
  `ActionDependency`, or failing that a cross-plan
  `trade_session_references` row bound to the dependent action's own
  identity at submission time (`go/internal/store/trade_session_reference.go`).
  Either way resolution ends in the same never-plan-scoped
  `LookupTradeSession` (`go/internal/store/trade_session.go`), so an Open
  that has not been observed complete still holds on
  `TradeSessionUnresolved` rather than guessing a session.
  Through the direct single-action submission surface
  (`POST /api/trades/plans`, mirroring `SubmitQuestFulfill`'s shape) only
  `trade_open` resolves end to end, since a lone `set_lines`/`accept`/`end`
  carries neither binding. The whole four-phase negotiation instead runs as
  the player `trade_economy` command (`POST /api/trade-economies/plans`,
  looked up at `/api/trade-economies/submission`): it ports Python's
  `select_trade`/`economic_reserves` (`policy/trade_select.go`) over a
  complete paginated sheet read (`bridge.ReadTradeSheet`) and drives
  open → set_lines → accept/end as separate one-action plans stitched by
  the cross-plan binding above (`store/trade_negotiation.go`,
  `buildingruntime/trade_economy.go`). It is an optional httpapi surface
  like `WorldEvaluation`, answering 404 when not enabled.
  Caravan departure and travel, quest accept, settlement gift and
  trade open now all have native acceptance harnesses
  (`nativeaccept/cmd/caravandepartureaccept`,
  `nativeaccept/cmd/caravancontrolaccept`, `nativeaccept/cmd/questacceptaccept`,
  `nativeaccept/cmd/settlementgiftaccept`, `nativeaccept/cmd/tradeaccept`),
  each verified end to end against a real headless RimWorld instance:
  native admission, CAS-token refusal (stale/corrupted identity, and for
  trade an owner-conflict re-open while a session is still live), preview
  non-mutation, replay idempotency and durable receipt lookup. Per the
  direct-submission limitation above, `tradeaccept` exercises OpenTrade
  alone; `SetTradeLines`/`AcceptTrade`/`EndTrade` still have no acceptance
  harness, since a raw wire harness cannot honestly submit them as a
  standalone action without fabricating one of the two session bindings
  they require — that remains a documented gap, not a closed vertical. The
  read-only `evaluate_world` advisory (caravan recovery, quest resource
  deficits/carried cargo) is also in Go, served at
  `GET /api/player/world-evaluation` behind `--world-evaluation`
  (`buildingruntime.WorldEvaluation`, `policy.EvaluateWorld`), and is now
  reachable through the player-command path as well: `evaluate_world` is a
  decodable interpreter command that sets `interpreter.Proposal.EvaluateWorld`
  and carries nothing else. That flag is the emptiest proposal in the family —
  no plan, no configuration, no named entity — because Python's `EvaluateWorld`
  contract is a bare kind discriminator with no parameters. The evaluation
  itself is deliberately not recomputed there: the interpreter holds no native
  surface and no expedition policy, so a consumer that sees the flag answers
  the player from the same `buildingruntime.WorldEvaluation.Read` the GET route
  already calls. The GET route therefore stays, and is not redundant — it is
  the single place the advisory is computed, and the only one with a native
  surface to compute it from. There is no `/submission` counterpart for the
  same reason there is no `Acknowledge`: the command writes nothing, so there
  is no state to make consistent under a CAS token.
- Most routine workflows beyond construction: cooking/butcher bill execution,
  care/tend/rescue/defense dispatch beyond compiled plans, and other
  non-building executable actions — G01.05/G01.07a–c/e (many `--routine-*-plans`
  flags compile shared plans today without an executable action family yet).
  Equipment wear/replace, gear/medical production, research selection and
  resource-target production dispatch through Go are closed (G01.07d).
- Native scenario/acceptance tooling: G01.13
  ([issue #33](https://github.com/davidarcher/rimgovernor/issues/33)) removed
  the Python production runtime, `controller_tests/` and the Python
  scenario/acceptance scripts entirely, along with the Docker
  `controller-tests`/`tests`/`worker` build targets that ran them. Native
  acceptance tooling is being rebuilt in Go — see
  [issue #38](https://github.com/davidarcher/rimgovernor/issues/38). Until
  then, only the `nativeaccept/cmd/*` Go harnesses listed above exercise a
  real headless RimWorld instance.
- Natural-language player chat (`POST /api/chat`) is not served by either
  runtime: the Python server that used to serve it was removed in G01.13, and
  the Go dashboard path exposes only structured player controls
  (`ObservationDashboard`/`PlayerControls`, no free-text chat UI). Go's
  `interpreter/decode.go` decodes chat-shaped commands but is not wired into
  `serve`'s HTTP server. See
  [issue #46](https://github.com/davidarcher/rimgovernor/issues/46) tracking
  a possible Go chat implementation.
- `--colonies` multi-instance directory serving was Python-only and was
  removed, unported, in G01.13; see
  [issue #47](https://github.com/davidarcher/rimgovernor/issues/47) tracking
  a possible Go implementation.

## Routine policy components

`policy.DetectRoutine` evaluates typed survival facts and separate recovery
thresholds. Missing facts cannot certify foothold stability or clear active risk.
Ongoing medical care is distinct from urgent tending: complete native health
reads keep bad conditions and medical rest visible as a priority-2 maintained
need. A tracked patient who disappears, dies, or has incomplete health remains
unresolved until fresh living health proves recovery. Patient identities persist
through restart, Manual and direction changes; world replacement or a tick rewind
resets them. The care need does not issue
medical orders or authorize surgery; execution remains tracked in G01.07a.

Medical reserves use a separate maintained need with one medicine per colonist as
its entry threshold and three as its recovery target. Native usable resource counts
are capped against observed unexpired, allowed medicine stacks. Unknown reads
preserve the reserve latch; Manual preserves it, while world replacement or tick
rewind resets it. `RoutineMedicalPlanner` (behind `--routine-medical-plans`)
proposes a `ProductionBillAction` through the shared GearProduce bench/recipe
census, closing replenishment execution (G01.07d).

Startup supply reviews retain the first known native forbidden-supply census.
Fresh reads can shrink that cohort, but later player forbids cannot expand or
revive it. Unknown reads preserve pending cells without proving recovery; Manual
and direction changes preserve the cohort. World replacement and tick rewind
initialize a new cohort. The current journal requires fresh schema-37 state.
`--routine-supply-plans` compiles at most eight exact native item snapshots from
retained cells into a shared Allow plan. `--routine-methods` enables its Hands
handler under the existing player direction; both switches default off. Each
write requires fresh CAS, preview, emergency and authority checks. Durable item
claims prevent re-admission after cancellation or later player forbidding. Lost
replies are observed without retry; receipts alone do not complete the action,
and missing items remain unknown. Allow requires no game tick window. Native
gameplay acceptance remains deferred to G01.12.

`--routine-work-plans` compiles changed work priorities in batches of at most eight
pawns, using saved overrides and required project skills. It requires routine reviews;
`--routine-methods` enables execution. Each action retains the original work snapshot,
so changed settings cannot be silently adopted on retry. Preference changes cancel
pending methods; direct native work-tab edits revoke controller authority. Readback
checks actual priorities as well as the correlated native outcome. Settings updates
need no simulation ticks and do not certify that pawn production occurred.

`--routine-field-plans` selects rice, potatoes or corn from native season, yield
and soil facts, preferring a faster viable crop when stored food is short. It
requires routine reviews; `--routine-methods` enables execution. Up to 32 connected
patches share construction footprint reservations. Each creation refreshes the
zone map CAS and ordinary native placement checks. Exact zone cells and crop
settings must match on readback; an uncertain write is only observed. Player crop,
zone and sow/cut edits revoke authority. Completed fields can support a bounded
healthy-colony growth window from their durable completion tick, only while fresh
readback still matches. Field capacity and expected harvest never increase edible
stock. Native harvest/storage acceptance remains in G01.12.

`--routine-acquisition-plans` compiles safe wild-plant food, bounded hunting and wood acquisition
in batches of at most eight sources. It requires routine reviews; execution uses
`--routine-methods`. Native pending yield reduces new designations but never
increases stock or food runway. Food selection uses the existing diet, rot and
competing-consumer forecast. Wild plants in growing zones are excluded, outdoor
writes hold during roof-collapse hazards, and native enabled workers and ordinary
designators decide eligibility. Wood plans consume development capacity. Each
write rechecks the exact source CAS; uncertain attempts only observe. Native
harvest and placement callbacks account for actual produced stacks, including
merges. Missing or consumed output remains unknown, and a completed designation
alone is not production. Ordinary labor uses the shared healthy-colony clock.
Hunting follows plant food, allows at most two outstanding designations, and
requires a native safe hunter route, an ordinary non-explosive ranged weapon and
a usable butchering bill. Exact fresh corpses complete material acquisition;
expected meat, pending hunts and fresh carcasses never become edible stock.
Gameplay acceptance of this path remains in G01.12.

`RankDevelopment` preserves accepted shared-action commitments while ranking new
projects by deficit, player preference, native-tick age and selection hysteresis.
Unavailable methods can yield their slot within the same review. `StarterLayouts`
proposes bounded shelter and disjoint crop patches while respecting observed
geometry and player exclusions; proposals still require native preflight.
The service supplies its configured method set to each review. Disabled optional
planners retain their need assessments with `method_unavailable` and cannot occupy
selection slots. Existing committed work still consumes capacity. Capability
availability comes from runtime configuration, not a native observation.

Routine reviews persist development scores, waiting age, known worker counts and
selection history alongside need assessments. Current wood and defense deficits
use bounded native deficit fractions. Accepted player projects and unresolved
optional methods consume capacity across shared plans; admission rechecks new
commitments in the same transaction as the method. Manual clears selections;
world/direction changes or tick rewinds reset age. Unknown worker counts admit no
optional work. Configure `serve --routine-reviews --routine-project-limit 1` to
limit optional concurrency (1–8, default 2), also bounded by observed workers.
Accepted work remains tracked when capacity falls. Persisted ranking does not
create missing action families or replace native resource/placement admission.

`NewPlacementSearch` ports general development placement: up to 64 nearby observed
anchors, ordered by distance and coordinates, with explicit indoor/outdoor facts
and protected geometry. `Select` accepts an exact definition/stuff preview only
when its entire safe footprint fits the observed free cells. Native indoor facts
remain separate from roofing. Method compilation owns definition/builder prerequisites;
the selected action still requires shared method admission and Hands execution.

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
This also applies when completion is observed after cancellation: fresh stock
replaces the historic cost hold while the action remains cancelled. Unknown effects
and observations predating completion cannot release the reservation.

`Store.ReviewRoutine` commits explicit deficit/unknown/recovered assessments and
food, wood and temperature latch history with all maintained-goal reviews in one
transaction. A review cursor rejects stale writers. Manual invalidates linked
work without needing valid native facts; world/direction changes and tick rewinds
give subsequent goals new identities while preserving old action evidence and
player cancellations. Unknown threats suspend new routine work until observed safe.
Manual and direction changes retain recovery targets; world replacement and tick
rewinds reset latches. Superseded invalidated autopilot goals leave the bounded
active catalog only after all linked work is observed and cleanup is settled.
Retired goals remain readable with their original IDs, methods and receipts, and
cannot be modified or reused. Disabled bindings and player cancellations remain
retained. Enabled reviews also retire settled autopilot method plans from active
capacity. `GoalState.Methods` lists active bindings; `LoadGoalMethod` reads an exact
historical binding, and `LoadPlan` preserves its progress and admissions. Plan and
method IDs remain reserved. Current plans, unfinished dependencies, uncertain effects,
owned-draft cleanup and unsuccessful outcomes stay pinned.

Completed plan retirement retains a per-world observation-tick floor. Method
admission, preparation and dispatch reject older observations, including after a
restart or tick rewind in the same load. A different colony/load/map has its own
floor. Retirement and the floor commit with the routine review; neither issues
orders or releases unresolved work. Unsuccessful-plan resource release remains open.

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

`policy.ForecastFood` ports per-consumer food allocation under observed diet/access,
private inventory, shared animal demand and native rot deadlines. It consumes the
earliest-expiring allocation first and reports usable and at-risk nutrition. Unknown
ownership, eligibility, quantities or deadlines cannot certify runway. Native human
food supply is projected through `DecodeFoodSupply`, including holder ownership and
eligible eaters; paused native parity and Go/Python replay cover populated stock.
The combined census includes animal competition and supplies routine `FoodDays`
for the selected human consumers. Cross-section census and demand conflicts are
rejected; incomplete quantities retain unknown runway. Native replay compares
both forecasts with Python using `RIMGOVERNOR_NATIVE_FOOD_FORECAST` and
`RIMGOVERNOR_NATIVE_COMBINED_FOOD_FORECAST` reference files.

`buildingruntime.RoutineReviewer.Step` serializes observation and durable review
through the existing player gate, rechecks authority after the read, and retains
unknown needs. `observation.ObserveRoutine` includes the typed emergency census
inside the same paused brackets. Medical/combat need counts use the shared emergency
rules, deduplicate patients/threats, and remain unknown on incomplete or conflicting
evidence. Owned-draft cleanup needs use the complete shared journal and the same
cleanup predicate as the release sweep. Manual and fresh acquisition invalidate previous routine reviews
without a native read. A disabled reviewer retires existing work without acquiring
authority. The reviewer has no independent background loop. A clock scheduler can
attach the same player's reviewer through `ClockSchedulerConfig.Routine`; it runs
after clock obligations drain and before a new window decision. Running epochs and
cleanup take precedence, and a failed review prevents a new window.

Native farm and cooking censuses also feed routine assessments. Production counts
and crop definitions supply field coverage: each crop budgets consumption through
its growth cycle plus the configured food reserve, including animals that can eat
its product. Routine reads include planning definitions; unknown yields or demand
remain unknown capacity. Coverage never credits food inventory. Production counts
only actively growing edible plants; cooking requires a usable bench with an
unsuspended recipe-matching food bill. Unknown fields cannot certify recovery, and
neither crops nor a cooking bill add credit to stored food runway. The native
Protobuf acceptance scenario supports `--routine-production` with the private
`RoutineProductionFixture` for populated read parity; no harvested or cooked food
is injected. Retained captures can be replayed with
`RIMGOVERNOR_NATIVE_PRODUCTION_REFERENCE` alongside the colony capture.

Routine naming needs use the exact pending native dialog ID. Explicit absence
recovers the need; missing or obstructed observations remain unknown. The read
does not confirm names. Native Protobuf acceptance uses `--routine-naming` with
the private `ModalFixture` to verify absence, presence and unrelated replacement.
Replay its captures with `RIMGOVERNOR_NATIVE_NAMING=present` or `absent` alongside
`RIMGOVERNOR_NATIVE_COLONY_CAPTURE` to check the durable need.

Routine defense readiness reads equipment for the complete emergency census's exact
pawn IDs inside the same paused observation bracket. Only living, standing armed
colonists count. Missing gear or inconsistent colony/pawn censuses leave readiness
unknown; tick or generation changes reject the review. This supplies the maintained
defense need without issuing equipment or combat orders.

The colony development section supplies a complete, bounded power-trader census.
Routine power coverage uses each consumer's own native network and output watts;
generation on unrelated networks does not cover a deficit. Disconnected or unpowered
consumers remain deficits, while no consumers means no electrical requirement.
Enabled, unforbidden consumers without power also remain recovery targets. Missing
census or service facts preserve unknown coverage. These reads issue no power orders.
The same bounded census carries native trader footprints, conduit positions and
active map conditions for power planning. `serve --routine-power-plans` compiles
network-local generation or up to eight conduit cells through shared building
admission; `--routine-methods` enables their existing Hands execution. Installed
capacity waits for ordinary refueling/output. Solar flares and player-disabled
equipment hold proposals. Completed methods lend at most 10,000 ticks for native
power recovery, scoped to the current direction; native consumer power establishes
recovery. Targeted gameplay acceptance uses `native_go_routine_acceptance.py
--power-methods generation` or `--power-methods conduit` with ForecastFixture. Both scenarios verify native
consumer recovery, correlated construction, Manual and disabled restart. Replay
uses `RIMGOVERNOR_NATIVE_POWER_METHODS_CAPTURE=<capture-directory> go test
./internal/observation -run TestNativePowerMethodsReplay`.

`serve --routine-temperature-plans` adds complete indoor room reads to the paused
routine bracket and compiles one ordinary campfire or passive cooler in an affected
player sleeping room. It requires `--routine-reviews`; `--routine-methods` enables
shared Hands execution. Eligible bed identities select rooms even when unsafe
temperatures remove safe reachability. Complete room geometry constrains the whole
native building footprint, while shared admission reserves only that footprint.
Existing thermal facilities wait for native temperature change. Entry thresholds
are 12/32 C and recovery thresholds are 16/28 C; unknown room evidence cannot prove
recovery. Completed current-direction methods lend at most 10,000 ticks for ordinary
refueling and heat exchange. Method identity follows the bed and thermal definition,
so regenerated native room IDs cannot duplicate a method in the same goal epoch.
The isolated acceptance variants are `native_go_routine_acceptance.py
--temperature-methods cold` and `--temperature-methods hot`, with ForecastFixture,
RoutineSleepingFixture and ScenarioStartFixture. Replay uses
`RIMGOVERNOR_NATIVE_TEMPERATURE_CAPTURE=<capture-directory> go test
./internal/observation -run TestNativeTemperatureMethodsReplay`.

Routine reviews also read native pawn needs and thought targets. Per-pawn mood
goals retain break-threshold and food/rest/recreation hysteresis through Manual and
restart; missing pawns and unknown reads cannot certify recovery. `MoodMethods`
records one bounded relief proposal and its measured need benefit, preserving an
unknown future mood benefit. Active or unverified mental breaks hold new clock
windows until observed clearance. Player-forced work, draft and medical availability
remain guards; relief action execution is not enabled. The isolated `--mood-review
food|forced|mental` variants compare native inputs with Python and durable Go needs.
Replay with `RIMGOVERNOR_NATIVE_MOOD_CAPTURE=<capture-directory> go test
./internal/observation -run TestNativeRoutineMoodReplay`.

Routine disaster history joins native environmental conditions and exact building
service needs to the shared survival gates. It retains damaged identities, records
ordered refuel/breakdown/repair needs and promotes affected service priorities.
Unknown reads and expired conditions cannot certify recovery. Manual retains
evidence; world replacement resets the episode. Native compound-disaster and
captured replay acceptance cover planning, Manual and disabled restart; recovery
action dispatch remains unavailable. `Recovery` retains typed proposal inputs and
at most eight candidates for existing roofed areas or ordinary service work. Player
restrictions, availability and used shared methods constrain selection; native
admission is still required. Manual clears these candidates. See the
[contract](../docs/developers/contracts/disaster-planning.md); the Python
acceptance commands this once linked to were removed in G01.13
([issue #38](https://github.com/davidarcher/rimgovernor/issues/38) tracks
their Go rebuild).

`ReadRoutinePawns` adds the work-only detail selection to the same exact-ID read,
plus schedule (`TimetableSlot`) detail: the whole routine census is shared across
every routine planner, and `EnsureMood-*` relief dispatch needs a pawn's current
timetable assignment (`boundary.ExpectedScheduleDef`) to fence its native writes.
It preserves native work applicability and numbered/checkbox mode. `AssignWork`
selects specialists with stable ties, construction skill precedence and shared labor,
and compares proposed priorities with native readback in the correct mode. Routine
reviews use that comparison for work coverage; the proposal does not write settings.
Open selected player buildings and admitted shared projects supply the maximum
native construction-skill requirement. Missing project definitions are read inside
the same paused bracket without replacing default crop inputs. Unknown skills
preserve unknown coverage; unresolved cancelled orders retain their requirements
until native observation settles them. Other action families' requirements remain
in G01.05. Player work preferences persist with the explicit player plan and feed
every review. Updates atomically invalidate the previous review and linked methods;
the next review records the preference revision and rejects stale inputs. Missing
native work types or capabilities preserve unknown work coverage. Native work captures and
the Python reference replay with `RIMGOVERNOR_NATIVE_WORK_CAPTURE=<capture directory>`.
The native routine scenario's `--work-project` option checks a HospitalBed project
outside the default definition census; it verifies work review, not construction.

With player control enabled, `GET /api/player/work-preferences?planId=<id>` returns
the plan's preference revision and overrides. Authenticated
`POST /api/player/work-preferences/replace` accepts `requestId`, `planId`, `expected`
world identity, canonical string `expectedRevision`, and an `overrides` array of
`{ "pawn": "Thing_Human1", "work": "Construction", "priority": 0 }` entries.
Priorities are 0–4; zero explicitly disables that work in proposals. The complete
array replaces prior preferences; an empty array clears them. The existing 8 KiB
player request limit applies. Reusing an exact request returns its historical
result without restoring old preferences; changed reuse or a stale revision returns
409. Preferences neither enable control nor change native work settings. The native
routine scenario's `--work-overrides` option verifies updates, clear/replay, durable
work review and disabled restart against a private colony.

`NewRoutineSleepingPlanner` configures the shared `RoutineBuildingPlanner` to compile an active reviewed shelter deficit into
one complete method of ordinary indoor sleeping spots. It requires a known native
definition with no construction-skill prerequisite, roofed indoor cells, disjoint
safe native previews and shared resource admission. Existing admitted footprints
remain protected. Method identity survives retries; observed recovery opens a new
epoch. Every preview stays under the player gate, and Manual cancels compilation.
The compiler stores pending actions only; the shared worker owns execution.

`NewRoutineCookingPlanner` uses the same compiler for one ordinary campfire. It
requires a known cooking deficit and waits for usable benches, existing campfires
or already committed campfire work. Native previews and shared reservations decide
geometry and cost. Building the campfire does not certify a food bill or cooked
food; bill/upkeep methods remain separate action-family work.

Configure shared spending rules with repeatable `serve --player-control`
options such as `--resource-rule WoodLog:allow:50` or
`--resource-rule Steel:defense_only:100`. Each rule names a native resource,
`allow`, `stop` or `defense_only`, and a nonnegative reserve. Duplicate resources
and invalid rules fail startup. Routine method admission and Hands use the same
session rules; current building methods have routine purpose. Rules apply to new
admission and dispatch, without undoing issued native work. These process settings
are not saved in SQLite: supply them again on restart.

Native preview stock already subtracts every blueprint/frame's remaining material
deficit. After a complete attempt-correlated observation of a pending construction,
Go retains its footprint but lets net stock from a later game tick replace its
original cost reservation. Repeated pending reads retain the first proof tick;
unknown evidence clears it. Same-tick stock, unobserved writes and gross stock keep
the original cost hold. The proof replays from the fresh Go journal; it does not
certify pawn completion. `native_building_service_acceptance.py
--construction-accounting` exercises two shared player projects under a reserve
that permits exactly two walls, including unfinished work and restart.

Add `--routine-reviews` to `serve --player-control --clock-control` to attach the
reviewer to the service clock worker. It uses the default routine thresholds and
requires typed colony observations. Startup remains disabled. This option journals
needs. Add `--routine-sleeping-plans` to compile eligible shelter deficits into pending
methods at that same paused boundary. It requires routine reviews and uses the same
player gate; a failed preview prevents a new clock window. Startup remains disabled
and this option does not execute methods. `--routine-shelter-plans` includes indoor
furnishing and falls back to a bounded 9×9 starter shell when the whole sleeping
method lacks verified space. Native definitions must support one-cell wood walls
and doors; every piece needs a safe exact footprint and the complete project must
fit shared stock and reservations. The door's observed completion gates all walls.
After all shell pieces complete, up to 10,000 game ticks allow ordinary automatic
roofing; that budget derives from durable completion and cannot renew on restart.
It remains available after furnishing until native indoor capacity recovers or
the budget expires: roofed spot footprints alone do not prove a fully roofed room.
Furnishing still requires observed roofed indoor space. Unfinished or cancelled
shells grant no roofing budget. `--routine-methods` is also required to execute
the method or advance its roofing wait. `--routine-cooking-plans` independently
enables campfire compilation at the same boundary. `--routine-comfort-plans` enables
table, adjacent dining chair and recreation furniture compilation after startup
needs recover and development ranking selects comfort. Native observations retain
facility-specific dining/recreation use through Manual and restart; replacement
facilities require new use. After observed construction, at most 10,000 ticks in
the same direction permit ordinary use, observed in windows of at most 120 ticks.
Skilled furniture requires a qualified assigned builder from the same native
observation bracket, honoring saved player work preferences.
Unknown access, existing inaccessible
facilities and exhausted waits cannot certify recovery or create duplicate furniture.
Add `--routine-methods` to let the shared
Hands worker execute reviewed building and starting-supply methods under the existing player direction.
Each dispatch rechecks the journal binding, active known deficit, epoch, world and
native generation. Pending player work takes priority; Manual stops routine writes
without changing the selected player plan or acquiring another lease. Clock windows
include eligible routine work after the player plan settles. Remaining fact projection,
method selection and execution composition remain in G01.05.
`scripts/native_go_routine_acceptance.py`, run through the documented container
scenario launcher with the private construction fixture and verified Go binary,
covers the live SDK read trace, durable goals, urgent and ongoing medical needs,
unknown food forecast, Manual invalidation, joined shutdown and disabled restart.
The paused gear read is compared against native upkeep for the exact pawn/loadout
census, deficit flags, eligible candidate identities and gains, and replacement
needs. `MaintainEquipment` remains visible as `method_unavailable` until its
execution family is connected; it does not consume an optional development slot.
The `--comfort-methods` variant needs private `GuardedConstructionFixture`,
`RoutineSleepingFixture`, `UpkeepFixture` and `ScenarioStartFixture` builds. It
uses a seeded naturally mild settlement and prepares hunger/recreation needs once
the three ordinary buildings exist. It never orders their use. Native use,
one-time fixture activation, Manual history retention and disabled restart are
separate assertions; inspect the retained report before claiming acceptance.
Recreation previews require native playing-cell access, separate from placement
legality. The variant also captures populated native fire, supply, repair and
cleaning facts after Manual. Replay its `upkeep-replay.json` through the Go
boundary and durable journal with `RIMBOT_NATIVE_UPKEEP_REPLAY=<absolute-path>`
and `go test ./internal/observation -run TestNativeUpkeepReplay -count=1` from `go/`.
The replay checks Python target ordering and metrics, all five direct upkeep needs,
and, when captured, medical reserve entry/recovery policy and its maintained need. It checks
Manual invalidation and retained needs after reopening the database.
Animal reference captures additionally check pen state, reachable feed, shared
food competition and both reserve thresholds. Sleeping captures compare owners,
users, access and comfort against native facts and retain exact pawn/bed use.
A safe assignment still needs observed use; unsafe assignments remain deficits.
Native floor-place replay establishes upgrade detection; actual real-bed use
acceptance and sleeping assignment/building methods remain open.
Completed autonomous building methods retain exact native origin/current IDs in
the journal, including after retirement and Manual. Routine reviews query those
current IDs inside the paused observation bracket and verify definition, position,
rotation and material before deriving Home coverage or stone-shell needs. Player
placements, explicit cancellation and replacement geometry confer no ownership.
Unknown queries preserve established needs. Home exclusions remain explicit;
Home/stone execution and stockpile ownership await their shared action families.
The ownership census is bounded to 256 method records and 256 completed buildings;
larger histories produce unknown ownership instead of silently truncating it.
`native_go_routine_acceptance.py --shelter-methods --facility-upkeep` uses the
existing `UpkeepFixture` to remove one Home cell after normal shell construction.
Replay the captured native facts against its real journal backup with
`RIMBOT_NATIVE_FACILITY_REPLAY=<absolute-output-directory>` and
`go test ./internal/observation -run TestNativeFacilityUpkeepReplay -count=1`.
The capture verifies 35 causally completed autonomous buildings, native Home
geometry/exclusions and 31 flammable owned walls. Replay uses the real journal
backup for durable needs, unknown preservation, Manual and reopen checks.
For a same-colony scenario retry, stage the retained initial save as
`profile/Saves/RimGovernor-tribal8-baseline.rws` and pass
`--start-save RimGovernor-tribal8-baseline`; headless preparation copies that
baseline into its private profile.
Animal upkeep reviews retain containment risk and per-animal feed thresholds.
Feed shares the observed diet/rot forecast with human consumers. Missing censuses
remain unknown; a complete empty census clears animal needs. Release and slaughter
directions suppress animal targets. The policy accepts player-directed herd
exclusions; runtime herd-policy composition and animal execution remain pending.
The `--expansion-methods` variant uses the same private fixtures to construct one
spare indoor sleeping place, verify native capacity recovery and single-attempt
shared admission, then check Manual and disabled restart. It also emits the upkeep
replay. After gameplay assertions, the explicit bounded-census fixture clears
disposable loose items and filth before spawning its targets; no ticks advance
after that setup. Production census limits and game rules are unchanged.
`--supply-history` additionally clears the original supplies through the native
player Allow designator, reviews their recovery, and re-forbids the same supplies.
Repeated same-database Go starts must preserve the empty cohort and issue no
operations during those reviews. Each player edit happens while Go is joined;
the final disabled restart preserves history without advancing time.
Its `--sleeping-methods` variant also uses `RoutineSleepingFixture` to provide an
empty roofed room and healthy starting colonists. It verifies one complete sleeping
method, native observed completion, single attempts, indoor footprints and unchanged
player authority through the shared worker. `--shelter-methods` instead starts with
an outdoor site and requires a complete wall-and-door shell, normal roofing,
indoor sleeping capacity and a satisfied shelter goal. Its retained native event
history covers the whole construction run, including Manual and disabled restart.
It requires healthy colonists and no initial hostiles, and retains `initial-save.rws`.
For the container runner, stage that file as the private profile's
`Saves/RimGovernor-tribal8-baseline.rws` and pass
`--start-save RimGovernor-tribal8-baseline` to repeat the same starting colony.
`--cooking-methods` adds campfire
construction and verifies that cooking still needs a bill after the building
completes. Its `--resource-rule WoodLog:stop:0` variant uses the same room fixture
and compile-only cooking to verify pending player work, no routine admissions or
construction orders, Manual and disabled restart. Normal authorized clock windows
remain available under spending restrictions. The separate
`scripts/native_go_clock_acceptance.py --routine-reviews` scenario additionally
requires a healthy colony and verifies clock advancement and construction.

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

### Native request diagnostics

`serve --flight-recorder <absolute-path>` opt-in-records every native
request/response/error, including background reads, replacing
`controller/rimgovernor/flight_recorder.py`. It is off by default; a service
started without the flag records nothing. Requests and errors are fsynced
before the call returns; a response row is written unsynced and becomes
durable only at the next durable record or segment rotation, so a crash can
leave an explicit unmatched request but never a silently lost one. The
timeline is a bounded JSONL file rotated into numbered segments (`<path>.1` is
the newest) once the active segment reaches its size bound; the oldest segment
is dropped on rotation. Oversized payloads are replaced with a truncated
summary (SHA-256, original size, a bounded preview and, when present, the
correlating `request`/`tool`/`category` fields) rather than growing the file
unbounded. See `internal/flightrecorder` for the writer and `ReadTimeline` reader.

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
later successful drain, verified by native acceptance covering ordinary pawn
completion and disabled same-database restart through the HTTP service (the
Python acceptance commands this once linked to were removed in G01.13;
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38) tracks their
Go rebuild). Supervised Go clock control remains a separate G01.10 integration.

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
behavior tests. Follow the
[G01 issues](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AG01%22)
for active owners, dependencies and completion gates.

`serve --routine-expansion-plans` maintains one spare indoor sleeping place beyond
the observed population. It requires routine reviews and uses the shared furnishing
and whole-shell planner, project limits, resource reservations and player authority.
Expansion waits until existing housing meets current needs and until pending beds
finish. Targeted native acceptance covers the additional indoor place; whole-shell
fallback also uses the separately accepted starter-shell construction path.
