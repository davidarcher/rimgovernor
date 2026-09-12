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
rewind resets it. Replenishment proposals and execution remain open in G01.05/G01.07d.

Startup supply reviews retain the first known native forbidden-supply census.
Fresh reads can shrink that cohort, but later player forbids cannot expand or
revive it. Unknown reads preserve pending cells without proving recovery; Manual
and direction changes preserve the cohort. World replacement and tick rewind
initialize a new cohort. The current journal requires fresh schema-32 state.
`--routine-supply-plans` compiles at most eight exact native item snapshots from
retained cells into a shared Allow plan. `--routine-methods` enables its Hands
handler under the existing player direction; both switches default off. Each
write requires fresh CAS, preview, emergency and authority checks. Durable item
claims prevent re-admission after cancellation or later player forbidding. Lost
replies are observed without retry; receipts alone do not complete the action,
and missing items remain unknown. Allow requires no game tick window. Native
gameplay acceptance remains deferred to G01.12.

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
[contract](../docs/developers/contracts/disaster-planning.md) and
[acceptance commands](../docs/developers/testing/disaster-planning.md).

`ReadRoutinePawns` adds the work-only detail selection to the same exact-ID read.
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
The replay checks Python target ordering and metrics, all four direct upkeep needs,
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

`serve --routine-expansion-plans` maintains one spare indoor sleeping place beyond
the observed population. It requires routine reviews and uses the shared furnishing
and whole-shell planner, project limits, resource reservations and player authority.
Expansion waits until existing housing meets current needs and until pending beds
finish. Targeted native acceptance covers the additional indoor place; whole-shell
fallback also uses the separately accepted starter-shell construction path.
