# Native bridge migration

RimBridge is the sole backend. The CLI, launcher and dashboard no longer select
or import RIMAPI. Retired source is available at Git checkpoint `9209b74`;
local submodule branches were also preserved in `.rimbot/rimapi-retired.bundle`.

## Implemented

- Native MCP/GABS transport with discovered input schemas.
- Typed compact observations; raw receipts retained separately.
- Companion zones, construction, bills, pawn settings and orders.
- Direct planner, player steering, Manual/Automate and initial planning pause.
- Integrated 8787 dashboard: game snapshots/chat/summary, Projects, Activity.
- Schema validation, explicit previews, no godMode, post-command observations.

## Next acceptance milestones

Combat execution now has repeatable headless melee/injury-stop, ranged-hit and
wound-treatment tests with selected AI-owned stand-down. See
`NATIVE_CONTROL_CHECKPOINT.md` for the precise observed outcomes and remaining
rescue/hostile-victory acceptance cases. These are execution tests, not model tactics.

1. Validate normal construction, instant zones and settings end-to-end through
   the current planner. Check duplicate/no-op receipts and pawn completion.
2. Restore durable objectives and project lifecycle against native state: cancel,
   deduplicate, reconcile and show current blockers. Do not revive HTTP adapters.
3. Restore session memory using native game identity, including new-game resets.
4. Connect strategy knowledge and the visual architect/shared placement
   reservations using the current game's native queries and tools.
5. Add selected-pawn/colony inspectors backed by native schemas.
6. Run the repeatable eight-tribal starter-base test through completion, then
   benchmark latency and model context before expanding scope.

Historical architecture and prior-art research remains in `archive/rimapi`.
Those documents describe the retired system, not currently available features.
The current launch and dependency instructions are in the root README.


## Persistent project and instruments slice

Added native save-backed colony identity and per-load tokens, SQLite restoration
of chat/plans/projects for that colony+map, player project cancellation and
observed building/zone targets. Completed targets reopen when removed; blueprint
and frame IDs may change without losing the target. Matching includes explicit
material when supplied. Untargeted objectives remain planned, never auto-complete.
The existing save must be saved after the identity component is attached for its
ID to survive a game restart; the unsaved baseline remains a fresh colony.

Added native TradeTool and normal model-facing speed control. Combat native orders
were already present; guarded event timing and draft-ownership cleanup are still
next work. See INSTRUMENTS_REUSE_AUDIT.md for all 54 instrument decisions.


## Queued: context-saving reviewers

The data scout is now implemented through the optional generic analyst. The
strategist's `scout` tool starts an independent read-only investigation, validates
native query schemas and evidence references, and retains raw evidence only in
the diagnostic audit. Every read and report is guarded by load/direction revision.
It has six native reads and a bounded evidence context; failed investigations do
not become advice. Configure `analyst` to advertise it, including the same local
model as strategist if desired. No extra model is loaded implicitly.

Validation: 48 controller tests; real local Qwen 9B/native query smoke completed
with unchanged paused game tick. That run took 9.7 seconds, eight model calls and
six reads, returning roughly 2 KB of findings/source metadata instead of 15 KB of
raw evidence. This demonstrates isolation and context reduction, not lower total
inference cost. Query efficiency and a controlled blocked-construction fixture
remain acceptance work. `scripts/native_scout_smoke.py` repeats the live check.

Player-requested next slice: headless test launch and measured throughput, using
HeadlessRim as research; retain native bridge semantics and normal interactive mode.

After the current persistence/operational instruments work:

1. **Visual second opinion** — a separate clean-context local VL request after a
   substantial layout change or stalled project, not a fixed wall-clock daemon.
   Capture relevant near/wide views with native map coordinates; yield camera
   ownership to the player. Return a small set of concerns, cited image regions
   and confidence. Verify actionable claims with native queries before orders.
   Acceptance: notices a deliberately missing entrance/zone overlap, does not
   promote uncertain image interpretation to fact, discards old-load reports,
   and measures latency/context cost on the loaded model.
2. **Data scout tool** — on-demand, read-only investigation of a concrete admin
   question. Prefer deterministic summaries before inference. Give it a bounded
   native query surface and its own context; return evidence IDs, tick/load token,
   filters/caps, concise findings and missing facts. No orders or approvals.
   Acceptance: diagnoses a blocked construction/bill from native evidence, keeps
   raw investigation out of admin history, rejects stale reports, preserves
   uncertainty and adds less overhead than the admin doing the same investigation.

Use the existing local model where its loaded capabilities allow; do not require
another provider/model, introduce Core/Hands, or revive turn-clock scheduling.


Validation for this slice: 23 controller tests, 2 React tests, typecheck and
build passed. `scripts/native_migration_smoke.py` passed real save/reload identity,
instant zone completion, draft cleanup, running/paused clock and trader listing.
The script uses a disposable save in the isolated profile and no model calls.

## On-demand visual second opinion

The strategist now gets `visual_review` when the optional architect model is
configured and rendering is enabled. It captures the current camera view freshly
without moving it, sends an independent one-shot image/question request, and
retains at most five concerns with normalized image rectangles, confidence and
required native verification. No plan rationale, colony-state blob, recursive
queries or write tools enter that reviewer request. This uses the existing
architect capability and may use the same vision-capable local model.

The report carries image hash, load token, capture time and a post-capture game
tick explicitly labeled non-atomic. Load/direction changes discard the result;
headless mode refuses and does not advertise the tool. The report appears in the
existing consultation activity and can be cited as used advice. Concerns remain
advisory; the strategist must check native facts before committing actions.

Validation: 82 controller tests, including fresh capture plumbing, stale-report
rejection, invalid image regions, no-report rejection and headless exclusion.
No live VL inference or missing-door detection claim in this slice. Deliberate
layout-error acceptance, actual loaded-model latency, and UI image-region display
remain backlog. Camera framing is intentionally the current view; automatic
near/wide positioning and player camera-ownership detection are not implemented.

## Visual reviewer: first live result (2026-09-08)

`scripts/native_visual_smoke.py` creates an ordinary 7x7 wall-blueprint perimeter
without a door on the disposable tribal baseline, frames it, and asks a neutral
usability question. Placement uses each native rotation's accepted flag, not the
request-level success flag. It runs the configured test Qwen 9B with a clean
reviewer context, preserving paused ticks and an unchanged strategic plan. The
script records the fixture, report, camera state, inference metrics and a simple
entrance-mention check; a human must judge accuracy. It does not build instantly.

First completed live run: 8.69 seconds end to end, 4,347 input tokens, 440 output
tokens, one model call. The model missed the doorless perimeter. It asserted
impassable cliff/pit terrain with high confidence and inferred remoteness despite
nearby colonists. The screenshot also has a ruin abutting part of the perimeter,
so a cleaner isolated fixture is needed for subsequent comparisons. These are
not accepted actionable facts. Native legal-placement checks do not prove overall
room accessibility, but neither does the model's interpretation prove a cliff.

Result: capture/local inference/structured report worked; visual-quality acceptance
FAILED. Do not treat this as a reliable autonomous layout inspector. Keep it
optional and require native verification. Current evidence is
`.rimbot/bridge/visual-smoke.json` and the native review PNG in profile/Screenshots.
Next: isolate fixtures from existing ruins, compare absent/present entrances, and
expose the source image plus concern regions for player review. No model was
trained or prompt tuned to force the expected answer in this run.

Follow-up camera check: map ID, center and root zoom were unchanged, but the
reported visible width changed from 49 to 93 cells (height stayed 27). The cause
was not established. The fixture now separates position/zoom invariance from a
recorded viewport change instead of treating both as the same assertion. This
run does not prove stable framing; image regions refer only to the captured PNG.

## Deterministic trade step

The strategist can commit `trade` with an observed trader, negotiator, named
signed item quantities and maximum net silver spend. Hands opens an ordinary
adjacent map-trader session, stages each line, checks the native preview and
executes once. No model handoff is needed between those operations. Positive
counts buy; negative counts sell. This first compiler is item barter/purchase,
not gifts or pawn selling, and rejects old session row indices.

An existing/unknown session is not replaced. Rejected lines, changed participants
or staged contents, unreadable affordability, either side lacking silver, and
budget overrun stop before acceptance. Completion requires executed and
actuallyTraded plus matching native moved rows; it does not imply purchased goods
were hauled into storage. The usual pre-write journal and load/direction guards
apply. Lost receipts are never retried automatically. A blocked partially staged
session is left for inspection/cancellation, not silently cancelled after a load
or player change. Native session calls are sequential, not an atomic transaction
against concurrent player edits; a mismatched final receipt is reported for review.

Validation: 92 controller tests covering sequencing, budgeting, preview drift,
existing sessions and lost receipts. `scripts/native_trade_smoke.py` passed actual
headless native discovery/status and no-session refusal. No live exchange was
completed; trader fixture, buy/sell stock and silver deltas remain acceptance work.
No DLL changed. Native quests and transport after trading retain upstream behavior.

## Research migration

`home/research` is now available for project/prerequisite discovery, optional
unlocks, research benches and worker capability. The planner can commit a native
project selection. Read-only calls work in Manual; writes require Automate and
explicit dryRun=false. Native nested refusals are errors, not successful steps.
A fresh read must confirm the resolved project in the main or knowledge-category
slot before execution reports success. Selecting is not completing research.

Validation: native build/install, 98 controller tests, and
`scripts/native_research_smoke.py` on the real headless tribal fixture. Selected
ComplexFurniture while paused, checked fresh native state, confirmed replay was a
no-op, and rejected an invalid project. No research progress was added. Actual
research labor/completion and Anomaly-category selection remain untested.
Evidence: `.rimbot/bridge/research-smoke.json`.

## World context migration

`home/world` is available to the strategist and generic data scout for native
biome, temperature, growing-period and nearby settlement facts. It is on-demand,
not added to every colony snapshot. Both query gateways reject show:true, so this
integration never toggles the player's world view. Settlement radius filtering
and unreadable/null values retain the native response semantics. Tile distance
is not caravan travel time, and growing period is not actual harvest yield.

Live testing found the imported tool queried the player faction's relationship
toward itself, triggering RimWorld GetSituations errors. Self-faction relation
and goodwill now return null with isPlayer=true instead; other faction reads are
unchanged. The corrected run read the temperate-forest tile and 60-day native
growing-period label, verified radius-zero filtering, kept paused ticks unchanged,
and confirmed no world view was displayed.

Validation: native build/install, 101 controller tests and
`scripts/native_world_smoke.py`. Evidence: `.rimbot/bridge/world-smoke.json`.
No caravan movement, settlement interaction or model strategic decision was tested.

## Native letters

The strategist can inspect `rimworld/list_letters` and commit native open/dismiss
operations using observed letter IDs. These use RimBridgeServer's existing
left-click/right-click equivalents; no copied notification implementation or DLL
change is needed. Opening a dialog is a write. Dismissal must return the matching
native ID and dismissed=true, then a fresh untruncated letter list must show that
ID absent. Unknown identities or incomplete lists cannot certify removal.

There is no automatic sweep, age-based dismissal or quest-choice execution.
Removing a letter does not resolve its event. Existing native notification push
and clock interruptions remain unchanged. Actual dialog choices need a separately
reviewed action path; this slice does not claim acceptance of quests.

Validation: 107 controller tests and `scripts/native_letters_smoke.py` on the
headless fixture. Native full listing and nonexistent-ID refusal passed, with an
unchanged stack. No real letter was opened/dismissed in this fixture; those live
cases and choice dialogs remain acceptance work. Evidence:
`.rimbot/bridge/letters-smoke.json`.

## Filtered spatial inspection

Migrated `home/get_cells_plus` into the reviewed native surface and generic scout.
It supports extent/inclusive-corner rectangles, selected cell/thing fields, sparse
content and summaries, with native omission counts and zone/area lookup tables.
It is on-demand and does not inflate the default observation. The detailed native
cap is 1,024 cells; summary mode can scan the map and reports that scope explicitly.
No glyph-based terrain categories or strategic rules were copied.

Planner guidance calls for fog awareness and warns against treating sparse output
as complete geometry. Upstream fogged=false is omitted when that field is selected;
fieldsApplied identifies which fields were requested. Existing discovered schemas
and response-size limits apply. This is state inspection, not a new placement API.

Validation: native build/install, 107 controller tests, and actual
`scripts/native_cells_smoke.py`: matching extent/corner results over 25 cells,
sparse accounting, summary behavior, selected fields, unknown-field refusal,
conflicting-bound refusal and oversized detailed-query refusal. Equivalent redundant
bounds are accepted by the native parser. Paused ticks stayed unchanged. Evidence:
`.rimbot/bridge/cells-smoke.json`. Better planner layout/latency is not yet measured.

### On-demand strategy library (2026-09-08)

The strategist can now search the 20 checked-in strategy cards and read one matching card at a time. Search returns at most three summaries; full cards retain applicability, verification, reconsideration and dated wiki sources. No corpus is preloaded into the strategy prompt. Retrieval is local and deterministic, with no extra model or game calls. Unknown IDs cannot resolve to filesystem paths. Removed obsolete starter-guide API terminology and old placement-contract assumptions.

Validation: 118 controller tests passed, including poor-soil, bedroom and startup retrieval, budgets, source preservation and invalid IDs. No live model/gameplay improvement measured in this slice. Live wiki retrieval remains separate unfinished work.

### Strategist notebook (2026-09-08)

Added an on-demand read/write/delete memory tool using the existing colony-and-map SQLite state. At most 20 notes, each with 1000 characters of text and 500 of required evidence; full notes are never automatically injected. Descriptive IDs allow deliberate updates without duplicate accumulation or silent eviction. Reads identify notes from another load, including loading an older save. Notes are advisory, not orders or authoritative current facts. Writes reject stale colony/load or player-direction revisions and record activity events. The memory index is included in strategy context; player-facing notebook editing remains future work.

Validation: 127 controller tests passed, including persistence round trip, load provenance, stale request refusal, capacity, validation and read isolation. No gameplay or model-quality improvement measured yet.

### Player notebook (2026-09-08)

The dashboard now has a Notebook tab showing strategist notes, evidence, recording tick and a previous-load warning. Players can forget individual notes; corrections can be given through chat. Deletion requires the displayed colony/load and note fingerprint, refuses stale requests, advances the direction revision and wakes Automate to reconsider. This prevents an in-flight review from committing against a forgotten assumption. Notebook content does not enter the default full model prompt; the strategist still sees only its index.

Validation: 129 controller tests, four dashboard tests, TypeScript check and production build passed. Tests cover stale deletion, pending-review invalidation, local route protection, version forwarding and visible error handling. Restarted headless controller is connected in Manual mode with the real empty notebook exposed. No new gameplay outcome measured. Direct note editing and live wiki retrieval remain future work.

### Live wiki reference (2026-09-08)

The strategist now has wiki_lookup: search (three results), read a page's section list, then read a selected numeric section (6000 characters, explicit truncation). It uses the public MediaWiki API at a fixed HTTPS origin with no HTTP redirect following, a 15-second timeout and a 1 MB response ceiling. Page redirects are resolved by MediaWiki; returned title, revision, URL and retrieval timestamp identify the source. HTML is converted to plain text. External prose is advisory, never an action contract or current colony state. No wiki corpus is preloaded and no model intermediary is involved. Current-load/direction checks apply after retrieval.

Validation: 134 controller tests passed; live search for bedroom, redirect from Bedroom to Rooms, section list (25 entries) and introduction read (666 characters) succeeded. The wiki does not provide the extracts extension, so section parsing is used. No LLM strategy-quality or gameplay improvement measured. Search is live rather than cached; repeated-query caching and richer table presentation remain possible follow-ups.

### People inspector (2026-09-08)

Added the People dashboard tab using the existing typed native observation, with no additional game or model queries. The roster shows current native job, weapon and flagged conditions; selecting a colonist exposes mood, food/rest needs, tending, bleeding and position. Unknown readings remain unknown, zero needs remain zero, and no-job does not imply idle. Last-observation and non-atomic collection notices are shown. Selection clears with the loaded session; removed pawns no longer retain displayed details.

Validation: six dashboard tests, TypeScript and production build passed. The live controller is connected in Manual mode and supplies all eight tribal colonists with current job/equipment/health fields. No new gameplay test or visual browser inspection performed. This is a compact read-only inspector; skills, traits and detailed health/work settings remain future extensions.

### Instant construction acceptance (2026-09-08)

The extended scripted-strategist/native-hands fixture reproduced a real bridge bug: SleepingSpot became Blueprint_SleepingSpot with zero work, while the planner waited. Inspection of the installed game's Designator_Build.DesignateSingleCell confirmed its ordinary ThingDef branch directly creates structures when WorkToBuild equals zero. PlaceBuildingTool now follows that rule without consulting god mode or enumerating sleeping definitions. This slice covers ThingDefs, not zero-work TerrainDefs. Existing cosmetic style selection limitations remain.

Hands now reconciles only the newly issued construction project immediately. Observed built structures complete immediately; blueprint/frame projects still wait for pawn work. The repeatable fixture asserts stockpile plus sleeping spot completion while paused, unchanged ticks, and no duplicate writes on replay. It uses a scripted strategic decision, not a live model, and does not establish room-construction or model layout quality.

Validation: native build/install, 134 controller tests, and repeated headless strategy smoke passed after reproducing failure before the fix. Evidence: .rimbot/bridge/strategy-smoke.json. No saves or generated artifacts committed.

### Mixed construction acceptance (2026-09-08)

Expanded the native strategy fixture to cover human and animal sleeping spots plus a normal wooden wall. Both zero-work structures complete while paused. The wall first refuses forbidden starting wood; the fixture discovers and applies the native Allow designator at one observed nearby wood stack, verifies allowed stock, and explicitly retries the same committed step. The resulting wall remains a blueprint with positive work, not an instant structure. Replaying issues no duplicate orders, and ticks remain unchanged.

This is a scripted two-decision acceptance test, not live LLM planning or finished pawn construction. Test definitions/materials are fixture choices, not production policy. An initial attempt to use building_config for a loose item correctly failed: it only selects buildings. The native Architect Allow path succeeded. No production action behavior changed in this slice.

Validation: scripts/native_strategy_smoke.py --headless passed; evidence in .rimbot/bridge/strategy-smoke.json. Colony construction by pawn labor remains the next acceptance gap.

### Pawn-built construction acceptance (2026-09-08)

The strategy smoke now supports --build: after its paused instant-versus-blueprint checks, it advances ordinary Superfast time (no ultra boost) for at most 120 seconds and observes the wall project. A finally block pauses the game. Success requires the exact cell to contain one built wall and the committed plan step to be complete. It then replays Hands and requires unchanged action/model-call counts. Failure evidence includes sampled jobs and final native building state.

Two live headless runs passed with ordinary hauling/construction, without forced jobs, work-setting changes, resource spawning or instant completion. Existing fixture wood is allowed through the native designator. This closes single-wall pawn-labor acceptance, not a full starter base or model-selected construction. Strategist decisions remain scripted; no claim about LLM quality. Evidence is saved to .rimbot/bridge/strategy-construction-smoke.json. No production behavior changed in this slice.
