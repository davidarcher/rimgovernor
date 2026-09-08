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
