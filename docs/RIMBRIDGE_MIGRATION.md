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
