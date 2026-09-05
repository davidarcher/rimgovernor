# Development guidance

Read AGENTS.md. The supported runtime is now the external Python controller and
customized RIMAPI Dashboard. See docs/EXTERNAL_CONTROLLER.md and THIRD_PARTY.md.

- Commit completed iterations after relevant checks; never push unless requested.
- Preserve normal game rules. Keep editor/cheat endpoints out of model execution.
- Target local LM Studio/Qwen. No silent paid-provider fallback.
- Game execution belongs to RIMAPI; do not rebuild a simulation in the controller.
- Keep upstream API names/contracts. Discover capabilities rather than inventing
  bespoke shelter/room/farm strategies as game-side actions.
- Distinguish HTTP acceptance from actual game outcomes. Never retry uncertain
  writes without observing state; work history does not own map coordinates.
- Manual, player steering, game/save/map changes invalidate pending reviews.
- Keep UI drafts and visible data in place during background refreshes.
- Build/test with build.ps1. launch.cmd starts the controller, quicktest and UI.
- Generated artifacts, local databases and logs are ignored. Do not commit them.
- Protocol/fixture tests are not a substitute for measured live gameplay.
- Native game integration belongs in integrations/RIMAPI.
