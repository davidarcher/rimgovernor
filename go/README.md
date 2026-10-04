# Go controller development

`go/` is the RimGovernor runtime: it observes the colony over GABP from
RimBridgeServer, runs deterministic routine policy, executes admitted work
through Hands, and serves the player API. The launcher (`cmd/launcher`, built as
`RimGovernorLauncher.exe`) is the sole control surface and starts this binary
directly. Start from the [source map](../docs/developers/source-map.md) and
[architecture overview](../docs/developers/architecture/overview.md); this page
covers building, running and testing the module, plus a per-family reference for
routine policy.

Use Go **1.27.1** from `.go-version`. From this directory:

```powershell
$env:GOTOOLCHAIN = 'go1.27.1'
$env:CGO_ENABLED = '0'
go run ./cmd/test   # the packages your change affects; go test ./... for everything
go vet ./...
go build -o ../.rimgovernor/go/rimgovernor.exe ./cmd/rimgovernor
```

Linux race checks use CGO/GCC; Windows race checks are not claimed without a
compatible C compiler. Dependencies are pinned in `go.mod`/`go.sum`; see the
[source notices](../THIRD_PARTY.md).

Wire contracts are generated from the [canonical Protobuf schemas](../contracts/schema-generation.md);
the generated Go wire module and its proof module have separate checks in
[the Go generation guide](../tools/protobuf/go/README.md). Generated parsing
preserves transport presence; domain validation enforces bounds, scope and
authority. Native adapters and observed game effects need separate tests.

## Running

From this directory on Windows:

```powershell
go build -ldflags -H=windowsgui -o ..\RimGovernorLauncher.exe ./cmd/launcher
..\RimGovernorLauncher.exe   # or double-click it
```

The launcher rebuilds the controller, the production native mod and the game
layout when stale, then starts `serve` with the settings in
`.rimgovernor/launcher.json` ([setup](../docs/players/setup.md)). The controller
launches RimWorld itself and talks GABP to RimBridgeServer directly.

`serve -h` is the authoritative flag list. Two modes:

- `serve --profile PATH ...` is **autonomous play**: player control (resume/pause,
  lifecycle save/load, presentation/media, the typed endpoints under
  `internal/httpapi/player.go`), the clock worker (event polling, epoch renewal,
  bounded supervised windows of 600 ticks under a 30-second lease), durable rounds
  with method execution across every routine planner family, and caravan journey
  tracking.
- `serve --observe ...` is **observation only**: reads the running game, never
  acquires control or writes. Tuning flags are rejected.

| Flag | Effect |
| --- | --- |
| `--config`, `--game`, `--state` | Absolute game configuration directory and state path, and the configured game ID (both modes). |
| `--profile` | Absolute shared game profile; required for autonomous play. |
| `--listen`, `--timeout` | Loopback listen address (default `127.0.0.1:0`, prints the URL; loopback IPs and numeric ports only); native call timeout. |
| `--clock-test-acceleration` | Acceptance only: every window at boosted Ultrafast. Otherwise each window runs at the speed the player last chose in game (Ultrafast under player pacing when none was chosen). |
| `--routine-resource-*` | Resource reserves/stops. MaintainResource default floors: Steel 200, ComponentIndustrial 10, stone blocks 150; trade buys components toward the same floor. |
| `--resume` | Run the bot for the observed world at startup and after every native load, without a launcher Resume. |
| `--flight-recorder <path>` | Flight recorder ring (default `<profile>/flight/flight.jsonl`; none under `--observe`); see [Native request diagnostics](#native-request-diagnostics). |

**Configuration.** `serve` reads command-line flags, then process environment;
there is no configuration file. Player preferences and colony state live in the
SQLite `--state` database. A compiled default applies only when neither source
sets a value, and environment never overrides an explicit flag. Every value is
validated in `parseServe` before anything connects.

| Environment variable | Read by | Effect |
| --- | --- | --- |
| `RIMGOVERNOR_ROUTINE_FAMILIES` | `parseServe` (autonomous play only) | Comma-separated short names (see `GET /api/routines`) narrowing the composed planner families; unset composes all. Rejected with `--observe`. |
| `RIMGOVERNOR_NATIVE_*_CAPTURE`, `_FORECAST`, `_REFERENCE`, `RIMGOVERNOR_NATIVE_NAMING` | `go test` only | Replay captured native evidence through component tests; see [Evidence replay](#evidence-replay-variables). Never read by `serve`. |

**Known defects:** in autonomous play the native clock can fail to start
([#45](https://github.com/davidarcher/rimgovernor/issues/45)) or restart in short
thrashing bursts ([#42](https://github.com/davidarcher/rimgovernor/issues/42)).

**No licensed game files here.** Without a real game install and prepared save,
`serve` fails at the bridge handshake (`bridge transport failure: initialize:
...`). That failure, `go build`, `go vet` and `go test ./cmd/...` are
compilation/protocol/wiring checks, not gameplay evidence.

## Player API

The launcher ([launcher doc](../docs/developers/architecture/launcher.md)) reads
this API. Endpoint shapes are in
[the player API contract](../docs/developers/contracts/go-player-api.md). The
player surface is control, not orders: session, control (resume/pause), clock
(acknowledge) and colony reads. Every other action family (tend, rescue, draft,
building, research, trade, zones, ...) is reached only through the routine
planners. The population target and production policy (reserves and spending)
belong to the autopilot alone.

Multi-instance colony directory serving (`--colonies`) does not exist in Go
([#47](https://github.com/davidarcher/rimgovernor/issues/47)).

## Testing pyramid

- Many fast unit tests: `go run ./cmd/test` for what a change affects, `go test ./...`
  for all (no game needed). Native reply fixtures under `contracts/fixtures` and
  captured payloads replayed through `RIMGOVERNOR_NATIVE_*_CAPTURE` establish
  parsing and durable review, not native outcomes.
- Fewer integration tests inside packages (`clock_worker_integration_test.go` and
  similar) against in-memory MCP sessions and temporary SQLite databases.
- A small set of native acceptance cases under `internal/nativeaccept/cases/*`, run
  by `internal/nativeaccept/cmd/acceptance` (`list`, `run`, `suite`) against a real
  headless RimWorld; see [choose-tests.md](../docs/developers/testing/choose-tests.md)
  for when to run them and [#38](https://github.com/davidarcher/rimgovernor/issues/38)
  for coverage gaps. `service/restart` is the kill-and-restart case:
  `serve --resume` plays with no HTTP write, is killed, and a restart on the same
  state resumes autonomous play for the same world.

Receipts do not prove pawn work completed; every harness asserts a native
postcondition.

## Routine policy components

Cross-cutting rules that hold for every family below:

- **Unknown is not recovered.** Missing, incomplete, conflicting or stale reads
  keep a need unknown and cannot certify recovery, runway, coverage or ownership.
- **Receipts are not evidence.** Completion comes from fresh native observation;
  a lost reply is observed, never retried blindly.
- **Manual** preserves latches, cohorts and recovery targets and invalidates linked
  work without needing valid native facts. **World replacement or tick rewind**
  resets latches, ages and cohorts and gives later goals new identities while
  preserving old action evidence and player cancellations.
- **Every write** goes through shared method admission and Hands under the current
  load token and tick: fresh CAS, native preview, emergency and authority checks.
  Pending player work takes priority; clock windows include eligible routine work
  after the player plan settles.
- Unknown threats suspend new routine work until observed safe.

### Survival, medical and trade

`policy.DetectRounds` evaluates typed survival facts and separate recovery
thresholds; missing facts cannot certify foothold stability or clear risk.

- **Ongoing medical care** is distinct from urgent tending: complete native health
  reads keep bad conditions and medical rest visible as a priority-2 maintained
  need. A tracked patient who disappears, dies or has incomplete health stays
  unresolved until fresh living health proves recovery. Patient identities persist
  through restart and Manual. The need issues no medical orders and authorizes no
  surgery.
- **Medical reserve** is a separate maintained need: entry at one medicine per
  colonist, recovery target three. Usable counts are capped against observed
  unexpired, allowed stacks; unknown reads preserve the latch. `RoundsMedicalPlanner`
  (`medical` family) proposes a `ProductionBillAction` through the shared
  GearProduce bench/recipe census.
- **`TradeWithCaravan`** (`trade` family, priority 3, development-exempt) stands
  while a tradeable or arriving caravan is on the map and the medical reserve, the
  component target or a `ResourceTargets` surplus gives it something to trade; an
  arriving caravan lends the clock one game hour per review. Each phase is one
  bounded plan per caravan and epoch. The open (up to three attempts) is native's:
  it walks the eligible negotiator to the trader with a goto that tracks the
  wanderer and opens the session on arrival, staying pending until then. Line
  staging buys the observed shortfall from silver above the reserve and sells
  surplus above target; a sheet with no affordable line ends the session. Sheet
  reads, staging, accept and cancel need only present participants. The native
  resource census after the exchange is the evidence.
- **Naming** needs use the exact pending native dialog ID; explicit absence
  recovers the need, missing or obstructed observations stay unknown. The read
  does not confirm names.
- **Defense readiness** reads equipment for the emergency census's exact pawn IDs
  inside the same paused bracket. Only living, standing armed colonists count;
  missing gear or inconsistent censuses leave readiness unknown. It issues no
  equipment or combat orders.
- **Disaster history** joins native environmental conditions and building service
  needs to the survival gates: it retains damaged identities, records ordered
  refuel/breakdown/repair needs and promotes affected service priorities. Expired
  conditions and unknown reads cannot certify recovery; recovery action dispatch is
  unavailable. `Recovery` retains typed inputs and at most eight candidates for
  existing roofed areas or ordinary service work (Manual clears them). See the
  [contract](../docs/developers/contracts/disaster-planning.md); acceptance is
  tracked in [#38](https://github.com/davidarcher/rimgovernor/issues/38).

### Supply, work, fields and acquisition

- **`supply`**: startup reviews retain the first known native forbidden-supply
  census. Fresh reads can shrink the cohort; later player forbids cannot expand or
  revive it; unknown reads preserve pending cells. The family compiles at most
  eight exact item snapshots from retained cells into a shared Allow plan. Durable
  item claims prevent re-admission after cancellation or later forbidding; missing
  items stay unknown. Allow needs no game tick window. The journal requires fresh
  schema-37 state.
- **`work`**: compiles changed priorities in batches of at most eight pawns, using
  required project skills. Each action retains the original work snapshot so
  changed settings are not silently adopted on retry. The autopilot owns every
  priority: a player edit in the Work tab is replanned as drift. Readback checks
  actual priorities as well as the correlated outcome. Updates need no ticks and do
  not certify production.
- **`field`**: selects rice, potatoes or corn from native season, yield and soil
  facts, preferring a faster viable crop when stored food is short. Up to 32
  connected patches share footprint reservations; each creation refreshes the zone
  map CAS and native placement checks. Exact cells and crop settings must match on
  readback; an uncertain write is only observed. Player crop, zone and sow/cut
  edits revoke authority. Completed fields can support a bounded healthy-colony
  growth window from their durable completion tick while readback still matches.
  Field capacity and expected harvest never increase edible stock.
- **`acquisition`**: safe wild-plant food, bounded hunting and wood, in batches of
  at most eight sources. Native pending yield reduces new designations but never
  increases stock or runway. Food selection uses the diet, rot and
  competing-consumer forecast; wild plants in growing zones are excluded; outdoor
  writes hold during roof-collapse hazards; native enabled workers and designators
  decide eligibility. Wood plans consume development capacity. Each write rechecks
  the source CAS; native callbacks account for actual produced stacks including
  merges, and missing or consumed output stays unknown (a completed designation is
  not production). Hunting follows plant food, allows at most two outstanding
  designations, and requires a native safe hunter route, an ordinary non-explosive
  ranged weapon and a usable butchering bill. Expected meat, pending hunts and
  fresh carcasses never become edible stock.
  - `ClearPests` (foothold priority 2): a recognised pest (`policy.PestDefinition`,
    today `Alphabeaver`; native's `PestDefinitions` matches) counted anywhere by the
    wild-animal census opens the goal. Methods `pest-hunt-*`, plans
    `routine-pest-hunt-*`, one hunt per pest within the two-hunt budget, no butcher
    bill or prey distance required; hostile ones are left to the defense family. It
    recovers when none remain.

### Development ranking and placement

`RankDevelopment` preserves accepted shared-action commitments while ranking new
projects by deficit, player preference, native-tick age and selection hysteresis.

- Unavailable methods can yield their slot in the same review. Disabled optional
  planners keep their assessments as `method_unavailable` and cannot occupy
  slots; capability availability comes from runtime configuration. Committed work
  still consumes capacity and stays tracked when capacity falls.
- A commitment whose labor idles (`RoundsLaborUse`: no pawn on any of its profile's
  work types while one enabled for them idles or works for another type) across
  reviews spanning `DevelopmentIdleTicks` releases its slot and reads `labor_idle`
  until work is picked up again.
- Rounds persist scores, waiting age, known worker counts and selection history
  with need assessments. Wood and defense deficits use bounded native deficit
  fractions. Unknown worker counts admit no optional work; optional concurrency is
  automatic (every project a distinct observed worker can take, at most eight).
  Admission rechecks commitments in the same transaction as the method. Manual
  clears selections.
- Persisted ranking does not create missing action families or replace native
  resource/placement admission.
- `PlannedLayout` stands each shell on its layout plan room, respecting observed
  geometry and player exclusions; proposals still need native preflight.
- `NewPlacementSearch`: up to 64 nearby observed anchors ordered by distance and
  coordinates, with explicit indoor/outdoor facts and protected geometry. `Select`
  accepts an exact definition/stuff preview only when its entire safe footprint
  fits observed free cells. Indoor facts stay separate from roofing. Method
  compilation owns definition/builder prerequisites.

### Goals, methods, plans and the journal

- Fresh SQLite state stores maintained goals and method links to the same
  executable plans. Reviews use a revision CAS; a renewed deficit gets a new method
  epoch after observed recovery, preserving older plans and receipts. Cancellation
  and context invalidation journal linked action cancellations atomically; goal
  guards apply at preparation and dispatch.
- Plans persist a validated dependency graph. Preparation and dispatch require each
  predecessor's observed completion in the current world; receipts cannot satisfy
  dependencies.
- `Store.AdmitBuildingMethod` reserves a complete building method's costs and
  footprints with its goal link and plan in one transaction, using the Hands
  resource policy and authoritative competing reservations. Actions stay pending
  until fresh Hands admission. Completed geometry yields to fresh placement
  observations rather than claiming coordinates forever; this holds when completion
  is observed after cancellation (fresh stock replaces the historic cost hold).
  Unknown effects and observations predating completion cannot release a
  reservation.
- `Store.ReviewRounds` commits deficit/unknown/recovered assessments and food, wood
  and temperature latch history with all maintained-goal reviews in one
  transaction; a review cursor rejects stale writers.
- Superseded invalidated goals leave the bounded active catalog only after all
  linked work is observed and cleanup settled. Retired goals stay readable under
  their original IDs, methods and receipts and cannot be modified or reused;
  disabled bindings and player cancellations are retained. Enabled reviews also
  retire settled method plans from active capacity. `GoalState.Methods` lists
  active bindings; `LoadMethod` reads an exact historical binding and `LoadPlan`
  preserves progress and admissions. Plan and method IDs stay reserved. Current
  plans, unfinished dependencies, uncertain effects and unsuccessful outcomes stay
  pinned (unsuccessful-plan resource release remains open).
- Completed-plan retirement keeps a per-world observation-tick floor: admission,
  preparation and dispatch reject older observations, including after restart or
  tick rewind in the same load. A different colony/load/map has its own floor.
  Retirement and the floor commit with the rounds and issue no orders.

### Observation and the rounds reviewer

- `bridge.ReadColonyFacts` and `observation.DecodeColony` consume the typed native
  core and planning geometry. Missing optional fields stay unknown; a changed
  tick/world is refused. Raw food runway never becomes policy `FoodDays`.
  `observation.ObserveColony` brackets the read with paused identities at the same
  tick and native generation, rejects expired/cancelled reads and publishes nothing
  on failure. Callers separately validate load token and tick.
- `observation.ObserveRounds` includes the typed emergency census in the same
  paused brackets. Medical/combat counts use the shared emergency rules,
  deduplicate patients/threats and stay unknown on incomplete or conflicting
  evidence.
- `buildingruntime.Rounder.Step` serializes observation and durable review through
  the player gate and rechecks authority after the read. The reviewer has no
  background loop: the clock scheduler attaches it through
  `ClockSchedulerConfig.Routine`, and it runs after clock obligations drain and
  before a new window decision. Running epochs and cleanup take precedence; a
  failed review prevents a new window. A disabled reviewer retires existing work
  without acquiring authority; Manual and fresh acquisition invalidate previous
  rounds without a native read.
- Autonomous play attaches the reviewer to the service clock worker with default
  thresholds and requires typed colony observations. Startup remains disabled.
- The undraft sweep reads the complete shared journal for the live plans that need
  drafts (see [Guarded player components](#guarded-player-components)).
- `ReadRoundsPawns` adds work-only detail and schedule (`TimetableSlot`) detail to
  the exact-ID read; `EnsureMood-*` relief dispatch needs a pawn's current timetable
  assignment (`boundary.ExpectedScheduleDef`) to fence native writes. It preserves
  work applicability and numbered/checkbox mode. `AssignWork` selects specialists
  with stable ties, construction skill precedence and shared labor, and compares
  proposed priorities with native readback in the right mode; reviews use that
  comparison for coverage and the proposal writes nothing. Open player buildings
  and admitted shared projects supply the maximum construction-skill requirement;
  missing project definitions are read in the same paused bracket. Unknown skills
  and missing work types preserve unknown coverage; unresolved cancelled orders
  retain requirements until native observation settles them.
- **Food forecast**: `policy.ForecastFood` allocates food per consumer under
  observed diet/access, private inventory, shared animal demand and native rot
  deadlines, earliest-expiring first, reporting usable and at-risk nutrition.
  Unknown ownership, eligibility, quantities or deadlines cannot certify runway.
  Native human food supply is projected through `DecodeFoodSupply` (holder
  ownership, eligible eaters); the combined census includes animal competition and
  supplies `FoodDays` for the selected human consumers. Census/demand conflicts
  are rejected; incomplete quantities keep runway unknown.
- **Production census**: native farm and cooking censuses feed assessments. Each
  crop budgets consumption through its growth cycle plus the configured food
  reserve, including animals that can eat its product. Only actively growing edible
  plants count; cooking needs a usable bench with an unsuspended recipe-matching
  food bill. Unknown yields or demand stay unknown capacity, unknown fields cannot
  certify recovery, and neither crops nor a bill credit stored food runway.
- **Power census**: complete and bounded. Every trader with refuelable (fuel,
  target, out-of-fuel, allowed fuels) and breakdown service facts, every battery as
  a zero-load row with stored and capacity watt-days, per-network summaries
  (generation, consumption, stored and capacity energy), plus trader footprints,
  conduit positions and active map conditions.

### Building families

- **`sleeping`**: `NewRoundsSleepingPlanner` configures the shared
  `RoundsBuildingPlanner` to compile an active shelter deficit into one complete
  method of indoor sleeping spots: a known definition with no construction-skill
  prerequisite, roofed indoor cells, disjoint safe previews and shared resource
  admission. Existing footprints stay protected; method identity survives retries
  and observed recovery opens a new epoch. Every preview stays under the player
  gate. It stores pending actions only; the shared worker executes.
  - `MaintainHousing`: assigns a vacant suitable bed to a colonist without one
    through the typed `assign` operation (one per goal epoch, carrying the expected
    previous bed) and, when nobody can be assigned, stages one `Bed` in a
    Bedroom-hosting room whose temperature suits the unhoused. Only observed sleep
    in the owned bed recovers the goal ([upkeep contracts](../docs/developers/contracts/upkeep-contracts.md)).
- **`shelter`**: includes indoor furnishing. Falls back to a bounded 9x9 starter
  shell when the whole sleeping method lacks verified space, or digs a corridor and
  room into a visible rock face when the typed excavation-site read verifies a
  supported, reachable target near the colony ([spatial contracts](../docs/developers/contracts/spatial-contracts.md)).
  - Excavation runs as bounded `excavation-stage-<n>` methods of at most eight
    cleared cells, then an `excavation-door` method; each stage is re-read natively
    before admission and after restart.
  - Native definitions must support one-cell wood walls and doors; every piece needs
    a safe exact footprint and the project must fit shared stock and reservations.
    The door's observed completion gates all walls.
  - After all shell pieces complete, up to 10,000 game ticks allow ordinary
    automatic roofing; the budget derives from durable completion and cannot renew
    on restart. It stays available after furnishing until native indoor capacity
    recovers or the budget expires. Unfinished or cancelled shells grant none.
    Furnishing requires observed roofed indoor space; roofed spot footprints alone
    do not prove a fully roofed room.
- **`cooking`**: `NewRoundsCookingPlanner` uses the same compiler for one ordinary
  campfire. It requires a known cooking deficit and waits for usable benches,
  existing campfires or committed campfire work. A campfire does not certify a
  food bill; bill methods are separate.
- **`comfort`**: composes the basic-phase planner for `EnsureComfort` (one table,
  one adjacent chair and one horseshoes pin from the unfiltered census, any room
  role, once shelter is no longer owed; methods `basic-comfort-*`, plans
  `routine-basic-comfort-*`). After startup needs recover and ranking selects
  comfort, it also compiles table, adjacent dining chair and recreation furniture.
  - Dining and recreation are native-role facilities: a table, chair or horseshoes
    pin counts only in a proper room whose `Room.Role` the facility catalog
    (`policy.FacilityCatalog`) hosts (DiningRoom, RecRoom or generic Room; one
    shared room serves both), and previews are restricted to those cells. With no
    hosting room it stages the 9x9 starter shell (`comfort-shell`) and furnishes it
    once roofed. The catalog lists every installed RoomRoleDef with planner status;
    other roles are explicit pending rows.
  - Facility-specific use is retained through Manual and restart; replacement
    facilities need new use. After observed construction, up to 10,000 ticks in the
    same load permit ordinary use, observed in windows of at most 120 ticks. Skilled
    furniture needs a qualified assigned builder from the same observation bracket;
    furniture with no construction skill requirement (a crafting spot) needs one
    available pawn with Construction enabled.
- **`workshop`**: when a resource floor deficit (default floors, or the stone-block
  floor for the stone whose chunks the map counts most) has no reachable bench
  hosting an available recipe, it reads the native recipe catalog and previews the
  first research-available, unpowered, unskilled bench definition (`CraftingSpot`
  on the tribal baseline; `TableStonecutter` once stonecutting is researched) in a
  Workshop-hosting room (`workshop-<definition>`), or stages a shell first
  (`workshop-shell`). While `MaintainHousing` is in deficit it waits
  (`awaiting_plan:earlier_shell`) so builders are not split across two rings.
  Research-gated or powered benches stop at `workshop_bench_unavailable`. The
  `resource` family then places the bill and `work` covers the bench's DoBill type.
  Unknown access, inaccessible facilities and exhausted waits cannot certify
  recovery or create duplicate furniture.
- **`expansion`**: maintains one spare indoor sleeping place beyond the observed
  population using the shared furnishing and whole-shell planner, project limits,
  reservations and player authority. It waits until housing meets current needs and
  pending beds finish.
- **Ownership**: completed autonomous building methods retain exact native
  origin/current IDs in the journal (also after retirement and Manual). Rounds
  query those IDs in the paused bracket and verify definition, position, rotation
  and material before deriving Home coverage or stone-shell needs. Player
  placements, explicit cancellation and replacement geometry confer no ownership;
  unknown queries preserve established needs. Home coverage restores missing cells
  in connected enclosed interiors in bounded batches. Stockpile ownership follows
  the completed zone receipt. The census is bounded to 256 method records and 256
  completed buildings; larger histories give unknown ownership rather than
  truncating.
- Hands executes reviewed building and starting-supply methods; each dispatch
  rechecks the journal binding, active known deficit, epoch, world and native
  generation. Manual stops routine writes without changing the selected player
  plan or acquiring a lease.

### Power

The `power` family compiles network-local generation or up to eight conduit cells
through shared building admission and Hands.

- Coverage uses each consumer's own native network and output watts; generation on
  unrelated networks does not cover a deficit. Disconnected or unpowered consumers
  are deficits; no consumers means no requirement; enabled, unforbidden consumers
  without power remain recovery targets. Missing census or service facts keep
  coverage unknown. A powered network whose stored reserve would drain in under
  `PowerPlanning.ReserveMinDays` (default one day) is a deficit too, so generation
  is sized to connected load, not momentary surplus.
- A producer that is out of fuel or broken down holds the proposal
  (`waiting_for_refuel`, `waiting_for_repair`): refuel and repair are other
  families' pawn work, never a second generator. Solar flares and player-disabled
  equipment also hold proposals.
- The generator comes from native planning availability and fuel stock (solar, then
  wood with wood on hand, then chemfuel), not a hardcoded default.
- Completed methods lend at most 10,000 ticks for native recovery, scoped to the
  current load token; native consumer power establishes recovery. The reads issue
  no power orders.

### Temperature

The `temperature` family adds complete indoor room reads to the paused bracket and
compiles one ordinary campfire or passive cooler in an affected player sleeping
room through Hands.

- Eligible bed identities select rooms even when unsafe temperatures remove safe
  reachability. Complete room geometry constrains the whole native footprint;
  shared admission reserves only that footprint. Existing thermal facilities wait
  for native temperature change.
- Entry thresholds 12/32 C, recovery 16/28 C; unknown room evidence cannot prove
  recovery. Completed methods in the current load lend at most 10,000 ticks.
- Method identity follows the bed and thermal definition, so regenerated native
  room IDs cannot duplicate a method in the same epoch.

### Refrigeration

`refrigeration` maintains `MaintainRefrigeration` at priority 2, bypassing ranked
development (spoilage is a loss already being paid). The typed food census carries
each stock's roof, temperature and room. `MaintainFoodStorage` counts a stock as
stored when roofed and either at or under 10 C or with at least five days of rot
runway. The review latches on roofed perishable nutrition warmer than that, in a
known room, short of runway (enter at the food-storage at-risk threshold; release
once every such stock reads 5 C or colder). Per affected room, in deterministic
order:

1. Unenclosed room: report `enclosed_storage_room_needed`.
2. Serving cooler disconnected or unpowered: hold `cooler_power_needed` (the
   `power` family's deficit).
3. Cooler's hot side vents indoors: hold `cooler_heat_rejection_blocked`.
4. Warm-setpoint serving cooler: patch it to the freezer target (-5 C) through the
   shared building-temperature action.
5. Otherwise wait for native cooling.

With no serving cooler, or after a completed cooler method's 120,000-tick allowance
elapsed without release, it compiles one `Cooler` on the lowest-sorted wall cell
with a straight inside-wall-outdoors line, front outward, gated on native `Cooler`
availability (`cooler_research_needed`). The Go side derives a cooler's cold and hot sides from its rotation.

### Upkeep: cleanliness, lighting, flooring, routes

**`MaintainCleanFacilities`** is a bounded response to failed work coverage, not a
janitor.

- The typed room census carries RimWorld's `Cleanliness` stat and each filth row's
  room. A workspace (enclosed kitchen, hospital or laboratory, or any enclosed room
  holding a cooking bench) latches dirty below -1 and releases at -0.25. The latch
  (`UpkeepHistory.DirtyRooms`, keyed by the room's lowest cell because native room
  IDs renumber on region rebuild, with its entry tick) persists across restarts.
- A latched room's filth becomes a direct clean target after 30,000 ticks of grace
  with a Cleaning-enabled colonist present, or at once when none has Cleaning
  enabled; at most eight targets per review, dirtiest room first. The order is
  player-forced, so any colonist not incapable of Cleaning may carry it. Filth in
  unlatched rooms and inherently dirty rooms (barns, any room holding a butcher
  bench) stays ordinary colonist work.
- Kitchen/butcher separation: butcher placements protect every cell of every room
  holding a cooking bench and cooking placements the reverse. When every butcher
  bench shares a room with a cooking bench, `butcher-spot-separated` admits a fresh
  `ButcherSpot` outside; the butcher bill defers (`butcher_separation_pending`)
  until that method was tried, then prefers a bench whose room holds no cooking
  bench. Deconstructing the co-located bench needs a generic deconstruct action the
  tree lacks (follow-up under #6).

**`MaintainLighting`** (`lighting`) keeps work-bench interaction cells lit from
measured glow. `UpkeepFacts.lighting` lists each colonist work table/research bench
interaction cell with ground glow, roof and room, and every `CompGlower` fixture
with radius, lit flag and service state.

- A roofed cell under 0.3 latches its bench (`RoundsLatches.Lighting`); an unknown
  census keeps the latch; it releases on the next measured census, never on a
  receipt.
- The planner defers to an in-range fixture that is merely unserviced
  (`lamp_power_needed`, `lamp_fuel_needed`, `lamp_repair_needed`,
  `lamp_switched_off`). A lit one within placement radius that still leaves the
  cell dark is `lamp_lit_but_cell_dark`; a lit one reaching only from further is
  partial coverage and does not stop a lamp of the cell's own.
- Otherwise it previews candidate cells nearest first (free, walkable, unzoned cells
  of the same room within two of the interaction cell) and admits one
  `StandingLamp` (only with an active power source) or `TorchLamp`. A cell whose
  room grows a plant that dies to light (`light_sensitive`, cave fungus) is
  protected and never latches.

**`MaintainFlooring`** (`flooring`) lays role-driven floors from measured terrain.
`UpkeepFacts.flooring` lists every proper indoor home room with the terrain under
each cell and any floor already ordered, plus a table of each named terrain's
cleanliness, beauty, path cost, flammability and natural flag.

- Deficient: clean workspaces (kitchen, hospital, laboratory, any enclosed room with
  a cooking bench) while a cell's terrain cleanliness is negative; living rooms
  (bedroom, barracks, dining, recreation) while a cell is natural ground; barns,
  butcher rooms and other roles have no requirement. A deficient room latches by its
  lowest cell (`RoundsLatches.Flooring`); unknown census keeps the latch; release is
  the next measured census, never a receipt.
- Clean workspaces are served first. From the policy floor list (`SterileTile`,
  stone tiles, `PavedTile`, `Concrete`, `WoodPlankFloor`) it picks the
  known-available terrain meeting the tier that pays for the most cells of a bounded
  batch (24), then by the tier's score over native stats and cost.
  `floor_research_needed`, `floor_materials_needed`, `floor_pending` defer.
- Each cell is previewed as a one-cell placement and admitted as its own action of
  one plan; completion is the terrain grid reading the admitted floor.
- A third, traffic tier floors the busiest natural home cells outside any tiered
  room using the routes census's observed travel samples (never a projected path): a
  cell counts once the census holds at least four times the per-cell minimum (12)
  and the cell itself at least that many, scored for path cost first.

**`MaintainRoutes`** (`routes`) keeps every facility reachable by its users from
observed reachability, never flood-fill or straight-line distance.
`UpkeepFacts.routes` lists each player bed, work bench (interaction cell), storage
building, dining surface, turret and stockpile zone (`zone-<id>`) with, per mobile
colonist, the game's own `CanReach` answer and, for a bounded number of reachable
pairs, the path cost and cell count.

- A facility no listed colonist reaches also lists breach candidates: one-cell
  player walls on its room's border whose outer neighbour some colonist can reach,
  nearest first, with any door already ordered.
- The census also reports traffic: every 30 ticks each walking colonist adds a
  sample to its cell; the top cells are listed with terrain, home flag, any floor
  ordered, and the window's total and start tick.
- A deficient facility latches by ID (`RoundsLatches.Routes`); unknown census keeps
  the latch; a colony with no mobile colonist can declare nothing unreachable.
- Order: storage and stockpiles, then benches, dining, beds, defence. It previews a
  `Door` of `WoodLog` on the breach cells in order and admits the first native
  reports legal with a one-cell footprint. A door over a wall is the one placement
  where the planner accepts native's "would wipe" verdict as the deliberate
  replacement (exactly one wiped building, no blueprint or frame).
  `route_door_pending`, `route_no_breach`, `route_door_unavailable` defer.
- The latch releases on the next census reading the facility reachable with no door
  still ordered on its border: a door frame is walkable before the door stands, so
  an ordered door holds the latch and plan until it lands.

**Map conditions** bend three responses while they last: under a solar flare
`MaintainRefrigeration` keeps a method, a `cook_ahead` `CookMealSimple` bill on a
fuelled wood bench sized to the warm at-risk nutrition beyond meals already
reserved, and the defense layout treats unpowered turrets as absent rather than
rearming them; under an eclipse `MaintainLighting` measures unroofed work cells too;
under a psychic drone `EnsureMood` enters up to .15 above the break threshold for
pawns bearing the `PsychicDrone` thought.

**Animal upkeep** retains containment risk and keeps a per-race-group herd feed
reserve (5 days of the group's nutrition). Feed shares the diet/rot forecast with
human consumers. Missing censuses stay unknown; a complete empty census clears
animal needs. Release and slaughter directions suppress animal targets. The policy
accepts player-directed herd exclusions; runtime herd-policy composition and animal
execution remain pending.

**Equipment**: the paused gear read is compared against native upkeep for the
pawn/loadout census, deficit flags, eligible candidates and gains, and replacement
needs. `MaintainEquipment` stays `method_unavailable` until its execution family is
connected and does not consume an optional development slot.

### Mood

Per-pawn mood goals retain break-threshold and food/rest/recreation hysteresis
through Manual and restart; missing pawns and unknown reads cannot certify recovery.
`MoodMethods` records one bounded relief proposal and its measured need benefit,
preserving an unknown future benefit. An active or unverified mental break neither
holds new clock windows nor declares an emergency (it ends only as ticks pass), so
windows are still evaluated on other goals' work. Player-forced work, draft and
medical availability remain guards; relief action execution is not enabled.

The same read carries grouped thought rows. When removable environment thoughts
dominate a pawn's pressure, the review records the upkeep goals whose facilities
remove them, raises their development deficits and proposes `facility_provision`
instead of relief ([mood-control](../docs/developers/contracts/mood-control.md#facility-provisioning)).

### Test and replay map

Decision logic is covered by colony snapshot tests, not acceptance runs:

| Area | Test |
| --- | --- |
| Power reserve/battery/wind/geothermal, map conditions | `internal/buildingruntime` snapshots |
| Comfort goals | `buildingruntime/rounds_facility_snapshot_test.go` |
| Workshop target ranking | `buildingruntime/rounds_workshop_snapshot_test.go` |
| Kitchen/butcher separation | `TestSnapshotCleanSeparationAdmitsSeparatedSpot` |
| `upkeep/sleeping` through real-bed use | colony snapshot |
| Facility-provisioning decision | `internal/policy/mood_snapshot_test.go` |

Targeted gameplay acceptance (`acceptance run <case>`):

| Case | Fixture |
| --- | --- |
| `power/fuel` (out-of-fuel generator: hold, colonists refuel, consumer recovers) | `PowerFixture` |
| `refrigeration/build\|setpoint\|power\|season` (each ends measuring spoilage recovery on the part-rotted meat stack, whose `CompRottable` progress stops under 0 C; `season` starts settled cold, lets heat waves ramp, starts the service on the warmed stock) | `RefrigerationFixture` |
| `clean/filthy` | `CleanlinessFixture` |
| `light/dark\|outage\|partial\|fungus` | `LightingFixture` |
| `floor/kitchen` (traffic tier is unit-tested) | `FlooringFixture` |
| `route/stockpile` | `RoutesFixture` |

The `upkeep/<scenario>` cases are the per-deficit startup-upkeep acceptance: each
opens on a fixture holding one deficit, composes the live service with only the
routine families that own it (`secure-supplies,repair,clean` for `UpkeepFixture`
scenarios; no `work` family so ordinary hauling does not race the controller),
follows the journal from deficit through method and plan to observed recovery, and
re-reads the native postcondition after the service releases the game. Run with
`acceptance run upkeep/<scenario>... -root <bridge root> -rimgovernor <service exe>
-output <fresh dir>` from `go/`. For a same-colony retry pass
`--start-save RimGovernor-tribal8-baseline`; headless preparation stages the
committed baseline (`scripts/fixtures/saves/RimGovernor-tribal8-baseline.rws`).

| Scenario | Checks |
| --- | --- |
| `scattered` | haul medicine into covered storage; repair a damaged home wall via native `PAWN_ORDER_KIND_REPAIR`; outdoor dirt never ordered |
| `storage-missing` | haul refused until SecureSupplies creates a filtered stockpile |
| `blocked` | allowed areas exclude targets; deficits stay visible, nothing completes |
| `fire` | home fire latches as an emergency deferring every development row |
| `medicine` | reserve deficit resolves through acquisition, a bill or mining |
| `feed` | pet without reachable stored feed |
| `sleeping` | one-bed shortage: bed built, ownership follows, every colonist observed sleeping in an owned bed |
| `cold` | sleeping room below the cold floor gets a heat source |

Native scenario variants (flags on the native routine scenario):

- `--routine-production` (private `RoundsProductionFixture`): populated read parity;
  no harvested or cooked food is injected.
- `--work-project`: checks a HospitalBed project outside the default definition
  census; verifies work review, not construction.
- `--mood-review food|forced|mental`: compares native inputs with durable Go needs.
- `--expansion-methods`: constructs one spare indoor sleeping place, verifies native
  capacity recovery and single-attempt admission, then Manual and disabled restart;
  also emits the upkeep replay. The bounded-census fixture clears disposable loose
  items and filth before spawning targets (no ticks advance after that setup).
- `--supply-history`: clears the original supplies through the native Allow
  designator, reviews recovery and re-forbids them. Repeated same-database starts
  must preserve the empty cohort and issue no operations; each player edit happens
  while Go is joined.
- `--sleeping-methods` (`RoundsSleepingFixture`: empty roofed room, healthy
  colonists): one complete sleeping method, native completion, single attempts,
  indoor footprints, unchanged player authority.
- `--shelter-methods`: outdoor site; requires a wall-and-door shell, normal roofing,
  indoor sleeping capacity and a satisfied shelter goal, with event history across
  the whole run including Manual and disabled restart. Needs healthy colonists, no
  initial hostiles; retains `initial-save.rws`.
- `--cooking-methods`: campfire construction; verifies cooking still needs a bill.
- Clock acceptance needs a healthy colony and verifies clock advancement and
  construction.
- The `ForecastFixture`, `RoundsSleepingFixture` and `ScenarioStartFixture` drive
  the cold and hot temperature variants; `UpkeepFixture` drives the facility-upkeep
  scenario (removes one Home cell after normal shell construction).

#### Evidence replay variables

Set to a retained capture, then run the test from `go/`. All `go test` only.

| Variable | Test |
| --- | --- |
| `RIMGOVERNOR_NATIVE_COLONY_CAPTURE` | `./internal/observation -run TestColonyNativeCapture -v` |
| `RIMGOVERNOR_NATIVE_FOOD_FORECAST`, `RIMGOVERNOR_NATIVE_COMBINED_FOOD_FORECAST` | reference files compared against both forecasts in native replay |
| `RIMGOVERNOR_NATIVE_PRODUCTION_REFERENCE` | alongside the colony capture |
| `RIMGOVERNOR_NATIVE_POWER_METHODS_CAPTURE=<dir>` | `./internal/observation -run TestNativePowerMethodsReplay` |
| `RIMGOVERNOR_NATIVE_TEMPERATURE_CAPTURE=<dir>` | `./internal/observation -run TestNativeTemperatureMethodsReplay` |
| `RIMGOVERNOR_NATIVE_MOOD_CAPTURE=<dir>` | `./internal/observation -run TestNativeRoundsMoodReplay` |
| `RIMGOVERNOR_NATIVE_WORK_CAPTURE=<dir>` | native work capture replay |
| `RIMBOT_NATIVE_UPKEEP_REPLAY=<abs path to upkeep-replay.json>` | `./internal/observation -run TestNativeUpkeepReplay -count=1` |
| `RIMBOT_NATIVE_FACILITY_REPLAY=<abs output dir>` | `./internal/observation -run TestNativeFacilityUpkeepReplay -count=1` |

- Upkeep replay checks target ordering and metrics, all five direct upkeep needs,
  and, when captured, medical reserve entry/recovery policy and its maintained need;
  it checks Manual invalidation and retained needs after reopening the database.
  Animal captures also check pen state, reachable feed, shared food competition and
  both reserve thresholds. Sleeping captures compare owners, users, access and
  comfort with native facts and retain exact pawn/bed use; a safe assignment still
  needs observed use and unsafe ones stay deficits. Native floor-place replay
  establishes upgrade detection.
- Facility replay uses the real journal backup for durable needs, unknown
  preservation, Manual and reopen checks, and verifies 35 causally completed
  autonomous buildings, native Home geometry/exclusions and 31 flammable owned
  walls.

## Observation service

Build the executable above, then run `rimgovernor serve --observe` with absolute
`--config` and `--state` paths and the configured `--game` ID. It attaches over
GABP to the running game and opens a fresh SQLite database; `--listen` defaults to
`127.0.0.1:0` and startup prints the URL.

It serves health/state/plan reads, retains last-good observations (marked stale)
when refresh fails, starts in Manual and cannot issue orders. Interrupting cancels
and joins polling before closing the SDK, database and asset handles.

Presentation reads: `GET /api/presentation/camera`, `/selection`, `/colonists`
(roster limited to the current map), and `/notifications` (letters, messages and
alerts, fixed limits 40, 12 and 40; explicit unavailable outcomes preserved). No
query parameters or bodies. Replies use the canonical presentation ProtoJSON shape
(optional presence; 64-bit integers as decimal strings). A service without the
provider returns 404; unavailable or stale observations use the sanitized local
error shape. These reads cannot select, move the camera, send input, acknowledge or
dismiss a notification, or resume play. Native notification production still needs
game-level acceptance.

### Native request diagnostics

`serve` records every native request/response/error (including background reads)
and every kinded service event to a flight recorder ring, by default
`<profile>/flight/flight.jsonl`; `--flight-recorder <absolute-path>` names another
(the acceptance runner's per-case path); `--observe` has none.

- The ring outlives each launch: a new launch continues the sequence and stamps
  rows with its own `run` id.
- Rows are written to the OS before the call returns and fsynced only at segment
  rotation and close, so a process crash loses nothing and a machine crash can lose
  the unsynced tail (per-request fsync cost seconds per step on CI disks).
- The bounded JSONL file rotates into numbered segments (`<path>.1` newest) at a
  size bound; the oldest is dropped. Oversized payloads become a truncated summary
  (SHA-256, original size, bounded preview, and the correlating
  `request`/`tool`/`category` when present).
- Writer: `bridge.FlightRecorder`; reader: `bridge.ReadTimeline`.

## Guarded player components

Autonomous play (`serve --profile <absolute-game-profile>`) includes the building
service; pass the same `--config`, `--game`, `--state` and `--listen` as the
observation service. The profile must be the shared game profile so another
controller cannot acquire its process lock. The service starts paused and never
restores a live lease from SQLite.

**Control.** The launcher's Resume runs the bot for the observed world and Pause
stops it (available while a resume is pending). Request IDs survive background
refreshes; result checks only read the recorded request. Controls are hidden when
the service is read-only.

- `POST /api/player/control/resume` and `/pause` (pause stops local work before
  waiting for native cleanup) require JSON and the process token from
  `GET /api/player/session` in the `X-RimGovernor-Player` header. Tokens stay in
  memory. Requests bind exact colony/load/map identity and stable request IDs;
  control intents are journaled in order without a CAS.
- An uncertain HTTP reply is resolved by reading its request ID through
  `GET /api/player/control?requestId=...`. Historical results are separate from
  current permission. Repeating a control request never acquires another lease.
- Shapes: [fixed player API](../docs/developers/contracts/go-player-api.md).

**Drafts are plan-owned.** Owned drafts come only from routine planners (defense,
medical). A plan's `owned_draft` action drafts its pawn through the `DraftIntent`
arm of `Actions/Apply`; subdue and movement actions name that draft action as
prerequisite; combat orders go out as the `CombatOrders` arm (a fight keeps a roster
of drafted defenders). Native keeps no draft claim. The rounds undraft sweep
(`plannedDrafts`, `undraftCandidates`) undrafts every drafted, live, sane colonist
no live plan needs: an unsettled or held draft action, the capturer of an open
capture or arrest plan, or an open fight's roster; a pawn running an Arrest or
Capture job is spared. Manual, checkpoint saves and shutdown undraft nobody; with
authority inactive the game's own auto-undraft applies.

**Admission gates.** Both building admission checks require fresh, complete threat
and basic pawn-health observations. Standing hostiles, hunting predators, critical
medical needs and unknown facts hold new orders. Previously issued attempts stay
observable while held; the controller neither releases their reservations nor
invents a retry.

`bridge.Client.ReadPawns` reads 1-256 exact pawn IDs, including dead pawns, with
optional detail families disabled. It preserves snapshot tokens and drafted state;
missing pawns never establish ownership or permission.

**Worker and shutdown.**

- The worker observes unresolved attempts after restart, renews only an existing
  lease, and does not start the game clock; pawn work needs the player or the
  supervised native scenario to advance time.
- Shutdown retains the native connection, database and profile owner until all work
  has joined and native authority cleanup is confirmed. A failed revoke is
  retryable; a lost reply is resolved by fresh observation before releasing the
  profile lock. After writers drain, a fresh positive colony/load/map replacement
  can retire the old shutdown target without revoking authority in the new world;
  unavailable identity retains ownership for another Close attempt.
- `Executor.Stop` cancels work and joins native dispatch plus receipt persistence; a
  failed drain retains the process lock, bridge and database until a later drain
  succeeds (native acceptance covers ordinary pawn completion and disabled
  same-database restart through the HTTP service; coverage gaps in
  [#38](https://github.com/davidarcher/rimgovernor/issues/38)).
- The building runtime combines exact native preview/map facts, complete SQLite
  reservation recovery and one-attempt execution. Authority and building writes use
  separately held typed capabilities (the read-only service has neither). The
  executor records dispatch before effects and resolves lost replies through
  attempt lookup and correlated observations. Unsuccessful outcomes stay distinct
  from unknown effects.

**Clock.** The internal clock scheduler performs one finite healthy-colony
scheduling step through the shared session; durable window admission binds current
review and native cursor evidence to dispatch, and repeated unchanged decisions keep
their request identity. The attached clock worker runs event polling, epoch renewal
and scheduling independently with joined, retryable shutdown. Autonomous play
attaches it with normal speed, 600-tick windows and a 30-second owned lease; startup
remains disabled. Interruptions hold execution without automatic acknowledgement;
the launcher shows the interruption review and explicit acknowledgement, which never
enables orders. Event-history maintenance checkpoints reviewed evidence while
retaining interruption holds and acknowledgement replay. Actual Go clock acceptance
remains pending. See the
[clock recovery contract](../docs/developers/contracts/go-clock-recovery.md).

## Isolated building acceptance

Build `./internal/buildingruntime/cmd/buildingsmoke` for the native scenario host.

- `--mode place --execute` requires absolute `--config`, `--profile`, `--state`,
  `--request` and fresh `--output` paths plus the configured `--game`. The profile
  is the shared running game's real profile directory; the state file must be new;
  the request is one official ProtoJSON `PlacementCandidate` naming an observed site
  and material. It performs one guarded dispatch and closes its lease; a receipt
  does not establish completed construction.
- The scenario advances ordinary pawn work, then `--mode observe` with the same
  state/profile/game paths and a new output directory (no `--execute`/`--request`)
  reopens the journal and observes the exact attempt without a write lease. It
  normally requires correlated completed construction;
  `--expected-outcome cancelled|interrupted` requires that terminal outcome
  instead. Unknown or pending evidence never passes.
- `--force-takeover` is accepted and changes nothing (there is no attachment lease).
- Both modes retain raw call evidence and a report; neither starts a game or
  advances ticks.

## Optional evidence replay

```powershell
go run ./cmd/rimgovernor replay expected.json actual.json
```

Exit 0 means matching JSON, 1 a difference or input error, 2 invalid usage. It reads
files only; it runs no policy and certifies no native outcome. Only insignificant
whitespace is ignored: IDs, order, numeric spellings, escapes, unknown fields and
nulls are significant. Inputs are bounded to 8 MiB and 128 containers; malformed
JSON, duplicates and invalid UTF-8 fail.

Injected clocks and ID sequences in `internal/testkit` support deterministic tests.
Follow the [G01 issues](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AG01%22)
for active owners and completion gates.
