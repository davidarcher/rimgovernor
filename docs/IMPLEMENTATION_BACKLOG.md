# Colony controller implementation and playtest backlog

Status: active plan, September 7, 2026. Baseline controller: 00ad80d.

This is the execution backlog for the existing Python controller and RIMAPI fork. It supersedes older implementation-order suggestions, not the historical test evidence. Keep one authoritative world model, project store, action lifecycle and spatial reservation system. No new manager tier, production MCP migration, or larger-model escalation is planned.

## Definition of success

The fixed eight-member Lost Tribe colony establishes a usable starter base through normal gameplay, without player repair: accessible starting supplies, appropriate consolidated storage, eight usable sleeping places, enclosed/roofed shelter, a feasible food-production path, and actual pawn work progressing. Reusing ruins or mining shelter is valid. Stockpiles are not prerequisites for using loose allowed resources. No fixed room size, crop, material or map corner is prescribed.

An issued command is not a completed job, and a completed job is not necessarily an achieved goal. Measure each separately. Temporary sleeping spots followed by beds are valid upgrades, but hundreds of redundant spots are not success.

## Working rules

- Reuse existing implementations before adding capabilities. Inspect current code because historical architecture tables contain superseded gaps.
- Define native wire contracts in OpenAPI and regenerate DTOs. Use explicit capability metadata rather than substring dispatch.
- Native eligibility and ordinary player actions are authoritative. No resource spawning, forced completion, immediate caravan departure or blanket hostile-presence veto.
- Use one configured local model per comparison. Start within the practical 8–9B budget; smaller models remain an experiment. Pin quantization, reasoning, context, prompt and tool set in reports. Do not change model and architecture simultaneously when measuring a fix.
- Preserve player's pause ownership and keep a single game controller active. Live LM Studio chat must stop before harness execution.
- Commit every completed implementation slice with relevant checks and a short evidence update. Do not push. Keep logs, binaries, saves and decompiled third-party source out of Git.
- AutoRim is an MIT selective-port source: retain attribution. RimMolt is behavioral reference; no core reuse license was found. Do not copy its implementation or security defects.

## Execution order

All boxes below are initially unchecked. Tests of a component do not close its live acceptance gate.

### P0 — Reproducible baseline and honest receipts

- [x] Checkpoint/review the currently untracked RimMolt benchmark client and its tests separately from production changes.
- [x] Restore the RIMAPI/controller test configuration with RimWorld closed; keep a reversible configuration backup. Record native DLL hash and source revisions. Do not mix the stock RimMolt experiment with controller benchmarks.
- [ ] Extend the existing tribal benchmark report to separate intended quantity, attempted placement, accepted native effects, completed objects and usable capacity.
- [ ] Record time to first useful order, first pawn progress, shelter usability, model/tool latency, rejected and repeated calls, excess capacity, context usage, and player interventions.
- [x] Save a fresh baseline report before implementation comparisons. Inspect and validate the existing fixture instead of regenerating it unnecessarily.

Gate: a no-op/control run and a known fixture observation produce accurate reports; game pauses on exit and no second controller runs. Never count an arbitrary first command as useful progress.

### P1 — Repair capability routing before building more APIs

- [x] Replace substring-based write-domain routing with explicit operation metadata.
- [x] Account for every exposed write: executable domain, controller-owned path, or intentionally withheld reason.
- [x] Repair singular bill add/update/delete/reorder access and building power routing where native semantics are appropriate.
- [x] Keep the destructive drop-all apparel editor withheld; add ordinary targeted equipment actions through P2.

Gate: coverage tests have no unexplained exposed writes. Administrator and specialist can modify an existing bill; native readback verifies instant setting changes while paused. No duplicate bill API.

### P2 — Native contextual actions and selected-object controls

- [ ] Add native pawn/target right-click option discovery with disabled reasons, parameters and queued-order support where the game supports it.
- [ ] Add inspect text and supported object gizmos, toggles, dropdowns and reverse designators.
- [ ] Use short-lived, target/session-bound action references. Re-resolve availability before execution; stale menu indices must never select a different action.
- [ ] Route applicable semantic orders through this bridge rather than fabricated JobDefs. Keep unsupported UI-only action types explicit.
- [ ] Add equipment candidates with native usability, reachability and restriction evidence, informed by AutoRim.

Gate: nonviolent equip, forbidden/unreachable targets, blocked construction, changed menus, drafted orders and toggles match native choices and failures. Verify effects; an invoked action alone is not a completed pawn job.

### P3 — Compact feedback and goal postconditions

- [ ] Attach typed relevant changes to action results: observation tick, changed targets, completed/interrupted work, actionable notifications and pause/modal state.
- [ ] Extend current goal evidence for usable sleeping capacity, supply access, storage function and food-production readiness. Keep unknown evidence distinct from failure and success.
- [ ] Check intended quantity/footprint against actual effects. Flag overshoot and inconsistent results for repair using native removal/cancellation actions.
- [ ] Capture the 332-sleeping-spot case as a regression scenario. No special-case prompt containing that exact map layout.
- [ ] Persist compact decision-boundary observations/actions for replay; avoid copying the entire map or tool catalog per event.

Gate: instant effects verify while paused; construction waits for native progress. A goal for eight sleeping places cannot pass based only on a successful fill command. Changed/removed work reopens the existing goal rather than producing a duplicate project.

### P4 — Complete native placement and spatial evidence

- [ ] Expose resolved native Architect/designator choices, TerrainDef floors, roof/home/allowed-area editing, and normal claim/chop/haul/tame/smooth actions.
- [ ] Return per-cell partial results and native eligibility reasons. Preserve instant placement behavior for zero-work objects.
- [ ] Add room-door connectivity and outside access, retaining existing enclosure and reservation logic.
- [ ] Give site selection bounded comparative evidence: starting pawn/supply locations, terrain, existing structures, danger, fertility, available materials, and estimated travel between functional areas.
- [ ] Show candidate sites and the selected site's concise justification in diagnostics. Do not introduce another architect path.
- [ ] Validate entrances, usable interior, corridors, existing zones and reservations before committing placements. Bridges and demolition require explicit intended effects and native checks.

Gate: distinguish a sealed room, missing door, connected hallway, ruin, mountain enclosure and valid starter shelter. Reject collisions with farms/corridors. A room need not be rectangular or 11x11; accepted sites must have supporting observations rather than arbitrary coordinates.

### P5 — Close the starter-base loop

- [ ] Run the complete tribal-eight acceptance case after P1–P4 and fix the earliest causal failure per iteration.
- [ ] Verify supply access near the colony without treating forbidden=0 across the map as a goal.
- [ ] Verify capacity and real shelter usability, storage filters/access, feasible crop/hunt/other food work, and available worker execution.
- [ ] Exercise scarce wood, low-fertility terrain and existing-ruin variants after the baseline passes. These are evaluation cases, not fixed production recipes.

Gate: three consecutive baseline passes with no player repairs, followed by the terrain/resource variants. Run a longer observation window through at least two in-game days to catch duplicate building, food neglect and unmaintained goals. A bounded timeout is a failed run, never an implied pass. Record precise thresholds and time limits in the harness before comparison; do not tune them after seeing results.

### P6 — Broader colony systems and prediction

- [ ] Complete existing-zone edit/delete/expand, crop/sowing changes, storage-container and special-filter workflows.
- [ ] Selectively port animal training/master/slaughter settings, medical policy, surgery and prisoner controls, and food/drug/apparel policy operations from audited AutoRim patterns.
- [ ] Audit which derived facts already exist; extend diet/access/inventory-aware food runway, uncertain harvest estimates, medical/power/mood risks and labor backlog only with observed inputs.
- [ ] Add typed event triggers and hysteresis for meaningful changes; keep bounded periodic fallback reviews.
- [ ] Extend commitments to production consumption where evidence supports it; preserve shared construction accounting.

Gate: native paused readbacks for instant settings; delayed effects stay pending. Missing diet/yield/rate data never becomes invented certainty. Repeated unchanged observations do not invoke every manager.

### P7 — Combat, world progression and strategic review

- [ ] Build the fast tactical loop on P2 actions and observed active danger, separately from slow strategic reviews.
- [ ] Add native trade-session inspect/adjust/evaluate/commit with session identity and post-trade receipts.
- [ ] Add normal caravan assembly/loading/movement, quest choices and bounded native dialog interaction.
- [ ] Add occasional same-model review of stale assumptions, stuck projects and strategic direction using existing administrator memory.
- [ ] Add emergency interruption/resumption, competing resources and multi-day progression replay scenarios.

Gate: a manageable threat is handled without a distant-insect construction veto; interrupted projects retain identity. Trade and a cargo-loaded trip complete through normal mechanics. Quest/dialog support reports unsupported paths honestly. No claim of full-game capability until demonstrated.

## The burn-down loop

1. Select the first open gate in priority order. Inspect existing code and the latest failing trace.
2. State one observed failure and its expected native outcome. Classify it: missing API, schema/discovery, stale state, model decision, native execution, verification, or inference performance.
3. Add a focused regression that would fail for that cause; implement the smallest complete fix across contract, native service, controller and UI as needed.
4. Run relevant tests and generated-contract checks. Install native changes only with the game closed; restart only the required components.
5. Reload the pinned tribal fixture with isolated controller history and fixed model settings. Record a bounded live run, including failures and unknown outcomes.
6. Inspect actual game effects, not model prose. Stop on a crash, corruption, uncontrolled placement or repeated no-progress loop; capture evidence and pause. Do not silently repair the colony and count a pass.
7. Update the iteration ledger, commit the completed slice, and continue at the earliest remaining failure. Repeat a passing run before broadening the test.
8. If the same decision failure remains after API/evidence correctness is established, compare a second small model on the identical case. Do not reflexively add managers or hardcode the observed layout.

## Iteration ledger

Append one row per implementation/playtest iteration. Link local evidence paths without committing generated artifacts.

| Slice / revision | Observed failure | Change | Focused checks | Live outcome | Remaining blocker |
| --- | --- | --- | --- | --- | --- |
| Plan / baseline 00ad80d | Starter-base success remains unproven; native coverage/routing gaps audited; stock Qwen/RimMolt overplaced sleeping spots | Establish ordered backlog and gates | Documentation review | No new gameplay run in this planning checkpoint | Start P0, then P1 |

## References

- colony-architecture-request.md: original requested architecture.
- colony-architecture.md: implementation history and partial capabilities.
- base-architecture.md: persistent master plan and shared placement.
- ADMINISTRATOR_AGENCY.md: implemented administrator inspection, wiki, memory and direct actions.
- TOOL_SURFACE_AUDIT.md and TOOL_SURFACE_INVENTORY.json: pinned third-party audit and capability gaps.
- tribal-benchmark.md and SETUP_BENCHMARK.md: existing fixture and harness limitations.

### P0 checkpoint evidence (September 7)

The comparison client is committed at 582055f. RIMAPI configuration was restored with the game closed after the player stopped LM Studio control; the previous mod configuration is backed up locally. No DLL was replaced.

Focused benchmark/client/starter checks: 11 passed. The live no-model control at `.rimbot/setup-benchmarks/20260907-162449` observed eight tribals, zero model calls/orders/new objects and paused on exit. Fixture hash still matches the saved manifest. The model baseline at `20260907-162619` used Qwen3.5-9B, 64k context, with installed DLL hash, native revision, model-load metadata and dirty source diffs captured. It failed before inference: strategy requests of 61,035 and 63,915 conservative units exceeded the 55,296 input budget. Stopped repeated retries after about 66 seconds; no orders or completed objects. This is not a gameplay pass.

Reports now separate requested target count, persisted placement attempts/native receipts, observed completed objects and unknown usable capacity. Excess object capacity no longer passes the narrow sleeping-object test. First construction-progress timing and model failures are explicit. First useful-order attribution, complete sleeping-place usability, tool latency/repetition totals, and the full starter acceptance outcome remain open P0/P3 instrumentation work. Do not label P0 fully complete yet. Next causal implementation task: reduce the strategy request to fit its budget without raising context, then rerun the pinned baseline before P1 gameplay comparisons.

### Strategy context follow-up

Strategy and daily planning now use the same compact administrator projection as arbitration. This removes duplicate crop/terrain expansion, executor construction details, repeated guide entries and precise lookup coordinates while preserving native crop choices, player direction, world facts and administrator tools. Both captured failed strategy inputs replay within the unchanged 55,296-unit input budget (53,635 and 53,624 units before fallback compaction). No tool capability was removed.

The requested 96k experiment loads Qwen3.5-9B at 98,304 context, one inference slot. The harness has an isolated `--context-tokens` override so its budget matches that load without changing dashboard preferences. Live run `20260907-164536` reached model inference and multiple tool calls; recent engine-reported inputs were approximately 17–22k tokens. Conservative budget units are not engine token counts. Goal completion is separate from this context fix. Focused administrator/replay/benchmark regression checks: 24 passed.

### P1 � explicit write routing completed

`capabilities.py` defines exact operation-to-system ownership, controller-owned planning operations, and the withheld drop-all apparel editor. Catalog construction rejects newly exposed writes without a deliberate policy; specialist validation and tool selection use exact names. Existing production bill add/update/suspend/reorder/remove and power routes are reachable without introducing another native API.

All 328 controller tests passed. A live paused-game check placed an ordinary zero-work crafting spot and added two native recipe bills on the disposable tribal colony. Administrator direct execution updated repeat count and suspended a bill with verified native postconditions; specialist execution reordered and removed it with native readback. The game stayed paused throughout. Test scratch/evidence: `.rimbot/p1-live-bills.py`, `.rimbot/p1-bills-route2.sqlite`. This verifies routing and paused effects, not autonomous colony success. The temporary spot and remaining test bill are disposable fixture changes; reload the pinned baseline before the next model comparison.

Next: P2 contextual native orders and object controls. During the bill test, material-filter output also included an overly broad list for a club recipe; investigate native filter presentation during later production/inspection work rather than treating that list as legal recipe ingredients.
