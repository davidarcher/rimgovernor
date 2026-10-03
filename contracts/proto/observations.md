# Observation contracts

`observations.proto` defines simulation reads and reusable settings snapshots.
It contains no command selector, mutation branch, arbitrary JSON field, `Any`, or
`Struct`. RPC descriptors identify fixed request/reply pairs at the shared MCP
ProtoJSON boundary; they do not require another server. Each RPC has its
individually named Request and Reply.

## Food source channels

`ColonyFactsSnapshot.food_channels` is an independent census, with unavailable
outcomes for failed or oversized reads. It does not select food policy.
`NativeFoodChannels` reads it; `observation.ColonyProjection.FoodChannels` retains
optional values as `domain.Fact` after bridge validation.

Every tame animal has gatherable and egg rows; missing components leave production
values unknown and mark activity false. Unreadable activity stays unknown. Multiple
gatherable components produce one row per resource.
Handler reachability requires an available colonist with Handling enabled.
Milk and egg rows retain native activity, nutrition/day and remaining lead time;
milk also reports gathering work/day. Inactive comps contribute no food. Pen
rows carry demand, worst-quadrum pasture production and stored nutrition, once
per enclosed pen. Slaughter rows carry native meat nutrition, grazing demand
and reproduction interval; policy owns the removal opt-in and safety selection.
Paste dispensers report power, eligible hopper nutrition and the interaction
cell's room. Pollution counts the map-clipped center +/-22 planning window.
Forage lists biome wild plants with human-edible harvest products and native
average-temperature growing twelfths (0..11); these are seasonal potential,
not harvestable instances. Acquisition retains ownership of plant instances.

Fishing is absent without Odyssey. With Odyssey, each fish-bearing water body
has one row for its visible passable water cells, body-wide population/capacity,
whether any such cell belongs to a fishing zone, and reachability from the colony
anchor. The section also reports Fishing research. Every collection is
complete; no per-cell fishing payload is emitted.
`tools/foodchannels` checks lab berry-bush forage and a cow's milk fullness.

## Deep resources and mineral scanners

`ColonyFactsSnapshot.deep_resources` reads discovered `DeepResourceGrid` entries,
aggregated into eight-connected lumps of the same definition. Each row reports
total remaining units, cell count and the member nearest its centroid (ties x,z).
Depleted cells disappear; touching discoveries of the same definition merge.
The census returns every lump, scanner and drill.
Failed reads return section-unavailable, never partial known-empty facts.

Drill rows (`DeepDrillState`) cover every spawned colonist deep drill: powered
state, the native next deposit under the drill (`resource`, `remaining`, absent
when `depleted`) and a standing Deconstruct designation. Go requires depletion,
designation and power to be stated, a positive exact deposit on an undepleted
drill and none on a depleted one; the removal guard dispatches only a depleted,
still-present drill (every colonist drill is the controller's; no ownership
ledger).

Ground and long-range scanner rows carry built, powered and recent-working state;
long-range rows also expose the selected output mineral when readable. Remaining
ticks estimate work to the guaranteed-find threshold at the last user's speed,
excluding idle time and the native discovery-check interval of up to 59 ticks.
Random discovery may occur sooner. Missing timing stays unknown. Go preserves an
absent/unavailable section as `ColonyProjection.DeepResources` unknown, while an
observed empty section establishes no discovered lumps or built scanners.
`tools/deepresources` checks seeded aggregation, both scanner kinds and a
yielding drill beside a depleted one; a colony snapshot test (#894) selects
a depleted drill for removal from the deep drill step's recorded read.

## Required semantic validation

- Every reply selects exactly one observed, unavailable, or request failure case.
  Observed snapshots have actual identity/tick/generation context. ReadScope checks
  expected identity only; advancing ticks or revoked authority never blocks
  reconciliation reads. Identity mismatch is stale, never silently rebound.
- Optional facts distinguish unavailable from observed zero/false. An omitted
  requested fact or repeated section requires a `ReadIssue` naming its proto field
  path. Omitted unrequested detail is not an empty observed section. Known-empty
  collections have explicit complete metadata and zero counts. The helper section
  oneofs preserve whole-section failure independently of sibling facts.
- Common unavailable reasons distinguish NOT_REQUESTED, NOT_APPLICABLE, HIDDEN,
  and NATIVE_COMPONENT_MISSING from failed/stale/limited observations.
- There is no paging. Every list, nested collection and geometry is returned
  complete; reads carry no count caps. No sampled geometry,
  stock count, candidate, or diagnostic target can claim completeness.
- A whole serialized reply has no size cap and never silently removes fields. Text follows common bounds.
  Stock/count quantities are nonnegative; trade transfer/minimum/maximum counts
  are signed (negative sells). Ratios and all measurements are finite. Units are named
  on facts; a percentage/fraction conversion is an adapter responsibility.
- `Completeness.filtered` counts the rows the query's filters excluded; only
  the filterable reads carry it (pawns, supplies, buildings, zones, rooms,
  research, resource sources, and visible-only hediffs).
- `SnapshotRef` binds context, exact entity ID, and an opaque token for settings
  CAS. Tokens never mean permission or completed effects. Bill-stack tokens bind
  the bench and ordered stack, not only a bill index; trade tokens bind the session
  and absolute row identities. A label is never an identity.
- One exact opaque source entity ID is reused across reads and writes, with no
  fuzzy or suffix matching. Hediff identity is scoped to a health snapshot plus
  definition and body-part index; no stable ID is invented. Duplicate
  indistinguishable conditions remain ambiguous and cannot authorize an exact write.
- Definition IDs and native state names remain open strings. Protocol outcomes
  such as waste location are closed enums and unknown values are rejected.
  Native enums projected as strings are observations, not unchecked command modes.
- Collection cell coordinates come from current map dimensions; no map-size
  constants or sentinel coordinates. Missing world-map scope remains absent.
- Batch sections are sequential, not atomic. All nested scopes must match the
  outer expected identity; start/end context makes tick drift visible. A successful
  batch does not turn an unavailable child into complete data.
- Reads must not mutate game or bookkeeping state.

## Colony-fact and operation-readback graph

- Food supply: per-consumer fed nutrition demand; each FoodStock preserves
  exact item, holder, count, eligible eaters, minimum-eater nutrition, rot deadline,
  temperature and roof. Held food does not become common stock. Future harvest,
  grazing, thaw, hauling and continued access are not promised.
- Forecast: combined human/animal stock, animal IDs, per-zone raw sow and
  harvest work, mature/stalled plants and current yield, patient blood-loss/mood
  target/break thresholds. Unknown native numbers remain absent with read issues;
  this is not a predicted medical outcome or labor schedule.
- Upkeep and comfort: exact items/rot/deterioration/storage, beds and
  owners/users/access, storage cells and item-specific unreserved covered capacity,
  structures/repair/fire/filth/home protection, people/thermal comfort, animal feed
  and pen eligibility, dining/recreation/surface access with each indoor
  facility's host `room_id` (joins to the typed room census and its native
  `Room.Role`). Helper errors identify
  missing sections rather than producing healthy empty lists.
- Construction, haul, wall-removal and Home records: explicit records and Home
  revision/shape/exclusions. Completed native events and surviving identities are
  required; disappearance or a building at equal coordinates does not prove work.
- Development: actual power/base consumption/network/occupied geometry,
  furniture indoor/bed slots, open native research prerequisites and current work.
- Colony scalars: colonist/worker counts, open resource quantities and
  policy definition labels, environment conditions, climate/growing inputs,
  farm productivity, cooking products/nutrition/rot and bills, butchering bills,
  harvestable acquisition items, food corpses, Boolean qualifying food storage,
  forbidden supply cells, the bounded `blighted_plants` census (up to 64
  blighted plants in growing zones or the home area with position, zone and
  designation state, #245), the player faction's tech level (`player_tech_level`,
  which selects the starter shelter's shape), and planning definitions/cells
  including per-cell `doorway` (a door, door blueprint or door frame) so indoor
  furnishing keeps entrance aisles clear. Planning definitions are explicit
  requested open definition names.
- `dialog` section (#156): the topmost open force-pausing
  `Verse.Dialog_NodeTree` (window id, type, title, text, interactive) with its
  current node's options in native order, each carrying its index, label,
  `selectable`/`disabled_reason` and whether activating it resolves (closes or
  advances) the dialog rather than opening a hyperlink. Absent when no such
  dialog is open; the initial naming dialog stays under `naming`.
- Need relief requires current needs, queued/current job identity,
  player-forced/interruptibility/native priority, timetable, carry/fire/draft/
  mental/dead/downed and medical rest facts. PawnState/Settings/JobEvidence carries
  these; operation admission does not stand in for later recovered need levels.
- BillStack, BuildingSettings, PawnSettings, ZoneState, GearLoadout,
  ResearchSnapshot and TradeSheet expose SnapshotRef for exact
  compare-and-set. Read tokens cannot revive authority or prove successful writes.
- PawnHealth carries surgery facts (#1161): `missing_parts` (each missing or
  destroyed part at its common missing ancestor, its parent and the vital flag)
  and `operations` (every available medical recipe per target part with its
  kind, vanilla `success_chance` for the best eligible doctor, best colony
  medical bed and best permitted medicine on the map, eligible doctor count,
  `ingredients_on_map`, `violation` and `lethal`). Go never recomputes them.
  `ReadMedicalCatalog` is retired.
- Gear loadouts carry an explicitly present native deficit and an optional blocker;
  blocked pawns can still have equipment needs. Complete candidate and replacement
  lists belong to that exact loadout token. Planning read issues distinguish an
  unavailable gear census from a complete census with no eligible replacements.
- PlanningFacts.environment is the controlled-growing census inside the 45x45
  planning region: sun lamps with the native growth cells (specialDisplayRadius,
  not glow radius), power draw, schedule-aware lit_now and network; plant growers
  with fertility, sow tag, current crop and can_sow; proper indoor rooms with
  temperature, open-roof and lit cell counts; and every power network's current
  generation split into solar and wind, consumption, stored and capacity
  watt-days. Planning definitions add sow_tags and grow_min_glow for plants and
  power_w, glow_radius, sow_tag and grower_fertility for buildings. An
  `environment` section issue withholds the census; row counts are bounded and
  a lit cell count never exceeds the room's cells.
- The colony upkeep projection includes independent complete visible item,
  structure, fire and filth censuses, each returning every row. A section issue
  requires no rows and prevents recovery; missing required row fields remain
  unknown. Item deterioration uses the native base rate, so covered items still
  need valid storage. Home flags limit fire, repair and cleaning needs. Native
  playing clearance is required before recreation capacity is accepted.

## Other reads

- ListArchitectCategories/ListArchitectDesignators expose exact designator ID,
  category, buildable definition/label, application kind and cell/rectangle
  support. Visibility/availability are explicit optional facts. No generic
  architect execution capability is introduced.
- Camera, selection, UI, tabs, gizmos, notifications and screenshots are
  presentation-owned. Process ownership, save/load and clock journal readback are
  lifecycle/clock-owned.
- Exact typed rows permit aggregation/presentation by consumers. Room cell queries
  use GetCells plus ListRooms by native room ID. Semantic diagnostics (inspect
  text, lock/refusal reasons, hidden/read failures, thermal sides and ingredients)
  remain typed facts or bounded diagnostic strings.

## Royalty facts

`ReadRoyaltyFacts` (#1599) reports the title ladder (seniority, favor needed, and the throne-room requirement
read from `RoyalTitleDef.throneRoomRequirements`: minimum impressiveness and
area, the accepted throne definitions and whether the throne is assigned),
the permit catalog (minimum title, permit points, whether the permit acts and
the favor a call spends) and each colonist's holdings per faction (title,
favor, permit points, taken permits). Each rung also carries the title's
bedroom requirements (`RoyalTitleDef.bedroomRequirements`: minimum area and
impressiveness, floor, furniture rows; without a holder's ideo exemptions) and
`ceremonies` lists each offered or ongoing bestowing-ceremony quest
(`RoyalTitleUtility.GetCurrentBestowingCeremonyQuest`): quest id, colonist,
bestower, awarded title, accepted, bestower waiting in the lord's Wait toil,
started, spot and the lord's colonist attendees (#1602). It is separate from the pawn row and
slow-changing: the Go client reuses a read for `RoyaltyRefreshTicks` and
decodes it into `policy.RoyaltyFacts`, where an absent scalar is unknown.
Without Royalty the reply is `Unavailable(NOT_APPLICABLE)`, which the client
returns as no facts. Reads are derived state (persistence-contracts.md); the
read adds no store.

## Animal race catalog

`ReadAnimalRaceCatalog` (#1625) reports the static facts of every animal race
the game knows, modded and DLC included, whether wild or tameable: carrying
capacity, trainability and the trainables the race can learn, wildness, body
size, combat power (the highest `PawnKindDef.combatPower` of the race), market
value, minimum handling skill (the Animals level taming needs) and its
periodic products (milk, wool, eggs and spawned items such as chemfuel, each
with item and interval). The facts hold for a load, so the Go client reads the
catalog once per load token and decodes it into `policy.AnimalRaceCatalog`
(`bridge.AnimalRaces`), where an absent scalar is unknown, never zero. It is
derived state held in Go memory (persistence-contracts.md); it adds no store
and no per-animal rows.

## Quest census Empire fields

`QuestState.faction_id` is the first non-player faction in `Quest.InvolvedFactions`
(the id `FactionState` rows use) and `QuestState.map_id` the map of the first
`QuestLookTargets` entry on a map; both are absent when native has none (a
quest anchored only to a world object has no `map_id`). `QuestReward.favor` is
the royal favor a `Reward_RoyalFavor` grants; summed per `choice_index`, it is
what the Empire quest planner (#1604) maximises. The Go read carries
`FactionState` id and hostile beside the quests, and a quest with no
`map_id` or one off the identity map is off-map. Favor granted outside a
reward-choice part is not read.

## Obtainable operation preconditions

References are `Ref {id}` (#1342); an `EntityRef` is only a row head (id,
definition, label, map, position). A write's CAS token is a `SnapshotRef`
sibling of the reference (`pawn_snapshot`, `source_snapshot`, ...), required,
context-scoped and bound to the exact ID; a reference without one cannot supply
an EntityPrecondition. Missing producer support yields unavailable; tokens are
never fabricated from a label, position, tick alone, or public protobuf bytes.
Tokens cover the relevant native facts and domain-specific settings, not authority.

| Operations precondition | Read path |
|---|---|
| DesignateIntent.thing_id | GetCells.thing.snapshot / ListPawns.pawn.snapshot / ListBuildings.building.snapshot |
| WorkSettingsIntent.pawn_id | ReadPawnSettings (same pawn ID) |
| ProductionBillIntent.bench_id | ReadBills.bench (same bench ID) |
| ZoneIntent.zone (Ref) | ListZones.zone.id; ListBuildings storage row id |
| BedAssignIntent.pawn_id/bed_id/expected_previous_bed | ListPawns.pawn and owned bed; ListBuildings.building |
| NeedReliefIntent.pawn/job/schedule | ListPawns.pawn.snapshot, JobEvidence, PawnSettings.schedule |
| PawnOrderIntent WEAR pawn/target | ReadGear.pawn.snapshot, candidate.item.thing.snapshot, GearLoadout.snapshot |
| HusbandryIntent.animal_id/target_id | ReadHusbandry.pawn (same animal ID), allowed area and master IDs |
| PrisonerInteractionIntent.pawn_id | ReadPopulation.pawn and current interaction |
| SetDrafted/AttackTarget/PawnOrderIntent | ListPawns.pawn.snapshot; exact target snapshot from ResolveTarget/GetCells/entity reads |
| OpenTrade.trader/negotiator | ListTraders.trader.snapshot/negotiator.snapshot |
| SetTradeLines/AcceptTrade/EndTrade.session | ReadTradeSheet.snapshot; trader and negotiator from ReadTradeSession |
| SetTradeLines.line_id | ReadTradeSheet.lines.line_id, scoped to frozen sheet; not an inferred DefName/index |

PlaceBuilding uses its placement preview and write authority precondition; it has
no separate EntityPrecondition.

Draft claims expose exact claim_id, original Owner (controller session, player
direction) and pawn scope. Boolean drafted does not prove ownership; unowned and
unavailable are distinct. Cleanup compares the unchanged claim even after
authority is revoked. Trade line IDs are opaque and sheet-scoped, catalog group
IDs catalog-scoped; neither is derived from labels or definition names.

Clearance: `GetClearanceTargets` reads visible, deconstructible non-player buildings touching Home. Salvage evidence on the rows outside Home is read only when the request sets `include_salvage` (#984); the routine review and the planner that execute remote salvage set it, the shelter ruin holds and claims do not. It retains partial Home overlap, sealed ancient-danger membership, counterfactual roof blockers, faction and a standing deconstruct designation (no ownership flag). The same read lists the chunk stacks standing in Home (`chunks`: forbidden, stored, hauling destination) and, while an allowed unstored chunk has no destination, a free outdoor Home footprint for a dumping stockpile (`dump_sites`). `ClearHomeObstructions` consumes both.

Shrines: `GetAncientShrines` reads each ancient-danger room as one unit: sealed state, Home overlap, caskets with hit points and contents, guards once the interior is unfogged, and the perimeter walls that can be deconstructed without a roof-support blocker. No readiness judgement, breach or casket order consumes this census yet (#456).
