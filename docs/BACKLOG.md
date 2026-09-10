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
- [ ] **B04f · Deterministic emergency/development methods.** Extend small-animal
  defense to larger encounters and extend medical recovery to unavailable doctors,
  multiple competing patients and player interruption ownership beyond tracked overrides,
  then add electrical generation/connectivity,
  research, comfort and expansion methods. Threats beyond the bounded small-animal method and unavailable power methods
  report explicit blockers instead of asking a model to improvise.
  Resolve repeated combat rearming on an unchanged, already-observed low-health
  colonist: require effective triage or an explicit hold without weakening injury thresholds.
- [ ] **B04g · Native command acceptance.** Extend the isolated native chat probe to construction and room
  refinements. Define and accept completed PLAYER-order archival when the 80-step
  live plan fills, preserving durable receipts and player intent. Broaden wording and context for
  issued-construction cancellation after a lost receipt, which has one native
  local-model acceptance case. Broaden research selection,
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

- [ ] **B06 · Spatial architecture.** Add long-term layout, functional room roles,
  staged construction and reuse of ruins/nonrectangular shelters. Compare bounded
  alternatives using native terrain, supplies, danger, fertility and travel evidence.
  Extend spatial protection to observed/retired rooms, continuous corridors,
  pawn-specific routes beyond the three-cell margin, and projected obstruction
  from building types beyond room shells. Validate native floor/roof/area/designator
  coverage before adding tools.
  Run `spatial_site_acceptance.py` on a stable native DLL set for shell admission,
  enclosed-farm refusal and latency evidence. Broaden game-level acceptance to
  ordinary pawn construction, live player edits, interrupted batches, large zones,
  custom definitions, sealed pockets, altered interiors and map edges. Measure
  repeated preflight/dispatch observation cost; controller fixtures do not establish
  native routes, construction completion or throughput.
- [ ] **B07 · Durable project scheduling.** Extend maintained functional goals,
  resource competition and production-consumption accounting through the shared
  plan and Hands; coordinate ingredient-policy coverage with B04e. Verify real
  consumption, competing project dispatch/restock, delayed shell-to-furnishing work
  and dependent chains across held restart. Cover player edits, save rewinds and
  interrupted/resumed work without losing identities, reservations or receipts and
  without duplicate orders; controller replay is not native scheduling acceptance.
  Broaden native edit/load/rewind acceptance for exact zone and building-facing
  postconditions. Migrate legacy targets lacking expectations only with grounded
  evidence. Extend zone contracts to stockpile filters/priority and sow/cut settings,
  and expose invariant native facing across game languages.
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
- [ ] **B17 - Container startup reliability and scale.** Broaden acceptance of the
  private Unity `gc-max-time-slice=0` startup mitigation across repeated launches,
  longer colonies and GC pressure; measure its pause/throughput effects. Investigate
  the underlying early Mono `GC_mark_from` SIGSEGV with the original setting.
  Retain failed trials and never hide native crashes with automatic retries.
  Measure sustained native test throughput, memory and disk use against Windows.
  Port remaining native acceptance scripts with hard-coded Windows paths to the
  shared GABS resolver. Verify exact paused-load ticks before fixed-baseline
  comparisons. Broaden Linux acceptance to paired restart, long-running
  pawn outcomes and mixed task builds. Bounded clock/shutdown/checkpoint acceptance
  does not establish sustained gameplay, inference throughput or cloud acceptance.

  - [ ] **Reusable native scenario runner.** Extend the Docker harness with named
    scenarios and shared setup, observation, tick-bounded waits, assertions and
    owned-process cleanup. Port existing gameplay probes through the shared GABS
    resolver instead of creating more standalone scripts. Declare each scenario's
    required save/mod/schema/model/display inputs and distinguish missing fixture
    prerequisites, infrastructure failures, assertion failures and timeouts.
    Accept when one documented command lists/selects scenarios and runs at least
    construction completion, resource production and paired-restart recovery in
    fresh containers, with structured per-scenario results and JUnit output.
  - [ ] **Pawn-outcome and scenario matrix.** Assert fresh native results tied to
    colony/map/load, action and target identities: blueprint-to-building completion,
    actual harvest/haul/craft stock changes and consumption, treatment completion,
    and interrupted pending work across restart. Reuse B04/B07/B09 acceptance
    contracts; receipts and elapsed ticks cannot satisfy completion. Add seeds,
    scarce inputs, blocked paths, player edits and headless/rendered variants with
    explicit coverage labels. Keep scripted controller scenarios separate from
    actual local-model chat runs. Accept negative cases that reliably fail when
    orders are accepted but pawn work cannot complete, retaining the last observed
    job, blocker, resource state and expected-versus-actual postcondition.
  - [ ] **Correlated flight recorder and failure bundles.** Build on SQLite events,
    retained actions, tool diagnostics and native logs. Audit actual coverage first:
    `Store` has compressed decision helpers but no current runtime callers, and
    review-local evidence is transient. Record a versioned timeline linking run,
    scenario, colony/map/load, native tick, wall time, plan/direction revision,
    goal/action, native request/receipt/readback, clock holds and pawn outcomes.
    Include failed calls and relevant background observations; identify omissions,
    truncation and dropped records explicitly. Use bounded recording with measured
    overhead and durable pre-write evidence, preserving the failure window before
    rotation. Automatically collect a consistent SQLite backup, logs, build/input
    hashes, last observations and rendered frame when available on assertion,
    timeout or crash; attempt a paired checkpoint only while the game can safely
    save, and retain partial evidence if it cannot. Accept by inducing an assertion
    failure, timeout and native process exit and diagnosing each from its bundle
    without an attached live session or unreported recording gaps.
  - [ ] **Repeatable diagnosis and regression loop.** Emit a short failure summary
    with the failed assertion, expected/actual values, last progress tick, active
    pawn jobs/blockers and links into the correlated timeline. Retain exact source,
    image, inputs, scenario parameters and one rerun command; reuse immutable paired
    checkpoints for targeted continuation while labelling fresh-start runs separately.
    Support offline timeline inspection and exporting recorded controller inputs to
    focused fixture regressions; a replayed fixture does not certify native behavior
    or bit-for-bit simulation replay. Add resource-bounded scenario scheduling and
    explicit repeated-run/flakiness reports that retain every failed attempt.
    Accept when a representative gameplay failure can be inspected offline, rerun
    from its declared inputs, captured as a focused regression, and verified with
    the same native scenario after a fix without writing a new probe script.

  **Rendered container acceptance.** Optional per-worker Xvfb/llvmpipe support
  provides explicit resolution, private rendered profiles and retained display/game
  logs. The two-worker runner captures native PNGs and verifies clocks, shutdown,
  peer survival and checkpoint retention. Broaden acceptance to screenshots/video,
  camera pan/zoom, selection, menus/dialogs and placement gestures, including B18
  input outcomes. Measure sustained rendering overhead against headless workers.
  Protocol and bounded lifecycle checks do not certify those native interactions.

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
  rendered game; protocol tests do not establish gameplay acceptance. Context menus, pointer/key discovery and native
  gesture ownership cleanup remain open.

  **Player handoff.** Add Take control and Resume automation. Taking control must
  invalidate pending autonomous work, suspend cinematic framing and acknowledge
  ownership before accepting input. Preserve the existing Manual clock semantics;
  taking input ownership must not implicitly resume simulation. Only explicit
  player release resumes automation. Scope input ownership to one viewer and the
  current colony/load/map. Release held buttons/modifiers on blur, disconnect,
  lease expiry or handoff; reject input from stale or non-owning viewers. Account
  for native player camera input so remote gestures and cinematic framing do not
  fight it. Keep input loopback-only with equivalent origin/session protection.

  Take control, Release control and Resume automation now use a single-viewer,
  load-scoped lease. Handoff invalidates pending execution, disables action follow,
  releases owned drafts and acknowledges only after a native paused readback.
  Heartbeats renew the 15-second lease; blur, hidden tabs and unmount request
  release without resuming. Expiry retains a Manual hold and rejects stale input;
  another viewer can take over, but automation requires explicit owner release.
  Model/controller writes and generic Automate cannot bypass the hold. Validate
  this against native pending controller work and actual browser disconnects.
  Held-button/modifier cleanup awaits the native gesture implementation.

  Lease-owned colonist selection and clearing are implemented through a fixed
  player-only API. Stable pawn IDs are checked against the current-map native
  roster before dispatch; a separate selection readback confirms the exact result.
  The dashboard selector does not depend on pixel coordinates. Context menus,
  direct image clicks, drag/designation, hover, scrolling and modifiers still need
  native contracts and frame-bound interaction; do not enable raw image input
  based only on a recent snapshot or camera rectangle.

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
