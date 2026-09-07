# Companion reuse and the next backend slice

Reviewed Snowstar38/rimworld-claude-harness at `89c2e90fedd51419a3db55a7f9865b0aef29b270`, especially `companion/src`, against our existing RIMAPI controller and the live RimBridgeServer trial. The user authorized local research copying on September 7, 2026. Attribution and source modifications are tracked in `integrations/colony-bridge/PROVENANCE.md`; nothing was pushed or published.

## Conclusion

Reuse the companion's domain observations and selected native command implementations. GABS handles sessions/transport, RimBridgeServer handles general game/UI capabilities, the companion supplies gameplay-specific queries and commands, and Python retains colony decisions and reconciliation. Another manager tier would not address these missing facts or native command semantics.

The companion is not a complete substitute for our controller. It has no equivalent to our persistent goals, reservations, arbitration and web console. Its direct construction/job implementations also reproduce native checks rather than obtaining every action from a universal game registry. Those portions still need version-specific behavioral tests.

## Reviewed surfaces and disposition

| Source | Overlap and value | Decision |
| --- | --- | --- |
| StatusTool | Clock, notifications, colonist jobs/condition and threat facts in one main-thread read | Adopted in observation companion |
| ListPawnsTool | Registry query, server filters, optional health/needs/equipment/work/social blocks | Adopted; added native thingId to every row |
| ListThingsTool | Ownership, forbidden, stockpiled, fogged and trader partitions; inventories included | Adopted; retain distinct meanings, not a single available count |
| ListBuildingsTool | Blueprint/frame/built states, bill ingredient blockers, actionable exceptions | Adopted; baseline queries playerOnly=true |
| ListRoomsTool | Native room roles, roof/contents/bed facts and geometry | Adopted; fogged rooms excluded from compact visible-room count |
| ZonesTool | Zone-list vs grid consistency, crops and optional filters | Adopted; anomalies remain available in raw diagnostics |
| ZoneCellsTool | Create/edit/filter/crop with native before/after and overlap handling | Next write integration; test single-cell and cross-zone transfer first |
| PlaceBuildingTool | Material, orientation sweep, footprint/refusal reason, refund-aware replacement | Next construction integration; audit ideology, instant builds, replacement/refunds and rotated footprints |
| PawnConfigTool / BuildingConfigTool | Priorities, schedule, hostility response, bed settings, forbidden and power | Reuse after paused-state readback checks; shared read helpers extracted now |
| BillsTool / BillCommon | Recipe/bill lifecycle plus ingredient shortfall | BillCommon adopted for observations; write path must pass existing paused bill lifecycle test |
| OrderTool | Explicit ID resolution, native jobs and current-job readback | Reuse after nonviolent pawn, stale ID, blocked target and queued-job tests |
| ResearchTool | Available projects/requirements and native selection | Later capability gate |
| TradeTool / WorldTool | Dialog/native world access | Later coverage; no untested blanket adoption |
| Watch / supervised playback | Stream presentation and automatic stop decisions | Leave out of this module; preserve our player pause ownership and scheduler |

## Important findings from the source and live test

1. **Generic bridge queries are too coarse for routine colony decisions.** The companion queries pawn/thing registries instead of scanning terrain for entities. All six selected queries ran successfully on our eight-tribal fixture.
2. **Full detail still costs context.** A pawn query with health, needs, equipment and work returned about 60,000 JSON characters in the first trial. The integration leaves optional work/social detail on demand, keeps native evidence out of the default prompt, and projects a small typed summary. It does not silently truncate the source response.
3. **Supply categories overlap.** `oursUnforbidden` differs from `ours`; stockpile and forbidden breakdowns cover all owners for the returned definition, not necessarily only the owned subset. Typed field names preserve that scope. No path safety/reachability or human-edible nutrition is inferred from these counts.
4. **Building counts are not all filtered counts.** The baseline playerOnly query returned no detailed player buildings while its counts.built remained 506 scanned map buildings. Do not turn that counter into "506 colony buildings." Native raw counts remain diagnostic, not a summary claim.
5. **Pawn IDs were missing by default.** Top-level ListPawns rows had names/positions but no identity; an ID existed in optional settings. Our only modification to the full copied tools adds `GetUniqueLoadID()` to every row. No label-to-ID guesswork.
6. **Read methods need care.** The companion explicitly avoids several getters with state effects and reconstructs some mood/social information. Treat those as labeled derived observations, not exact native UI values without checking. It also uses reflection into the operation journal to report unknown input fields. Python now validates discovered input schemas before dispatch, so unknown arguments cannot be silently dropped even if that journal lookup changes.
7. **The companion reports hidden world state in some queries.** Rooms explicitly carry fogged. Baseline building queries are limited to player-owned objects; visible-room counts exclude fogged rooms. Raw diagnostic data is not yet a general model-facing discovery interface. A future all-map query needs explicit visibility treatment.
8. **Native receipts still need interpretation.** The API can return transport success around payload failure. The existing bridge client checks payload success, and the observation gateway also rejects ignored arguments. Missing required observation fields fail rather than becoming zero/default state.

## Implemented boundary

`ObservationGateway` permits exactly six read tools, fetches and caches each native input schema, and rejects all other tools before dispatch. `observe` produces:

- Full native responses and their operation receipts for diagnostics.
- A generated `BridgeObservation` DTO from `data/bridge_observation.schema.json`: stable pawn IDs, jobs/condition, supply partitions, visible-room and zone counts, alerts and threat counts.
- Start/end game ticks and explicit same-tick status. Sequential reads are not represented as an atomic snapshot if the clock advanced. No claim of persistent game identity is made from map name.

This slice does **not** feed the legacy RIMAPI-dependent runtime fabricated compatibility objects. The adapter is exercised through the isolated probe; switching the live planner/runtime to it is still an explicit migration gate.

## Verification and next gate

Live evidence before the final replay: `.rimbot/bridge/evidence/20260907-174347`. Six exported home/* tools discovered, eight distinct pawn IDs, identical start/end ticks, paused state preserved. Full batch including on-demand schema discovery: 0.41 seconds. Projection before adding alert/threat counters: 4,277 characters versus roughly 63,051 characters of native batch evidence. This is API/projection performance, not model performance or proof of starter-base completion.

The native companion builds against the installed game and SDK with zero warnings/errors. Focused tests cover denied writes, unknown argument rejection, schema caching, missing-field failures, tick drift and owned/forbidden/stored distinctions.

Next: integrate ZoneCellsTool and the selected construction/config/bill commands behind explicit native schemas, prove their immediate/queued effects, then connect a backend-neutral observation/execution boundary to the existing planner. Do not expand RIMAPI or rewrite those companion commands from scratch without a demonstrated need.

Final combined replay: `.rimbot/bridge/evidence/20260907-174743`, 0.41 seconds for the companion batch, 4,109 compact characters. Observation checks and sleeping-spot/stockpile/draft/undraft/screenshot regression passed. Full Python suite: 334 passed. Generated-model drift check passed. No autonomous model playtest was run.
