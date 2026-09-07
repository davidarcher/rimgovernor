# Instruments reuse audit

Audited 2026-09-07. Upstream main resolves to **89c2e90fedd51419a3db55a7f9865b0aef29b270**:
https://github.com/Snowstar38/rimworld-claude-harness/tree/89c2e90fedd51419a3db55a7f9865b0aef29b270/instruments

## Recommendation

Reuse this as a library of native observations, command receipts and operational
mechanisms. Do not fork its entire agent harness. Combat and event-aware game time
are the most important missing operational layer; trading, research, letters and
layered map observations follow. None requires Core/Hands agents or a turn clock.

## Scope and evidence

- Inventoried all **54 top-level implementation modules (30,808 lines)**, their
  imports, definitions and literal native-tool references, plus **41 test files**.
- Read module contracts across the directory and inspected critical implementation
  paths for placement outcomes, trading, session generations, combat ownership,
  clock state, map targeting, vision verification and event/service lifecycle.
- Reviewed upstream playbook, efficiency audit, combat live/review notes and recent
  bug-fix notes. Their historical claims are not our gameplay validation.
- Ran upstream **test_build, test_clock, test_combat, test_game_session and
  test_play_refusals: 114 tests + 3 subtests passed** locally. These use mocks;
  no real combat or trade was performed. Optional overlay delivery attempted its
  absent local service and logged drops; it did not affect the test result.
- Static inventory is in INSTRUMENTS_INVENTORY.json. Dynamic tool strings and
  calls delegated through helpers are not a complete capability enumeration.
- This is a reuse/architecture audit, not a line-by-line security certification
  of every native implementation or proof the whole harness can run our colony.

## High-value systems and required adaptation

### Combat and actual game time

`combat.py` has a write-ahead ledger: record draft intent before the command,
confirm afterward, remember which pawns the controller is responsible for, and
clean up without silently forgetting failed undrafts. Copy this mechanism into
our session store. Baseline player-drafted pawns separately and do not undraft
them automatically. Native `home/order` already provides draft/undraft, attack,
goto, equip, rescue and tend; we should use that one execution path.

The upstream 2026-09-03 live note reports a move to the requested cell after 181
game ticks, with drafting and cleanup checks, on an impaired manhunter-tortoise
fixture. This is useful bounded evidence, **not proof of general combat tactics**.
Later notes document failed real combat and fixes. The current ledger's
any-conscious-hostile cleanup gate conflicts with our contained-insect case.
Remove that policy; keep explicit threat IDs, intent and normal native eligibility.

`clock.py` correctly distinguishes paused, running and unknown. Even running
clock state plus a queued job is not completed work: actual position, equipped
weapon or target health must be observed after ticks advance. Our current speed
control is only the first step. `play_service.py` + `home/supervised_play` provide
lease renewal and in-game event stops; `home/play_until_event` provides bounded
advancement and receipts such as pauseVerified/session changes. These are game
clock mechanisms, **not turn clocks**. Adapt the companion waiter, injury hook
and ownership lease into our existing async process. Inspect stop policies and
baseline known contained threats explicitly. Never silently resume a player pause.
Do not copy `run.py` wholesale: it mixes time control with turn budgets, broad
hostile stops and old polling policy.

### Trade

The useful part is `TradeTool.cs`, not argparse in `trade.py`: native trader
eligibility and negotiator prices, TradeSession/TradeDeal, row staging, currency
preview and final execution. Positive quantities buy, negative quantities sell.
The module guards invalid row adjustments, affordability and watched restaging.
It also explicitly defaults to skipping the negotiator's walk. Our gateway forces
`requireAdjacent=true` when opening a map trade; orbital trade needs a separate
vanilla input-path review. Purchased goods may be forbidden at the trader until
it leaves; do not infer storage/delivery from a successful deal.

We copied and compiled the native module in this slice and exposed read/session
operations with Manual/Automate classification. **No live deal completed here.**
No dedicated `test_trade.py` exists; `test_play_refusals.py` covers watched-trade
interaction, not end-to-end stock and currency correctness. Before unattended
acceptance, test buy/sell quantities, both sides' affordability, stock changes,
trader departure, stale session/load, and final native receipts.

### Observation and spatial design

`map.py` combines terrain/roof/pawns, stockpiled versus loose items, buildings,
designations and room boundaries. Its sparse rectangles and layered view are a
better input than repeated blind cell calls. Copy representation and native
`CellsPlusTool` support, not its hardcoded glyph categories as strategic truth.
It is **not a base architect**: we still need layout objectives, geometry checks,
shared reservations, entrances and staged execution.

`pawns`, `health`, `buildings`, `bills`, `zones`, `alerts` and `status` are mostly
compact projections of companion data we already have. Reuse selective formatters
in structured tool responses and dashboard details. Keep null/unavailable values,
truncation counts and native identifiers. Do not feed all reports at every review.
`inv.py` makes the right nutrition-versus-units distinction, but uses a vanilla
nutrition table and assumed intake. Read nutrition/needs from the native game
before porting its food-security calculation. `research.py` and `world.py` expose
important missing strategic context through ResearchTool and WorldTool.

### UI, events, memory and vision

`pick.py` documents a real native-click hazard: success can mean the old selection
remained because the cell was off-screen or an armed designator ate the click.
Copy selection verification and camera checks only for UI fallback operations.
`ui.py` surfaces/text targets, `letters.py` choices, `dialog.py` fields and packed
furniture install are useful missing actions. Avoid string-matched automatic
letter dismissals and decorative viewer delays. Opening a tab while inspecting
is a UI mutation, not a pure read.

`event_bus.py` offers a durable SQLite outbox with acknowledgement; use its
semantics for player and game events without agent routing. `game_session.py`
stamps observations by load generation and detects rewinds. Our added saved
colony ID plus per-load token goes further for persistent objectives. Retain
intent across a saved-colony reconnect but invalidate observations on load.
`runtime_binding.py` adds process birth time to prevent PID reuse errors.

`look.py`/`see.py`/`combat_camera.py` provide real image acquisition and framing.
Adapt for the loaded local VL model and yield to human camera input. Reject fixed
external reviewer cadence, mandatory screenshots per turn and model-escalation
services. `verify.py`'s native checks are useful, but its regex classifications
and NO FIRE conclusion from an absent alert are not universal ground truth.

### Scouts versus a blind screenshot reviewer

The legacy data-scout briefs in `rota.py` start in a clean context, ask a bounded
set of questions, and seed compact building, pawn and room observations plus a
recent handback. `scout_snapshot()` captures fixed data before inference; the
scout interprets it rather than executing arbitrary generated shell commands.
Reports are short, disclose filters/caps, and carry session/time stamps. This can
save administrator context by keeping the raw investigation in a separate request.
It does not eliminate inference cost, and parallel requests can contend on one
local GPU. Some old briefs are stale (for example, claiming work priorities are
unreadable) and should not be copied verbatim.

The automatic seeded-scout path is disabled in the reviewed revision. The live
`rota_service.py` launches a blind image reviewer at startup and fixed 180-second
wall slots. `look.py` sends near/wide screenshots without the current strategic
plan or the action narrative. That independence may notice visual contradictions
that the planner overlooks. Its findings are leads, not verified world state;
`verify.py` checks some claims, and preserves uncertainty for others.

Recommendation: deterministic compact state projections first; read-only,
on-demand scouts for specific unresolved questions. Retain observation IDs,
filters and freshness in a compact report. Add a blind local VL pass after major
layout changes or stalled progress, not every timer tick. Use the same loaded
model where image input works. Neither mechanism needs a new manager hierarchy,
external model service, a mandatory WEIRD line or an extra approval stage.

## Do not carry these behaviors across

- `turnclock`, Core/Hands dispatch, Claude lifecycle hooks, consultancy escalation,
  secondary summary model, fixed scout schedule and external Twitch relay.
- Global hostile-presence bans or treating a paused queued job as executed.
- `gear.py`/PawnConfig's instant apparel drop, which bypasses ordinary pawn labor.
- Numeric nutrition/metagame tables as game facts, unknown values becoming zero,
  screen-resolution assumptions, or prose-derived pawn/target identity.
- Native watch delays and camera movement on every order. Keep feedback cheap.
- A second transport stack, sidecar daemons and duplicate legacy action paths.

## Current implementation versus next work

Already present before this audit: MCP SDK transport, observation companion,
normal direct order tools, buildings/bills/zones/pawn settings, dashboard/chat.
This migration slice adds save-backed identity, durable projects with
native building/zone observations, cancellation, placement outcome text adapted
from build.py, normal model-facing speed control, and the native trade module.
It does **not** yet port draft cleanup ledger, supervised event control, research,
world context, rich inspectors, strategy retrieval or visual architecture.

Recommended implementation order:
1. Complete identity/project acceptance tests and real placement reconciliation.
2. Draft ownership ledger + actual game-time lease/event interrupts; live move,
   equip, rescue/tend and contained-hostile scenarios. Keep one planner/writer.
3. Native trade acceptance test, research, world, letters and CellsPlus.
4. Compact role-specific observation projections and native nutrition.
5. VL layout planning/reservations, informed by the layered map; starter-base test.

## Module-by-module disposition

All 54 implementation files are accounted for below. “Adapt” means extracting
functions/data contracts into our runtime, not launching the upstream CLI.

| Module | Lines | Disposition | Reuse or constraint |
|---|---:|---|---|
| `act.py` | 1177 | Adapt next | Resolve Architect labels once, reject ambiguity, distinguish dropdown/child, keep width/height explicit. Use live definitions, not aliases as substitutions. |
| `alerts.py` | 698 | Adapt next | Compact culprit IDs and priority ranking. Keep target truncation visible; do not extract pawn identities from prose. |
| `bills.py` | 807 | Adapt next | Format recipes, ingredient shortfalls and bill configuration over our existing home/bills. No second execution engine. |
| `build.py` | 543 | Partly copied | Copied reason/outcome/verdict formatting. Still useful: rotation/vent-side reports, material shortfalls and bounded suggestions. |
| `buildings.py` | 1614 | Adapt next | Pending work, materials, power-net topology and inspect-string summaries. Direct native reads exist; UI gizmo execution needs separate review. |
| `cam.py` | 650 | Adapt later | Home framing, camera bounds and human override. Remove fixed machine paths, leases tied to Hands and decorative zoom glides. |
| `camlock.py` | 257 | Adapt idea | Use a single local camera owner with human override, not a multi-agent camera mailbox. |
| `cell2px.py` | 26 | Skip | Assumes a particular fullscreen geometry and OS input path. Prefer native semantic targets. |
| `chat_events.py` | 133 | Skip for now | Audience moderation relay is not local player chat. Preserve untrusted-source tagging if streaming is added. |
| `click.py` | 35 | Skip | Windows pixel input for exceptional setup screens; not a general gameplay API. |
| `clock.py` | 290 | Adapt now/next | Reuse paused/running/unknown distinction. Strip supervisor-on-disk inference and turnclock remedy strings; flags alone do not prove job completion. |
| `combat.py` | 1717 | Adapt high priority | Write-ahead draft ownership, verified native orders, guarded advancement and cleanup. Remove blanket map-hostile cleanup gate, turn ownership and extra services. |
| `combat_actions.py` | 178 | Adapt selectively | Equipment tickets separate issuing a job from actual equipment. Prefer current home/order over older context-menu movement paths. |
| `combat_camera.py` | 188 | Adapt later | Frame participants while time advances; relinquish on external pan/zoom. Keep camera changes outside model critical path. |
| `consult.py` | 170 | Skip | External Codex consultant/model escalation conflicts with the chosen local single-model direction. |
| `dialog.py` | 128 | Adapt later | Native dialog-field enumeration and ambiguity rejection; requires DialogTextTool. Audit reflection setters per dialog before broad exposure. |
| `event_bus.py` | 312 | Adapt high priority | SQLite durable outbox, acknowledgement and retries. Replace Core/Hands routing with our controller inbox. |
| `game_session.py` | 118 | Adapt principle | Generation-stamped observations and rewind invalidation. Our saved colony ID plus per-load token supports memory beyond a loaded-session generation. |
| `gear.py` | 183 | Adapt reads only | Compact inventory/equipment. Do not copy instantaneous apparel drop: upstream explicitly bypasses the ordinary pawn job. |
| `health.py` | 183 | Adapt next | Pure health/needs formatters and sort keys over native pawn records; preserve unavailable values. |
| `human_events.py` | 81 | Adapt principle | Reliable player-message delivery through tool boundaries. Existing chat revision checks cover part of this; durable acknowledgement remains useful. |
| `inv.py` | 906 | Adapt selectively | Ownership, holders and loose/stockpiled partitions are useful. Replace vanilla nutrition tables with native def stats before food forecasting. |
| `letters.py` | 1064 | Adapt high priority | Read choice-bearing letters, select native options, acknowledge/dismiss appropriately. Remove viewer holds and automatic stale-letter policy. |
| `look.py` | 610 | Adapt later | Near/wide visual observations with game-evidence verification. Use the loaded local VL model; no mandatory external reviewer or fixed 180-second schedule. |
| `luna.py` | 386 | Skip | Extra model call to compress a fixed overlay box. Our resizable dashboard and concise output can avoid this cost. |
| `map.py` | 2803 | Adapt high priority | Layered compact map, sparse/rectangular queries, terrain/roof/storage/rooms/designations. Requires CellsPlus. This is a representation, not a base-layout solver. |
| `mini_install.py` | 145 | Adapt later | Packed furniture validation and two-stage item/destination targeting. Prefer a native install action over pixel placement when available. |
| `move.py` | 220 | Mostly skip | Older select/right-click/draft path with camera/modal dependencies; current home/order goto is the preferred route. |
| `order.py` | 1046 | Adapt high priority | Native job refusal formatting, exact selector resolution, force/menu fallback discovery. home/order already compiled; no need to copy CLI parsing. |
| `overlay_client.py` | 546 | Adapt principle | Non-blocking presentation and bounded delivery diagnostics. Keep one same-origin dashboard, not a second overlay server. |
| `pawns.py` | 1335 | Adapt next | Work, schedule, animals, social relations, health and roster projections over existing native payloads. Drop unsupported instant gear mutations. |
| `pick.py` | 403 | Adapt for UI fallback | Selection verification and camera visibility are essential when forced to click. Do not involve selection for direct native queries. |
| `play.py` | 385 | Adapt high priority | Durable game-time ownership and event stops; distinct from turn clocks. Bind to our process rather than a Core/Hands session. |
| `play_service.py` | 392 | Adapt high priority | Companion lease heartbeat and event cursor. Merge into our async runtime instead of launching another Python service. |
| `relay.py` | 113 | Adapt UX only | Player message plus explicit pause/interrupt. Existing local chat already supplies the message path. |
| `research.py` | 493 | Adapt next | Current research, benches, prerequisites and unlocks, plus normal selection. Requires ResearchTool; do not invent an equivalent Python simulation. |
| `rim.py` | 362 | Skip transport | We already use the MCP SDK over stdio. Do not add a second hand-written HTTP transport or legacy argument shims. |
| `rota.py` | 2301 | Skip scheduler; mine formatters | Legacy scout/brief formatters may help; seeded multi-agent scheduling and report mailboxes are not needed. |
| `rota_service.py` | 308 | Skip | Fixed wall-clock external vision reviewer daemon is unnecessary for our current runtime. |
| `run.py` | 469 | Do not copy whole | Useful native event waiter, but old client pulse loop includes turn limits and any-hostile stops. Rebuild thinly around reviewed companion event semantics. |
| `runtime_binding.py` | 181 | Adapt principle | PID plus process birth time prevents stale owner/PID reuse. Use in launch/session ownership checks, not Claude agent identity. |
| `runtime_hook.py` | 363 | Skip | Claude lifecycle hooks and cross-agent delivery are platform-specific. |
| `say.py` | 63 | Adapt style only | One concise outcome per intent; no mandatory character cap, first-person persona or mood service. |
| `see.py` | 112 | Adapt later | Actual image input plus framing, not a path string. Remove per-turn screenshot quota. |
| `setup.py` | 1107 | Adapt selectively | Health/readiness, save validation and explicit ownership recovery. Keep our profile/launcher and avoid hardcoded colony save prefixes. |
| `status.py` | 739 | Adapt next | Compact native status and meaningful deltas. Do not copy prose-derived global stopping policy. |
| `stream.py` | 936 | Skip orchestration | Goals/chat presentation is useful and partly adopted; Hands lifecycle, turn counters and service start orchestration are out. |
| `trade.py` | 570 | Native module copied; adapt formatter | Native TradeSession lifecycle, signed quantities, preview and accept. Keep adjacency for map traders; verify stock, price and receipt changes. |
| `turnclock.py` | 334 | Skip explicitly | User rejected per-agent elapsed-turn budgets; unrelated to actual game-time control. |
| `ui.py` | 979 | Adapt fallback | Structured text/surface IDs, main-tab cleanup and selection readback. UI reads that open tabs are mutations and need serialized ownership. |
| `verify.py` | 529 | Adapt cautiously | Validate visual claims with native evidence. Regex topic gates and absence-of-fire-alert => no fire are not general proof. |
| `watch.py` | 474 | Mostly skip | Older broad polling loop overlaps native event waits. Do not copy range/hardcoded policy or automatic letter handling wholesale. |
| `world.py` | 165 | Adapt next | Biome/growing-season/settlement context directly from native world state; requires WorldTool. |
| `zones.py` | 481 | Adapt next | Zone/filter/crop/occupancy summaries and anomaly visibility. Core native zone tools already present; repair is an explicit mutation. |

## Provenance

Pinned upstream has no repository license file in the reviewed checkout. Reuse
here follows the user's explicit local-research authorization; this audit does
not imply permission to redistribute upstream source. Preserve provenance for
copied functions and modules. Do not import their personal playbook as controller
instructions: its named people, paths, colony rules and agent roles are context
for their setup, not requirements for ours.

## User-approved queue

Visual second opinion and an on-demand data-scout tool are queued after the
current operational migration. See RIMBRIDGE_MIGRATION.md for acceptance criteria.
These are advisor requests with independent context, not additional order writers.

## Local migration validation

The native smoke script passed save/reload identity continuity with a changed
load token, immediate one-cell zone/project completion, draft/undraft readbacks,
and actual clock advancement followed by pause. Trader listing succeeded; a
completed live trade, combat tactics, guarded event advancement and draft-ledger
cleanup remain unvalidated/unported as described above. This is narrower than
claiming the entire upstream combat/trading system works here.


### Follow-up: native clock installed

The native supervised-play watcher is now integrated with the Python controller,
including independent heartbeat renewal, event delivery, explicit external-pause
release, and proximity-scoped hostile detection. The repeatable native clock smoke
passed lease expiry, external pause, movement and draft cleanup. Combat victory and
trade transaction validation remain open; visual review and data scouts follow.
See `NATIVE_CONTROL_CHECKPOINT.md` for the current boundaries.
