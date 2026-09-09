# RimBot backlog

This is the single queue for implementation gaps, remaining reuse audits and
gameplay acceptance. Work top-down within each priority. Check the current code
before adding an API; available native tools often need acceptance, not rebuilding.
Architecture belongs in [ARCHITECTURE.md](ARCHITECTURE.md), procedures in
[TESTING.md](TESTING.md). Remove completed items once their evidence is recorded
in the checkpoint commit; do not append an implementation diary here.

## P0 — Reliable startup execution

- [ ] **B04 · Broader sustained foothold coverage.** Extend the two-day native
  acceptance matrix to more seeds, eight-colonist starts, scarce wood, low
  fertility and temperature variants. Include longer survival, changing seasons
  and production that replaces initial supplies. Sampled stable gates over a
  bounded window do not establish arbitrary long-term colony survival.
- [ ] **B04a · Complete deterministic food control.** Verify hunting, butchering,
  crop labor, food runway and spoilage-aware storage under real pawn behavior.
  Add safe prey reachability/nearby-predator checks, nutrition-based acquisition
  limits, inventory-aware forecasts and season/biome-specific methods. Enforce
  persistent player food targets through production capacity as well as stock.
- [ ] **B04b · Native interruption acceptance.** Verify autosave recovery, letter
  attribution, real player pause/speed holds, danger preemption and load changes
  on the final binary. Add a native tick-budget boundary for execution windows;
  current polling can overshoot.
- [ ] **B04c · Shared intent completion.** Adopt completed player room shells for
  deterministic furnishing without duplicate shelter. Support safe explicit
  relocation/cancellation of issued construction through native cancellation
  skills; preserve existing orders until validated. Verify freezer expansion,
  policies and conversational refinements with a real local model.
- [ ] **B04d · Long-running goal lifecycle.** Audit durable receipt/history growth after active-plan retirement of completed
  routine native actions. Add bounded
  recovery for other known transient failures beyond confirmed interrupted tending, preserve new-colonist work coverage and
  expand sleeping/farming templates beyond the eight-colonist starter. Reconcile
  player interruptions and partial work across save rewind. Broaden paired native
  restart acceptance beyond a cancelled room shell to mixed pending work,
  resource reservations and interrupted non-idempotent actions.
- [ ] **B04e · Resource policy completeness.** Add exact bill ingredient accounting
  and policy enforcement for existing production bills. Currently protected
  policies conservatively block new bills; they do not suspend existing native
  production. Cover components, medicine, steel, fuel and material substitutions
  with target controllers, production deficits and persistent reservations.
- [ ] **B04f · Deterministic emergency/development methods.** Extend small-animal
  defense to larger encounters and extend medical recovery to unavailable doctors,
  multiple competing patients and player interruption ownership beyond tracked overrides,
  then add electrical generation/connectivity,
  research, comfort and expansion methods. Threats beyond the bounded small-animal method and unavailable power methods
  report explicit blockers instead of asking a model to improvise.
- [ ] **B04g · Native command acceptance.** Extend the isolated native chat probe to construction, room
  refinements and cancellation of issued construction. Broaden research selection,
  refusal and persistent goal cancel/resume coverage across projects, models and
  save/load boundaries. Measure schema-valid semantic errors, including wrong
  resources and unrequested numeric reserves, separately from native refusals and
  zero-inference controller acceptance. A passing scripted model run is not a
  reliability rate.
- [ ] **B04h · Player policy parameter fidelity.** Prevent spending-only chat
  instructions from inventing a numeric reserve or overwriting an existing one.
  Keep explicitly requested reserve changes available through semantic commands;
  enforce the distinction beyond prompt guidance and verify it with the local
  model in the native controller context.

## P1 — Functional colony planning and recovery

- [ ] **B06 · Spatial architecture.** Extend existing room compilation, geometry
  checks and native preflight with long-term layout, shared reservations, entrances,
  outside/room connectivity, room roles and staged construction. Compare sites
  using bounded terrain, supplies, danger, fertility and travel evidence. Accept
  ruins/nonrectangular shelters; reject sealed rooms, blocked corridors and farm
  overlap. Validate native floor/roof/area/designator coverage before adding tools.
- [ ] **B07 · Durable project scheduling.** Extend basic building/zone/installation
  reconciliation with maintained functional goals, resource competition and
  production consumption. Verify dependent work, player edits, save rewinds and
  interrupted/resumed work retain identity without duplicates. Existing plan
  dependencies, cancellation, revision guards and event triggers are implemented.
- [ ] **B08 · Native forecasts.** Audit available inputs, then add nutrition,
  diet/access/inventory-aware consumption, spoilage, harvest uncertainty, animal
  feed and labor demand. Extend medical/power/mood risk projections only from
  observed inputs. Power risk persists across unavailable observations; verify
  native aggregate readability and live reserve recovery. Preserve unavailable
  values; avoid fixed food-stat tables.
- [ ] **B09 · Combat and rescue acceptance.** Controlled movement, equip, observed
  melee/ranged hits, tending and owned-draft cleanup have passed scripted tests.
  Rescue delivery tracking exists but actual carry-to-bed has not passed a live
  scenario. Verify delivery, model-selected triage, injury interruption, real
  hostile encounters and strategy-selected stand-down. Raid victory and autonomous
  tactics remain open. Audit human undraft/redraft ownership ambiguity and native
  safety/path checks before adding automatic rescue, firefighting or heat escape.
- [ ] **B10 · Trade acceptance.** The deterministic transaction and native module
  exist. Complete real buy/sell exchanges with both sides' affordability, exact
  silver/stock changes, stale sessions/loads and trader departure/delivery checks.
  Test lost acceptance receipts without replay. Audit orbital trade's ordinary
  player input path separately from adjacent map trading.
- [ ] **B11 · Event delivery and process ownership.** Compare current persisted
  history/revision guards with acknowledged durable inbox/outbox semantics.
  Verify crash/reconnect delivery without dropped or duplicated player/game events.
  Extend paired checkpoint restart with checkpoint retention/deletion and attached
  external games. Broaden Windows legacy-migration acceptance to interruption at
  every ownership/save/stop boundary; automatic legacy reconnection is unavailable
  after takeover and requires explicit recovery. Audit game-worker PID
  plus process birth time against PID reuse; test lease expiry and
  lost-worker cleanup. Generated disposable profiles require DirectPath launches
  without process-name cleanup fallback; regenerate existing profiles to adopt this
  protection. Reuse semantics inside the existing runtime.

## P2 — Coverage, inspection and evaluation scale

- [ ] **B13 · Remaining player actions.** Audit supported native contextual orders,
  gizmos, dropdowns, reverse designators and queued jobs before adding fallbacks.
  Revalidate short-lived target/session references and selection after UI clicks.
  Cover existing-zone edits/deletion/expansion, crops, special storage filters and
  model-selected bill ingredient whitelists;
  identify gaps in animals, medical/surgery/prisoner and food/drug/apparel policies.
  Require normal native eligibility and observed effects. Packed-furniture install
  already has exact-identity, rotated pawn-work acceptance.
- [ ] **B14 · Visual review quality.** Optional visual review and data scouts exist.
  Image consultations use fresh captures with source identity and context guards.
  Add near/wide framing, verify player camera ownership and add source-image concern overlays.
  Validate good/bad layouts including missing doors; measure whether advice and
  evidence recall improve decisions. Avoid fixed reviewer timers and extra writers.
- [ ] **B15 · World progression.** World/research reads and research selection
  exist. Audit and accept normal caravan assembly, loading, movement and quest
  progression. Extend evaluation to competing resources, emergencies, multi-day
  survival and winter readiness before claiming full-game capability.
- [ ] **B16 · Sustained throughput.** Windows two-worker clock/lifecycle isolation
  passed; parallel inference throughput remains unmeasured. Identify boosted-speed
  pause causes; measure useful completed tests/minute, memory, startup, inference,
  observation and action overhead across colony ages and render/capture modes.
  Interrupted burst TPS is not sustained episode throughput.
- [ ] **B17 · Linux/container workers.** Parameterize game/GABS paths, explicitly
  configure inference networking beyond current loopback validation, mount licensed
  game/mod inputs and separate writable profiles, and persist artifacts externally.
  Verify discovery, independent clocks, shutdown and peer survival before comparing
  cost/throughput with Windows. No Linux/cloud acceptance is established.

## Completion rule

For each item record the observed failure, focused fix, source revision, checks,
native outcome and remaining limitation in its commit. Protocol tests, scripted
native acceptance and real-model gameplay are separate evidence levels. Preserve
failed trials locally. Do not reintroduce duplicate transports, manager hierarchies,
CLI wrapper services, external consultants, fixed reviewer daemons, turn clocks,
instant equipment cheats or hardcoded game facts as unfinished reuse work.
