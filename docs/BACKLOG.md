# RimBot backlog

This is the single queue for implementation gaps, remaining reuse audits and
gameplay acceptance. Work top-down within each priority. Check the current code
before adding an API; available native tools often need acceptance, not rebuilding.
Architecture belongs in [ARCHITECTURE.md](ARCHITECTURE.md), procedures in
[TESTING.md](TESTING.md). Remove completed items once their evidence is recorded
in the checkpoint commit; do not append an implementation diary here.

## P0 — Reliable startup execution

- [ ] **B04 · Repeatable starter foothold.** Obtain three consecutive fixed-build,
  fixed-model baseline passes: eight living colonists, eight nearby completed
  sleeping places, a nearby stockpile of at least nine cells and allowed starting
  pemmican. Then verify usable enclosed/roofed shelter, storage filters/access and
  feasible food work over at least two game days without player repairs. Follow
  with scarce-wood, low-fertility and ruin variants; no fixed production layout.

Acceptance baseline: the documented 20-run adaptive eight-tribal campaign reached
zero combined footholds, with five stockpiles and one small completed shell as
partial results. Code changed between runs, so this is not a controlled comparison.
Schema execution and subsequent UI checks do not supersede that outcome. Raw local
evidence is under `.rimbot/headless-campaign-20260908/`; it is not bundled in Git.

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
  Audit PID plus process birth time against PID reuse; test lease expiry and
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
