# RimMolt, AutoRim and RimBot: action/state audit

Audited 2026-09-07. Recommendation: keep the RIMAPI fork and Python controller;
incorporate selected AutoRim implementations and independently implement the useful
RimMolt interface patterns. Do not migrate to AutoRim wholesale. MCP is out of scope.

The most important difference is not the number of tools. RimMolt can inspect and
invoke **the actions the game currently offers**, including contextual pawn orders,
object gizmos and dialog controls. Our model frequently has to reconstruct those
choices from definitions and low-level jobs. We also have real native coverage gaps
and several working API routes that our semantic dispatch cannot reach.

## Evidence and reproducibility

| System | Inspected version | Evidence |
| --- | --- | --- |
| RimBot | `7f02522` | Controller catalog, semantic domains, administrator tools, execution and observation code. Includes the newly added administrator agency/wiki tools. |
| Our RIMAPI fork | `92f2b39` | Native services/controllers, generated OpenAPI and construction/planning overlay. |
| AutoRim | `89fadf37e5af4d4e5ca24e7b822d8b3fbb8084ca` | Entire public repository downloaded; command registry and facade enumerated; representative implementations examined in the areas discussed below. |
| RimMolt | Workshop item `3796006886`, downloaded September 7 | Shipped assembly inspected through ILSpy decompilation, including registration, handlers and bridge code. No third-party mod was enabled or executed in RimWorld. |

RimMolt DLL SHA-256:
`27337f8b2843f6c879c8c4f9dab1660cadc311c236eeb8f5ea23626e0a801e04`

The authoritative hash is also recorded mechanically in
[the JSON inventory](TOOL_SURFACE_INVENTORY.json); use that value for reproduction.
The decompiled assembly is evidence about shipped implementation, not an upstream
source release or a license to copy it. We found no public core reuse license.
AutoRim's [LICENSE](https://github.com/Critical-Reynolds/autorim-mcp/blob/89fadf37e5af4d4e5ca24e7b822d8b3fbb8084ca/LICENSE)
is MIT; preserve its copyright/license if incorporating code.

The [Astra completion report](https://www.reddit.com/r/singularity/comments/1w93mgg/gpt6_astra_finished_the_game_rimworld_in_15_hours/)
is useful motivation, but we have not reviewed the complete video, difficulty,
interventions or exact command trace. This audit does not establish that every
RimMolt tool was used in that run or that a local 4B model will achieve the same result.

### Inventory totals

- RimMolt: **113 named tool definitions**, excluding aliases. Thirteen registrations
  are gated by Biotech/Anomaly/Royalty; 100 are independent of those gates. Individual
  tools still have loaded-game, settings and DLC requirements. Some tools expose
  multiple operations; others can invoke dynamically discovered native actions.
- AutoRim: **24 facade tools containing 116 actions**, backed by matching command
  classes. There are **119 backend commands** including three backend-only helpers.
  No facade action lacked a corresponding command class at the inspected commit.
- Ours: **207 catalog entries** after adding the native contract overlay: **128
  exposed**, comprising **98 reads and 30 writes**. These are static policy counts;
  runtime availability also depends on what the connected mod advertises. Eight
  exposed writes match no semantic execution domain. This excludes Python-level
  administrator, knowledge, project and enclosure tools.

Full names, source locations, facade actions and all our exposure/domain flags are
in [the readable inventory](TOOL_SURFACE_INVENTORY.md) and JSON inventory. Tool counts
are not capability-equivalent: a generic action bridge can cover many game commands.

## Capability comparison

“Missing” below means no equivalent dedicated route/bridge was found in the inspected
catalog and services. A sufficiently low-level JobDef or editor method is not counted
as equivalent to a supported player action.

| Player system | RimMolt | AutoRim | Our current coverage and gap |
| --- | --- | --- | --- |
| Overall observation | Configurable `get_status` bundles, pawn summaries, alerts and changed-state attachments | `colony.snapshot`, alerts, letters, food/resources/power | We already aggregate observations and consume events. Missing configurable compact bundles and consistent changes-since-last-command in model tool replies. |
| Contextual pawn orders | `order_pawn` lists current right-click options for pawn + target, then invokes a chosen option; disabled options are visible | Named draft/move/attack/stop; prioritize scans work givers | Our `post_pawn_job` requires the model to supply a JobDef. No native right-click list/execute bridge found. This is a major reliability gap. |
| Selected object actions | `inspect_thing` returns inspect text, gizmos, reverse designators, disabled reasons and toggle state; `do_thing_action` invokes them | Mostly individually implemented commands | No general inspect/action bridge. Missing many ordinary player controls unless individually implemented. |
| Architect menu and designations | `list_architect` enumerates resolved designators; `designate` supports things, cells and filled/outline rectangles | 19 designation actions, including claim, chop/cut, tame, haul, smooth, uninstall and vein mining | Our designation endpoint is limited to mine/deconstruct/harvest/hunt/remove-all and manually manipulates designations. No broad native registry exposure. |
| Buildings | Building/terrain placement, material selection, per-cell partial results; special instant placement path | Building/terrain placement, line/rectangle batching and checks | Our native building contracts, exact footprints, material budget, reservations and enclosure compiler are useful and should stay. Native placement is ThingDef buildings only. |
| Floors and areas | Terrain construction and home/roof/allowed-area paint/erase/invert | Terrain build; allowed-area create/modify | Current exposed construction path lacks TerrainDef floors; old builder is hidden by overlay. No native home/roof/allowed-area editing surface. Roofs can happen automatically, but the model cannot reliably direct them. |
| Room usability | `room_graph`: door adjacency, outside connectivity, native map-edge reachability and room quality | Local ASCII map and general building/state inspection | We inspect rooms, roof/reachability and construction sites, but lack a room-door connectivity graph. Add this as observation, not another room recipe. |
| Map orientation | Whole-map coarse ASCII plus detailed area layers, things, unmanaged items, fires and screenshots | Bounded ASCII region, thing search, grouped/filterable rows | We have detailed spatial observations, resource overview, visual base plan and video. Add a compact shared map/room representation usable by any role. |
| Pawn health/mood/social | Tab-shaped detail for colonists, enemies, prisoners, animals and off-map pawns; thoughts, capacities, relations, logs, optional tooltips | Pawn detail, surgery and derived worker/threat analysis | Not wholly missing: our pawn/social APIs already expose substantial facts and logs. Gap is coherent pawn inspection and presenting actionable reasons without large duplicated context. |
| Work and schedules | Work priorities, schedule and restrictions | Bulk priorities, schedules, explainable pawn ranking | We have priorities, schedules and work allocation. Do not replace job creation with more priority heuristics. |
| Equipment | `manage_gear` plus contextual orders | Candidate equipment inspection with usability reasons; equip/wear/unequip | We can equip/wear through the equip service. Missing a useful candidate/disabled-reason query and normal targeted drop/forced-outfit controls; current drop-all editor route is not equivalent. |
| Production bills | Recipes, read/add/edit/delete; broad settings | Workbench discovery, add/set/remove/reorder | We have rich bill update contracts but production routing misses singular `bill` names. Fix exposure before implementing another bill API. |
| Stockpiles | Item/category/special filters, quality/HP, priority and occupancy | Create/expand/delete, priority and allowed-item settings | We already support item/category filters, HP/quality and priority. Gaps include explicit special filters, fuller storage-container support and zone shape editing. Do not treat stockpile basics as absent. |
| Growing zones | Read, rename/delete, crop change and sowing toggle; blight information | Create/change crop/expand/delete | Our irregular native crop-cell placement and fertility checks are stronger than AutoRim's creation path. Missing full existing-zone lifecycle, sowing toggle and crop-change workflow. |
| Animals and pens | Animal training/master/slaughter-related management, wildlife and building assignment | Training/master; tame/slaughter/release designations | We observe animals and can construct pen components. Missing training/master/autoslaughter/restriction/pen-setting coverage; a pen project is not an animal-management API. |
| Medical and prisoners | Medical care, surgeries, prisoner modes; pawn detail | Medical policy, surgery lifecycle, prisoner modes/release/execute | Ours has tend/rest and health observations. Missing surgery bills, care policy and prisoner interaction controls. |
| Apparel/food/drug policies | Read, assign and edit policy definitions | Read/assign existing policies | Our schedules and outfit read are not policy management. Dedicated assignment/editing is missing. |
| Power and building configuration | Power-grid observation plus native gizmos | Power observation; limited named controls | We have power read and a power write with no semantic route. General switches, settings and dropdowns need native action discovery. |
| Research | Progress, prerequisites, bench/facility requirements and selection | Current/list/set/stop/suggest | We already have tree/progress/selection. Improve concise prerequisite evidence rather than replacing the system. |
| Trade | Session inspection, transfer adjustment and execution | Open/stock/set/evaluate/execute/close | Our Trade category contains `get_traders_defs`, not a trade session. Actual trading is missing. |
| Caravans/world/quests | Normal caravan dialog with pawns/items, movement/world actions, quest accept/dismiss/abandon | Immediate caravan departure; world/quest reads | Ours has world/caravan/path/quest reads but no equivalent formation, loading, movement or quest-action workflow. |
| Dialogs and uncommon/endgame actions | Window listing, drawn controls and verified interaction; gizmos/world targeting provide an extensibility path | No equivalent generic dialog/action bridge | We can list UI windows, not operate them. This is a major coverage limit for quest choices, unusual building controls and endgame progression. We did not verify a complete ship-launch sequence. |
| DLC/setup/streaming extras | Genes, mechs, anomaly study, royalty, ideology, setup, YouTube chat and player messages | Limited ideology read; setup/control helpers | Most DLC-specific management is absent. Setup/editor/YouTube tools are not evidence of a normal colony gameplay gap. Our dashboard/video/test harness already serve different needs. |

## Concrete findings that explain current failures

### 1. Discover the native action; do not make the model invent a job

RimMolt `ActionTools.OrderPawn` calls `FloatMenuMakerMap.GetOptions` for the pawn and
target cell. With no command selected it returns the option labels, indices and
disabled flags. With a selection it checks the option and invokes its action.
It supports a queued-order mode. `EntityTools` similarly enumerates `Thing.GetGizmos`
and reverse designators, including disabled reasons and toggle state.

Our `PawnJobService.AssignJob` resolves a JobDef and target and calls the helper that
creates a job. A valid JobDef is not proof that the player can issue that job for this
pawn and target, or that all its targets/parameters have been supplied. This leaves
the model doing work normally handled by RimWorld's contextual menu.

AutoRim's [JobCommands](https://github.com/Critical-Reynolds/autorim-mcp/blob/89fadf37e5af4d4e5ca24e7b822d8b3fbb8084ca/mod-src/AutoRim/Commands/JobCommands.cs)
are more convenient than raw JobDefs, but `jobs.prioritize` independently scans work
givers and chooses the first usable job. It is not the actual float-menu interface.
Prefer the RimMolt pattern implemented independently against our native contracts.

Do not copy RimMolt's addressing unchanged: menu indices are rebuilt and labels can
be matched by substring. Use an observed action reference tied to the game session,
map, pawn/thing and current option identity; recheck availability before execution.
Return a stale/changed-action response instead of executing a different option.
Also separate RimMolt's injected `AppendAutoAttackOption` from actual native menu
options; it proves that even this bridge is not entirely vanilla menu forwarding.

### 2. Several capabilities are lost in our routing

`EXECUTION_DOMAINS['production']` matches `bills`, while these exposed operations use
singular `bill`:

- `put_buildings_bill_update`
- `put_buildings_bill_suspend`
- `put_buildings_bill_reorder`
- `delete_buildings_bill_remove`

The API already has repeat/target counts, pause/resume thresholds, ingredient radius,
worker/skill restrictions and material filters. The specialist cannot select the
corresponding update tools. The administrator's new direct execution path deliberately
shares the same domain check, so it also cannot bypass this gap.

`post_map_building_power` and `post_pawn_edit_apparel` are also exposed but unrouted.
The latter drops all weapons/apparel immediately; review native semantics and replace
with normal targeted gear commands rather than simply enabling an editor method.
`planning_create` and `planning_remove` are the remaining two unmatched writes, but
they have a separate architect path; their missing executor match is not itself a bug.

Replace substring routing with explicit operation-to-player-system metadata and a
coverage check. Every exposed write should be routed, explicitly controller-owned,
or intentionally withheld with a documented reason.

### 3. Room connectivity is missing evidence, not missing strategy prose

RimMolt `RoomTools.GetRoomGraph` constructs room nodes and door edges, performs an
outside-connectivity traversal and also asks native map-edge reachability. It reports
both results when they disagree. This makes a sealed room or disconnected hallway
visible without expecting the model to infer it from scattered cells. Its tool can
be disabled in mod settings, so registration alone does not prove runtime availability.

Keep our geometry/reservation/enclosure code. Add a native room graph plus factual
roof coverage, usable sleeping capacity and open construction gaps. Test a sealed
room, adjoining rooms with no exterior exit, an existing ruin, a mountain enclosure
and a valid room reached through a hallway. Report facts; do not force 11x11 layouts.

### 4. Carry meaningful change back with the response

RimMolt `ToolRegistry.Call` attaches notifications, deltas, pause/dialog information
and threat warnings. `DeltaTracker` compares resource/building counts and pawn health;
this is useful compact feedback, **not** a complete object-lifecycle event log or a
blueprint-to-building identity map. `wait_for_event` returns on notable events and
reports why, with configurable timeout/pause behavior.

We already have event ingestion, world-state reconciliation and verified work receipts.
The missing piece is consistently delivering the relevant changes inside the active
model interaction, rather than only in a later review or the dashboard. Add typed
`observed_tick`, changed entities, interrupted/finished work and actionable letters
to response context. Do not duplicate the whole colony snapshot after every command.
Do not adopt RimMolt's blanket hostile-presence waiting cap: dormant cave insects were
already a harmful trigger in our tests. Distinguish actual danger and preserve player
pause ownership.

## What to incorporate from AutoRim

1. **Equipment availability inspection.** `EquipCommands` reports candidate items,
   current weapon and unusable reasons, checking violence/work restrictions, item
   availability and reachability. It ranks purpose-built weapons before improvised
   ones; market value also affects ordering. Copy the evidence fields, not the
   assumption that price determines the best weapon. Its candidate scan is capped,
   so do not claim exhaustive results without reporting the bound.
2. **Trade session operations.** `TradeCommands` uses native tradeables, transfer
   limits and the active deal. This is a useful starting point for open/read/adjust/
   evaluate/commit contracts. Add explicit session identity and post-trade receipts
   before integrating with our controller.
3. **Policy, surgery and animal operations.** Small subsystem implementations are
   reasonable candidates for selective porting with MIT attribution and native
   behavior tests. Extend OpenAPI/generated types rather than importing the TS facade.
4. **Explainable inspection.** Definition ambiguity candidates and worker/equipment
   explanations reduce repair calls. Keep evidence and recommendations distinct.
   Our indexed definition discovery already supplies part of this capability.

### Why not fork AutoRim as the replacement?

Its smaller codebase and player-system grouping are attractive. However, replacing
our foundation would bring these additional tasks:

- **Instant construction:** `BuildCommands` calls `PlaceBlueprintForBuild` for both
  single and batched placements. No zero-work direct-placement branch appears there.
  That is exactly the class of sleeping-spot behavior we have already had to handle.
- **Growing legality and partial side effects:** `ZoneCommands.AddCells` checks
  bounds, existing zones and zone-incompatible things, but not crop fertility. Zone
  registration/cell addition occurs before resolving the requested crop, so an invalid
  crop can fail after changing the map. Our irregular crop placement validates more.
- **Research/material choices:** list-buildable filters research, but that does not
  establish parity with every placement prerequisite. Automatic stuff selection uses
  the resource counter then defaults; it does not prove accessible economical supply.
- **Caravans:** `caravan.form` uses immediate departure with carried items, not the
  normal cargo assembly workflow. RimMolt instead works through `Dialog_FormCaravan`
  and reflects private methods/fields, which is broader but version-sensitive.
- **Schemas:** the TS facade uses an action enum with a shared set of mostly optional
  arguments. Required arguments for each action are substantially described in prose
  and checked in C#. It is not a replacement for our enforced per-operation contracts.
- **Timeouts:** its dispatcher queues work on the game thread and abandons requests
  that have not begun. A timeout racing with a command already executing still needs
  an unknown-outcome treatment; it is not an exactly-once guarantee.
- **Existing investments:** we would need to port our typed API, observations,
  material accounting, spatial reservations, result verification, dashboard/video and
  repeatable test integration, or support two native authorities.

These are source findings, not claims that an AutoRim playtest failed. Neither AutoRim
nor RimMolt was installed into the test game during this audit. A focused port can
reuse useful code without inheriting an entire alternative execution stack.

## Recommended implementation order and acceptance checks

| Order | Work | Evidence required before calling it done |
| --- | --- | --- |
| 1 | Explicit capability routing; repair bill lifecycle access | Every exposed write is accounted for. Administrator and specialist can update/suspend/reorder an existing bill; native readback confirms the change while paused. |
| 2 | Native contextual order and object-action bridge | Nonviolent equip, forbidden/restricted targets, blocked construction, dropdowns and changed menu options return the same choices/rejections as the game. Stale action references cannot select another action. |
| 3 | Native architect/designator and area/terrain coverage | Chop/claim/haul/tame/smooth, roof/home/allowed areas and floor placement work through native eligibility. Partial results name rejected cells. Instant spots create no labor blueprint. |
| 4 | Room graph and compact command feedback | Sealed/missing-door/hallway cases are distinguishable; completed construction and interruptions appear in the next relevant model reply without a full rescan dump. |
| 5 | Existing-zone lifecycle and policy/animal/care coverage | Change crop/sowing, edit zone cells, special storage filters, medical policy, animal settings and surgery bills verify through native state. |
| 6 | Trade, normal caravans, quest/dialog/world actions | A normal cargo-loaded trip, trade and quest choice can complete without teleport/immediate-departure substitutes; pause/modal handling is explicit. |

Continue the eight-tribal-colonist starter test alongside steps 1–4. Measure time to
first useful orders, completed usable shelter, duplicate orders, rejected/stale calls,
and model tokens. Broader endgame coverage cannot substitute for proving starter-base
reliability. This checkpoint is an audit, not an implementation of these milestones.
