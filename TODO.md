# Current work

> Historical notes for the retired RimBot C# mod, removed from this repository.
> For the supported Python/RIMAPI architecture, see docs/EXTERNAL_CONTROLLER.md.

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

## Pawn queries and concise colony display — September 5
- Added read-only find_pawns (map-wide visible spawned pawn index, group/kind filters, stable ID pagination, optional distance ordering) and inspect_pawn (needs, active mood thoughts, direct relations/traits, visible health conditions/capacities, equipment, skills/work and animal age/training). Detail arrays carry pagination metadata. These are initial adapters; schedules, policies, pens, training eligibility, all social opinions and further animal systems remain gaps.
- Activity feed omits successful observation calls and routine review start/end messages. Full calls/results remain in the debug log. Notes are short; the strategy opens with collapsed projects and optional long-term direction. Completion details remain available by expanding a project. Old saved prose remains intact and is shortened only for display.
- Daily and strategic prompts request concrete, brief colony notes. Daily prompt: 95 words.
- Verification: production build and isolated GameSmoke build compile with zero warnings/errors; 274 regression assertions pass. Added in-game assertions for pawn pagination and detail sections, but did not execute them: RimWorld is currently running. New window layout is not visually verified. Production package generated, NOT installed. No new commit made.

## Native player API revision — September 5
See docs/PLAYER_API_AUDIT.md for the per-tool audit, native execution paths and outstanding coverage gaps. Removed hardcoded shelter/layout/material policy. Added native placement and explicit broken-spot repair, dynamic materials, current building/room observations, grouped/renamed tools, native zone/Allow/Hunt/Equip/Roof commands, configurable stockpile/shelf filters, daily planning, player steering and queued notification updates. Known tool names migrate in saved strategy metadata; removed composite strategy requirements need regeneration.
Validation: production build and GameSmoke build compile, 122 regression checks pass. New game assertions compile but have not been executed in RimWorld. No live model request made during this revision. Lower regression count reflects removal of obsolete room-template tests. Installation requested by user; production package only, never harness. No commit or push requested.

## Remove blanket hostility blocking — September 5
- Removed the objective's hostile-count => Blocked transition and its instruction to avoid construction. Removed aggregate hostile/medical counts as gates on strategic planning.
- Safety indicator now describes equipment; faction hostility is qualified as relationship data, not active attack evidence. Hostile observations include visible pawn positions, current jobs and visible job targets; no proximity threshold or replacement threat policy was introduced. The strategic hostiles metric remains for saved-plan compatibility, now explicitly described as visible standing faction-hostile pawns.
- No new draft/attack/rescue commands in this change. Missing controls apply to those specific actions, not all colony work.
- Production build: zero warnings/errors, 123 regression checks pass, 104-word daily prompt. New assertions verify faction hostility does not block objectives or change their priorities. New runtime pawn-target observations still need an in-game check. Package built; not installed or committed.

## Allow All and plan windows — September 5
- orders_allow_all invokes the native Designator_Unforbid right-click Unforbid All option. No guessed IDs, custom radius or selective item policy. IDs remain available for selective orders.
- Log evidence showed real IDs such as 3844 returned by items_list followed by invented small IDs. Added bounded observed-identifier memory to context compaction, IDs in the forbidden item sample, and explicit invalid-ID recovery guidance. This improves context continuity; it does not claim to eliminate model errors.
- Today's plan has a separate draggable window reachable from both overview and strategy, including an explicit empty state. Main, strategy, daily-plan and activity windows permit camera motion and do not absorb input outside their bounds.
- Production and GameSmoke builds compile without warnings/errors; 129 regression checks pass. Native Allow All runtime assertions compile but have not been executed; map zoom/window layout also await player validation. This package includes removal of hostility-based policy described above.
