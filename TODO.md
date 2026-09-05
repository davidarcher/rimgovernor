# Current work

- [x] Single colony manager; upstream ownership, pawn deletion and difficulty cheats removed.
- [x] LM Studio tool adapter; no silent cloud fallback.
- [x] Manual/Automate only; automatic reviews follow mode; no Run one review or approval checkbox.
- [x] Compact objective rows with tooltips, embedded recent activity, expanded draggable activity window.
- [x] Uncapped local turns/actions; optional hourly budget; bounded context compaction.
- [x] Forbidden item visibility and normal area Allow action; explicit material discovery.
- [x] Complete 7x7 room ordering with material/layout preflight, bed aisle, doorway and roof designation.
- [x] Explicit roof-area and visible blocker tools; no roof-building search guesses.
- [x] Build succeeds with 269 regression assertions, including closed room perimeter, bed footprints and doorway reachability.
- [ ] In-game verification of this UI and room/roof tools; current user game was running during the build. Earlier hidden screenshots were black, not usable visual evidence.
- [ ] Compare reasoning enabled/disabled against the same decision fixtures and colony state.
- [x] Saved season/year/three-year strategy, validated project dependencies/completion, automatic scheduling, reasoning/daily separation and strategy UI.
- [x] Map-wide filtered item query with nearest-first pagination and ID-based Allow.
- [x] Local requests no longer send a mod output-token cap.
- [ ] In-game and live-model validation of strategy (not run while the user tests the installed previous build). Reasoning support is requested, not yet confirmed for the loaded model.
- [ ] General layout planning and staged project execution; the current room tool is a small starting capability.
- [x] Starter equipment, growing zones, production/butcher bills, hunting designations and research selection.
- [ ] Medical/prison settings, combat orders, broader completion checks and longer autonomous play.
- [ ] Longer play testing, resource reservations and path/access validation.

- [x] Persistent base location, nearby site discovery, adaptive rectangular shelter options and normal phased excavation.
- [x] Regenerate strategy button and a development-oriented default direction.
- [x] Corrected pending-bed counting: only Building_Bed-derived definitions count; ordinary wall blueprints no longer satisfy shelter demand.
- [x] Isolated quicktest verified nearby structure reuse and idempotent shelter orders; follow-up covers crop zones and variable-product butchering bills. Final result recorded with the checkpoint.

## Checkpoint verification

- Production build: 269 regression assertions passed, zero compiler warnings/errors.
- First isolated quicktest: nearby structure reuse, shelter-order idempotence, stockpile idempotence, normal pawn count/speed, and crop/research/workstation discovery passed.
- That test exposed wall blueprints being counted as beds; type-based counting was corrected and rebuilt.
- Follow-up test with crop-zone and butcher-bill mutations stalled during game startup before assertions. It was stopped; those new mutation assertions remain unverified in game.
- Production DLL restored and verified; the test harness is not installed.
- Live reasoning/model quality and full UI/longer-play verification remain pending.
- Next architecture: audit and expose existing player APIs, especially pawn/social/mood and animal state. See docs/PLAYER_API_DIRECTION.md.
