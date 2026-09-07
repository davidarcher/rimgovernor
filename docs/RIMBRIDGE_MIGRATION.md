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
