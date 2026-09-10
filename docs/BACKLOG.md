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
  Add safe prey reachability and monitoring after designation and season/biome-specific methods.
  Nutrition-based harvest limits and per-colonist inventory/rot forecasts exist;
  validate actual spoilage, changing temperatures and food sharing during sustained runs. Enforce
  persistent player food targets through production capacity as well as stock.
- [ ] **B04b · Native interruption acceptance.** Verify controller actions racing autosaves,
  letter attribution, real player pause/speed holds, danger preemption and load changes
  on the final binary. Native execution tick boundaries are implemented; broaden
  interruption coverage to autosaves and real player input during those windows.
  Require both current native DLLs in headless trials. One ordinary autosave has
  save creation, long-event recovery, exact clock-boundary and paused reload acceptance;
  broaden seeds, timings and mixed pending work.
- [ ] **B04c · Shared intent completion.** Broaden adopted-room furnishing acceptance to
  edited rooms and hot/cold variants. Support safe explicit
  relocation/cancellation of issued construction through native cancellation
  skills; preserve existing orders until validated. Exact-target blueprint cancellation
  has semantic-command/shared Hands acceptance, including a lost successful receipt.
  A partly built wooden bed also has native material-refund acceptance.
  Broaden interrupted cancellation across native
  save/load and real player direction changes.
  Verify freezer expansion,
  policies and conversational refinements with a real local model.
- [ ] **B04d · Long-running goal lifecycle.** Broaden retention measurement from archived completed actions to
  goal-method evidence and event history in sustained native campaigns.
  Indexed recent-history reads preserve the full ledger. Synthetic lifecycle
  audits and archived-method deduplication have controller and paired-native
  restart coverage. Broaden sustained native coverage to remaining goal evidence
  such as hunting targets and recovery histories, and measure ledger disk growth.
  Add bounded
  native acceptance for known pre-write construction-resource recovery and add
  recovery for other known transient failures beyond confirmed interrupted tending,
  verify work reassignment when colonists join an existing colony and
  broaden larger-starter native acceptance to bed use and additional seeds, and
  expand farming and shelter capacity beyond the fixed shell. Reconcile
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
- [ ] **B04g · Native command acceptance.** Extend the isolated native chat probe to construction and room
  refinements. Issued-construction cancellation after a lost receipt has one native
  local-model acceptance case; broaden wording and context. Broaden research selection,
  refusal and persistent goal cancel/resume coverage across projects, models and
  save/load boundaries. Measure schema-valid semantic errors, including wrong
  resources and erroneous reserve-tool selection, separately from native refusals and
  zero-inference controller acceptance. A passing scripted model run is not a
  reliability rate.
  Combined goal/policy and multi-resource requests have native paused-session
  acceptance, including paired restart and subsequent goal cancellation/resumption.
  Repeated fixed-fact measurement includes native resource-label distractors;
  broaden wording, contexts and models beyond this bounded acceptance. Include
  preservation-versus-removal ambiguity and conflicting instructions across
  multiple construction intents; the conservative preservation guard is not a
  general natural-language authorization proof.

## P1 — Functional colony planning and recovery

- [ ] **B06 · Spatial architecture.** Extend existing room compilation, geometry
  checks and native preflight with long-term layout, outside/room connectivity,
  room roles and staged construction. Shared planned-room bounds, immediate entrance
  clearance and native footprint conflicts are validated at admission, with current
  placement footprint checks in Hands. Verify these under native construction and
  player edits; extend protection to observed/retired rooms and continuous corridors,
  including projected access as blueprints become buildings. Native zone census
  and immediate entrance access now gate shell admission and dispatch; verify live
  enclosed-farm refusal, interrupted batches, large zones and custom definitions,
  and measure repeated preflight/dispatch observation latency.
  Compare sites using bounded terrain, supplies, danger, fertility and travel evidence. Accept
  ruins/nonrectangular shelters; reject sealed rooms and blocked corridors beyond
  immediate entrances. Validate native floor/roof/area/designator coverage before adding tools.
- [ ] **B07 · Durable project scheduling.** Extend basic building/zone/installation
  reconciliation with maintained functional goals, resource competition and
  production consumption. Verify dependent work, player edits, save rewinds and
  interrupted/resumed work retain identity without duplicates. Existing plan
  dependencies, cancellation, revision guards and event triggers are implemented.
  Execution budgets follow deterministic ready order when stock changes, retaining
  full remaining batches, player reserves and uncertain-write costs. Verify live
  production consumption and competing project dispatch/restock/restart behavior;
  controller fixtures do not establish native scheduling acceptance.
  Watchdog-held goals can continue after newly observed tracked completion while
  preserving methods and receipts. Verify delayed native shell-to-furnishing work
  and dependent-chain completion across held restart; current coverage is replay.
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
- [ ] **Dashboard native acceptance.** Verify Outpost time controls against a
  rendered isolated session: pause, normal/fast/superfast, danger refusal, new
  player direction and load invalidation. Verify action follow on pawn, building
  and placement writes, turning it off and loading another map. Validate that
  native lead/capture timing makes the selected action visible. Add manual-camera
  suppression and configurable/decoupled cinematic pacing before claiming a
  continuous high-speed director.
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
  Interrupted burst TPS is not sustained episode throughput. Use the read-only
  `scripts/dashboard_throughput.py` to retain wall TPS including pauses and exclude
  load/rewind/disconnection intervals. Compare isolated rendered, suspended and
  headless runs from the same checkpoint, with follow off and no competing workers.
  Measure observation age in game ticks, danger-to-pause latency, player-stop latency,
  verified outcomes per minute and missed safety gates at each candidate speed.
  Keep Ultrafast test-only until its reaction envelope is accepted.
  Batch only the retained starting-supply targets through discovered native contracts:
  the current method emits up to eight cell-wise Unforbid operations with repeated
  validation. Whole-map unforbid would broaden scope to unrelated player restrictions.
  Profile per-operation preview, identity, native dispatch and readback cost before
  removing redundant reads; uncertain writes still require observation before retry.
  Consider adaptive game-tick review windows for stable colonies, preserving native
  hazard supervision and a bounded observation age. Archive/reuse verified checkpoints
  for long-lived scenarios while keeping fresh-start acceptance separate.
- [ ] **B17 · Linux/container workers.** Parameterize game/GABS paths, explicitly
  configure inference networking beyond current loopback validation, mount licensed
  game/mod inputs and separate writable profiles, and persist artifacts externally.
  Verify discovery, independent clocks, shutdown and peer survival before comparing
  cost/throughput with Windows. No Linux/cloud acceptance is established.

- [ ] **B18 · Interactive game view and low-latency streaming.** Keep the React
  dashboard and replace the watch-only game panel with explicit player control.
  Complete native input alongside continuous video. Snapshot polling remains a
  fallback; a faster transport alone does not remove capture or dispatch latency.

  **Native input prototype.** Discover the installed RimBridgeServer schemas for
  map/UI clicks, drag, hover, scrolling, camera movement and modifier/key handling.
  Upstream capabilities are candidates, not proof of installed support. Prefer
  native input that does not require OS focus; preserve normal vanilla/modded
  eligibility. Start with selection, context menus and camera navigation against
  the current snapshot view, then test drag selection and placement gestures.
  Keep explicit player input outside model execution and route it through the
  existing runtime's identity and writer guards, not a second game-order owner.

  Discrete dashboard pan/zoom uses the installed camera signatures, validates
  each live schema, serializes through the runtime lock, checks load identity
  before dispatch and reads native camera state afterward. Navigation disables
  action follow without changing clock or automation, clamps zoom to the normal
  native range and never retries uncertain writes. Still validate pan directions,
  map-edge clamping, zoom limits and concurrent native camera movement in a
  rendered game; protocol tests do not establish gameplay acceptance. Selection,
  context menus, pointer/key discovery and the ownership handoff below remain open.

  **Player handoff.** Add Take control and Resume automation. Taking control must
  invalidate pending autonomous work, suspend cinematic framing and acknowledge
  ownership before accepting input. Preserve the existing Manual clock semantics;
  taking input ownership must not implicitly resume simulation. Only explicit
  player release resumes automation. Scope input ownership to one viewer and the
  current colony/load/map. Release held buttons/modifiers on blur, disconnect,
  lease expiry or handoff; reject input from stale or non-owning viewers. Account
  for native player camera input so remote gestures and cinematic framing do not
  fight it. Keep input loopback-only with equivalent origin/session protection.

  **Frame and gesture integrity.** Map browser coordinates through the actual
  displayed image bounds, including letterboxing, resolution changes, fullscreen
  and browser scaling. Bind requests to frame/view identity and reject stale frames
  or changed camera/map context. Preserve button-down/up and key ordering; coalesce
  obsolete pointer movement without dropping gesture boundaries. Define what happens
  when the view changes during a drag. Never replay an uncertain click automatically.
  Confirm effects from native state rather than treating input delivery as completion.

  **Continuous video.** The receive-only WebRTC prototype is implemented: native
  RGB shared memory, latest-frame delivery, viewer leases, session cleanup and
  visible snapshot fallback. Paused native-to-aiortc delivery is verified; this does
  not establish desktop Chrome acceptance or the 30–60 fps target. Remaining work:
  evaluate asynchronous GPU readback and hardware encoding instead of synchronous
  ReadPixels/software encoding, and measure Chrome delivery and simulation cost.
  Verify capture/encode/delivery during slow controller reviews and inference
  while preserving native main-thread constraints.
  Evaluate WebRTC data channels versus the existing server's WebSocket support for
  small input messages; use one authoritative input path. Bound queues and discard
  stale video frames instead of accumulating latency. Keep snapshots as a visible
  degraded mode and disable unsafe interaction when video is stalled. Separate
  streaming demand from simulation speed; acceptance/headless runs must incur no
  capture, encoding or cinematic delay when streaming is off.

  Connection-scoped cleanup, ordered heartbeats, bounded reconnect, retained-frame
  fallback and delivery diagnostics are implemented. Synthetic in-app browser
  checks cover pause/resume, stalled-track recovery, source resolution changes and
  fullscreen; protocol tests cover concurrent viewers and slow lease renewal.
  Repeat these against native capture in desktop Chrome, including hidden tabs and
  actual colony/load changes. Synthetic delivery does not establish game performance.

  **Acceptance.** Use an isolated rendered colony to verify click selection,
  right-click menus, scroll/zoom, camera pan, drag selection/designation and
  modifiers without stealing desktop focus. Cover browser resize/fullscreen,
  disconnect mid-drag, multiple viewers, stale frames, load/map changes, native
  player input and handoff during pending controller work. Measure capture-to-display
  and input-to-visible-effect latency (median/p95), delivered fps, dropped frames,
  CPU/GPU cost and end-to-end simulation TPS with streaming on/off, including while
  a review is busy. Retain native outcome evidence. Smooth video does not certify
  safe Ultrafast control; pair that claim with B16's separate reaction/throughput
  acceptance. Prototype feasibility and measured results decide the final media stack.

## Completion rule

For each item record the observed failure, focused fix, source revision, checks,
native outcome and remaining limitation in its commit. Protocol tests, scripted
native acceptance and real-model gameplay are separate evidence levels. Preserve
failed trials locally. Do not reintroduce duplicate transports, manager hierarchies,
CLI wrapper services, external consultants, fixed reviewer daemons, turn clocks,
instant equipment cheats or hardcoded game facts as unfinished reuse work.
