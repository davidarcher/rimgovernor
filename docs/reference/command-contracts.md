# Player command contracts

[Documentation](../README.md)

These contracts describe admission, scope and preservation for semantic player requests.
For the end-to-end path, read [plans and Hands](../explanation/plans-and-hands.md).

The chat command union supports SetResearch, BuildRoom, PlaceBuildings, CreateZone, EditZone,
SetWorkPriority, CreateBill, DraftPawn, MovePawn, TendPawn, RescuePawn, CreateGoal, CancelGoal and
ModifyResourcePolicy, SetResourceReserve and TradeEconomy. The model receives individually named
semantic tools and read-only native inspection/preview tools, not arbitrary native
execution. Fresh native facts and resource-definition labels are available in Manual as
well as Automate. The latest player message follows the evidence context. Consecutive
maintained goal and policy requests in the same response are applied before
interpretation ends; downstream routine work stays with the controller. Partial
rejections are included in the acknowledgment alongside accepted requests.
Acknowledgments come from accepted structured results, not inferred completion. Optional
local knowledge/wiki lookup, colony notebook, scout, visual review and consultations
remain chat tools. Advice is evidence, never executable authority.

`EditZone` changes an existing observed zone through native eligibility and readback.
`CreateBill.ingredients` optionally supplies a complete native ingredient whitelist.
See [player action coverage](player-actions.md) for fields, special storage filters,
UI reference guards and the native capability audit.

## Economic selection

`TradeEconomy` queues one policy-driven exchange through the shared plan and Hands.
It names an observed available map trader and eligible negotiator. Its ordered
targets contain exact native item definitions, desired retained stock, maximum
buy/sell quantities and maximum buy/minimum sell unit prices, plus a net spending
limit and silver reserve. The first target has purchase priority. Unavailable or
ambiguous items remain explicit blockers in the action's policy evidence.
The negotiator must reach the trader normally before execution; inaccessible,
departed, orbital or nonadjacent traders cannot bypass native eligibility.

Selection uses fresh eligible stock and current trader demand/prices. Exports are
bounded by existing surplus and the trader's current silver. Pending construction
costs, native construction deficits, resource spending restrictions, maintained
stock goals and player reserves protect stock before an export is selected.
Equipment, medicine, food and pawns are excluded using native definitions. Buying
does not borrow against expected export income. Native acceptance atomically
enforces post-deal reserves and protected exports. Changed inventory or policy
eligibility requires inspection rather than replaying an uncertain exchange.

For replenishment, use a separately requested `MaintainResource` goal through the
existing production system. Export limits do not create bills or assume unobserved
production capacity. Expedition supplies use the same resource reserves and stock
goals; trading never accepts quests or starts expeditions. Final storage and
orbital/expedition interactions retain their separate completion contracts.

## Research and work assignments

Research admission resolves the player's project label through native dry-run validation
before adding an action. Refusals return the native reason directly; successful requests
use the resolved definition and still pass through Hands and fresh research readback.
Research contract discovery permits reads/previews only. Work commands accept an exact
unambiguous colonist name or observed ID, then retain the native ID for execution and
persistent work overrides. Current pawn jobs do not establish work-type assignments.

Explicit tending and rescue resolve exact observed colonist identities, require
current patient eligibility and pass native preview before admission. Hands waits
for actual treatment or living-patient bed delivery. Manual ground tending requires
an already drafted doctor; Automate provides managed draft cleanup. Native rescue
chooses its eligible bed. Reachability permits `Danger.Deadly`, so legality does not
certify a fire-free, cool or threat-free route. Automatic rescue, firefighting and
heat escape are not scheduled by these commands.

## Context and native labels

Initial chat context includes the shared controller state and a native fact-section
index. Large native definition catalogs are retrieved through `inspect_colony_facts` in
one to three sections instead of being inlined into every question. These reads are
explicitly historical observations from the current review; native validation still
refreshes facts before a command is accepted. The initial context retains a bounded
native resource-label glossary so resource policy names remain grounded when the larger
definition catalog is omitted. Resource choices expose exact native display labels, with
definition IDs used for ambiguous labels. Exact observed IDs from earlier context remain
valid aliases. Resolution persists the native definition ID; base names do not include
qualified resource variants automatically.

## Provenance and resource policies

Actions carry PLAYER, AUTOPILOT or LLM_ADVISOR provenance. Advisory provenance cannot
commit orders. Player steps normally run before routine optimization; hard validation
and resource policies still apply. A food target updates the same EnsureFoodSupply goal
and hysteresis policy. Work overrides are retained by the deterministic allocator.
Resource constraints include reserves and normal/defense-only/stopped spending.
`ModifyResourcePolicy` changes only spending; `SetResourceReserve` changes only an
explicit numeric reserve, including zero. Each preserves the other field and validates
the resource against native facts. Policy changes queue a shared Hands action for native
production enforcement; clock admission refreshes the current budgets before simulation.
Persistent `MaintainResource` goals use exact native output definitions and stock
targets. Native source and bill facts supply their actual work types and relevant
skills. Active resource goals extend deterministic work coverage with these jobs;
incapable pawns remain excluded and explicit player work overrides retain ownership.
They acquire nearby reachable mineables or mature wild plants with identity-guarded
ordinary designators, or discover native bench recipes and create target-count bills.
Available recipes with an available native option for every ingredient slot take
precedence over recipes missing inputs; native bill admission still owns reachability,
reservations and consumption. Existing bills count as continuing capacity only when
their repeat mode and target cover the request. Player bill edits are preserved; an
explicit goal renewal is required before replacing previously issued production that no
longer covers its target. Construction material alternatives are selected against both
native costs and current policy/reservations; a room shell needs one affordable material
for its entire perimeter. Unavailable sources, recipes and benches remain explicit
prerequisites; accepted orders and projected yield do not count as stock. The spending
contract rejects reserve fields; combined requests use separate policy calls in one
interpreter response. Existing stored policies retain their values. Explicit
draft/movement overrides prevent autonomous recruitment or cleanup from taking ownership
of those pawns.

## Construction intent and cancellation

Room and zone commands retain an intent ID and request history. A follow-up can replace
unissued geometry after validating its replacement. Issued geometry requires an explicit
construction change rather than silent relocation. Player construction has tracked
goals; related pending or blocked player work prevents a competing autonomous project.
Cancelling related player work suppresses its autonomous replacement until an explicit
goal request re-enables it. Existing native blueprints/designations are retained by
`CancelGoal`.
The bounded chat intent index exposes live state and issued-operation count without
receipt bodies, so unissued refinement can be distinguished from native construction.
Missing live progress remains unavailable; exact history is retrieved through inspection.

## Exact native removal

Chat removal requires an explicit construction-removal clause in the current
request. Cancelling a goal alone does not authorize removing game objects.
Conflicting preservation/removal clauses conservatively preserve all orders.
This is bounded English protection, not a general natural-language authorization proof.

Explicit `CancelConstruction` resolves a tracked player construction intent and captures
its current pending native objects from issued placements. Admission previews every
exact target before atomically accepting the removal batch and suppressing the original
source. Shared Hands persists each removal intent and uses the native cancellation path
with colony/load/map, ThingID, definition, stuff and position checks. Completed
buildings remain. An uncertain removal is observed before any further action: absence
satisfies that exact target, while a still-present target blocks replay. A fresh
explicit request can capture the remaining orders; existing accepted batches never
retarget replacements or loads. Explicit English preservation clauses refuse removal at
command admission, plan admission and execution even if the model selects the removal
tool. This conservative refusal does not certify arbitrary wording or multi-intent
scope.

## Relocation and dependencies

Relocating an unissued room uses the existing validated `BuildRoom` refinement
path with the same intent identity and no removal action. Explicit move requests
cannot admit standalone cancellation: the plan must include a replacement that
depends on completed removal, preserving validation before destructive work.

`RelocateConstruction` admits replacement geometry and exact old-order removal in one
validated plan. Replacement placements must already pass native placement, resource
policy and preservation checks before any removal; their dependency does not waive
placement refusals. Shared Hands waits for the entire cancellation to complete before
issuing the replacement. Completed, missing or uncertain old placements and dependent
projects require explicit inspection instead of relocation. The conversational intent
points to the replacement while old receipts remain durable. Manual dispatch advances
newly satisfied dependencies within the same accepted request and operation budget; it
never dispatches unrelated work. Native construction projects retain their source step
identity, so equal display titles cannot replace another action's completion targets.
Legacy descriptive projects remain separate.

## Geometry admission

Geometry admission retains conflicts between unfinished actions and explicit reserved
walkways. Native placement previews determine whether furniture can share existing zone
cells; zone edits separately require exact existing geometry. Unchanged completed
actions remain history and do not reserve their old cells against later player edits.
New placement still requires native previews against the current map, including actual
buildings and cancellation hazards.

## Temperature and room adoption

Thermal handoff in the selected shelter uses its verified native room temperature.
Unsafe temperatures can exclude beds from the safe-reachability census; this must not
prevent heating or cooling that same room. Roof, room geometry and adoption context
remain required before furnishing.

`SetBuildingTemperature` requires one exact observed completed player building and a
successful native temperature-control preview. It uses shared Hands and the native
setpoint control. The accepted setpoint does not certify room temperature or power.

`AdoptRoom` selects an existing native room as the preferred shelter without changing
its old construction project or issuing orders. Admission verifies exact enclosed
interior, complete roof and a native doorway on the requested side. Rectangles use
the central doorway; nonrectangular adoption supplies exact `interior_cells` and
`entrance_cell` together, following the [spatial contract](spatial-contracts.md).
Native
colony/map/load identity is retained with the adoption; another load requires fresh
explicit adoption. Handoff rechecks geometry and entrance after player edits, retains
existing objects and fits furnishings through the shared native previews. Missing or
sealed rooms block furnishing. The original construction receipts remain history.

## Shelter handoff and furnishing

After a player shelter shell completes, sleeping handoff verifies its exact native
interior and roof coverage before furnishing the missing indoor sleeping capacity. Each
spot uses a native accepted footprint wholly inside that room; existing obstructions and
a continuous entrance aisle are excluded. Changed/unknown rooms or insufficient space
block for refinement instead of issuing another shell. Roof work keeps simulation
requested. Storage, cooking and thermal furniture wait for the chosen player shell and
use its verified room geometry. Building footprints and entrance aisles are excluded;
food-zone previews must preserve existing zones. The native footprint census can contain
harmless things: accepted placements are screened for actual building conflicts, wipes
and frame cancellation. Unissued cached farm patches are recomputed around accepted
player shells and walkways; issued growing zones require explicit refinement instead of
silent replacement. Work allocation batches are identified by the remaining native
changes so cohorts larger than eight continue through subsequent batches.

## Inspection and player settings

Chat can inspect structured controller facts, gates, goals, blockers, reservations,
policies and intent history to explain what is running or why work is blocked. The
dashboard presents the same state. Its Autopilot page shows native readings, all
foothold gates, goal provenance/blockers and effective food, wood, temperature and
execution-speed parameters. Player setting edits go through a typed local API, colony
identity and effective-policy version checks, and cross-field validation. They persist
in ColonyPlan, invalidate pending direction and wake deterministic review without
creating a human chat request. Verification/recovery limits remain read-only in the
editor. Chat food targets and the editor use the same policy. Unsaved drafts survive
polling; concurrent changes require an explicit reload. Runtime revision/load guards
reject stale requests and conversational changes. Manual execution is scoped to the
accepted current player requests and does not resume time or release an external hold.
