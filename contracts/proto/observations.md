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

## Generated def rows and constants

`DefinitionCatalog.thing_defs` and `terrain_defs` (#1730, epic #1720) carry
every `ThingDef` and every `TerrainDef` as the generated messages of
[`defs.proto`](defs.proto) (all fields, comps and verbs included), sorted by
`defName`, beside the planning rows, which are unchanged until #1731 makes
them views over these. Unlike the planning rows they are unfiltered: no
buildable or sowable test. A def whose name is not a valid identifier is
left out, as in the planning rows.

Native fills them with `DefMirrorFill`, a generic protobuf-reflection walker
over the generated descriptors, with no per-class code: a message mirrors the
CLR class its `clr_type` option names, a field is read by its name, a def
reference is its `defName`, a `System.Type` its full name, an enum its number,
a `Nullable<T>` an optional field, a `Dictionary` repeated `entry` messages
a nested collection a `list` message, and a class element of a list an
`Opt_` message whose `value` is unset for a null element (the game reads null
entries positionally, `ThoughtDef.stages`: Go checks `entry.GetValue()` for
nil and keeps the index). A null dictionary value is the entry's unset `value`.
A class with subclasses is a `<Class>Any` oneof whose arm is chosen by the value's exact type. A value the
messages cannot hold fails the whole read naming `Class.field`, with no skip
and no fallback: a missing CLR field, a null string, def or `Type` element (or a null
dictionary key or scalar value), or a type with no arm. A mod subclass of a mirrored polymorphic class (a mod's
`CompProperties`) is such a type, so the catalog read fails on a modded game;
the supported configuration is vanilla plus the DLCs the generator saw.

`DefinitionCatalog.constants` (`CatalogConstants`) holds the game numbers Go
must not type in: `ticks_per_hour`, `ticks_per_day`, `days_per_year`
(`GenDate`), `bill_stack_max` (`BillStack.MaxCount`), `skill_max_level`
(`SkillRecord.MaxLevel`) and `lit_glow_threshold`, read by reflection from the
non-public const `GlowGrid.GameGlowLitThreshold`; the read throws naming the
member when it is missing or not a float. `currency_def` is `ThingDefOf.Silver`: the
def `Tradeable.IsCurrency` tests, which Go reads as `ItemFacts.Currency` (the
census coin; an empty value or a def with no row is refused). `full_rot_rate_c` is the temperature at which
`GenTemperature.RotRateAtTemperature` first reaches its full rate of 1: its
curve is literals inside the game function, so native bisects the function
itself and throws naming it when the rate never reaches 1 or is not finite; Go
reads it as `FoodStorageObservation.ChilledMaxC`, the limit for refrigerated
food. Drugs, chemicals
and the preventive drug are not constants: `ItemFacts` derives them from the
`CompProperties_Drug`, `ChemicalDef` and `HediffDef` rows. There is no plant-glow constant: each
def carries `growMinGlow`.

`bridge.DecodeDefinitionCatalog` keys the rows by def name in the per-load-token
cache (`DefinitionCatalog.ThingDef`, `TerrainDef`; a thing and a terrain may
share a name) and refuses a missing or repeated name, absent def rows, sets or
constants, and a non-positive or nonfinite constant. The reply must stay under
the 48 MiB gunzipped reply guard (`maxReplyProtoBytes`, a decompression guard,
not a row cap).

### Every other def class

`DefinitionCatalog.defs` (`DefSets`, #1761) carries the defs of every other
concrete `Verse.Def` class (251 in the game and its DLCs, so the catalog holds
every def the game loads): one repeated field per class, named by the class
(`stat_defs`, `recipe_defs`, ...) and numbered in ordinal order of the class's
full name. `ThingDef` and `TerrainDef` keep `thing_defs` (8) and `terrain_defs`
(9). One generated message with a field per class costs the least generated
code: a field per class on the catalog would mean hand-written proto that the
generator could not keep in step, and a oneof row wrapper adds a message per
class and a second encoding layer.

Native fills each field from the game's def database for that exact class
(a def of a subclass is in its own field), sorted by `defName`, by the same
walker. The class list is the `DefSets` descriptor itself, so a new class needs
no native code. A concrete def class the game has and `DefSets` lacks (a mod's)
fails the read naming it, as a mod subclass of a mirrored class does.
`QuestGen.SlateRef<T>` fields are mirrored as the field's XML text, a literal
or a `$variable` held in the struct's one private string.

`bridge.DecodeDefinitionCatalog` reads the `DefSets` fields by protobuf
reflection into `DefinitionCatalog.Defs` (message full name, then `defName`);
`bridge.DefRow[*defspb.StatDef](catalog, name)` is the typed lookup. A set
that is absent or empty, an invalid `defName` and a repeated `defName` within
a class are contract violations.

Rows mirror private fields too, and a def class whose fields nest its own
kind (quest nodes, think nodes, thing set makers) mirrors them as recursive
messages, so a quest script carries its whole node tree. Every row's
`modPackageId` is the id of the mod that defined it
(`Def.modContentPack.PackageId`; unset when the def has no mod). The runtime-state
exclusions are listed in the `defs.proto` header; see
[schema generation](../schema-generation.md#def-mirror).

`DefinitionCatalog.class_chains` (`ClassChain`, #1785) carries the base classes
of every class the fill touched, def classes and every `System.Type` value in a
row alike, sorted by name. `bridge.DefinitionCatalog.ClassIsA(class, base)` and
`RowIsA(row, base)` match a family against it; a class with no chain is an
error. `ActiveCondition.condition_class` is the concrete `GetType().Name`, not a
full name, so a consumer needs the full name to resolve it against the chains.

### Stat values and adjusted costs

`DefinitionCatalog.stat_values` (`DefStatTable`, #1759) carries the game's own
numbers that StatDef parts compute in code, once per load. For every
`ThingDef` native emits one `DefStatRow` per allowed stuff
(`GenStuff.AllowedStuffsFor`) when the def is made from stuff, else one row
with an empty `stuff_name`. A row holds `GetStatValueAbstract(stat, stuff)` of
every StatDef the game shows for that def and stuff
(`StatWorker.ShouldShowFor(StatRequest.For(def, stuff))`; a stat the game hides
is absent, not zero; except `DeteriorationRate`, which a planner reads of
every `ThingDef` and the game hides (`showIfUndefined` false) for a def that
does not set it, so native emits it whether shown or not) and `costs`, the game's `ThingDef.CostListAdjusted(stuff)`
(stuff volume and difficulty adjustments applied). Stat names are the shared
`stats` table; a row's parallel `stat` (index) and `value` arrays are the
compact form, chosen over repeated name strings per row. A stat or cost list
that fails, or a non-finite value, fails the whole read naming def, stuff and
stat. A def, stuff or stat whose name is not a valid identifier fails the read,
as in the def rows. Go does not port StatWorker or type any stat constant.
`terrain_rows` holds the same rows for every `TerrainDef` (stuffless, no
costs; kept apart from `rows` because a terrain defName may also name a
ThingDef); `DefinitionCatalog.TerrainStatValue` reads them and
`FloorTerrain(name)` joins cleanliness, beauty and flammability with the def
row's `pathCost` and `natural`, so a frame's `FloorTerrain` names the terrain
only (#1733).

`DefinitionCatalog.StatValue(def, stuff, stat)` and `AdjustedCosts(def, stuff)`
look the values up in the per-load-token cache; an absent table, a missing
(def, stuff) row or a hidden stat is an error, never a default. Decode refuses
an unknown def, stuff or cost def, a repeated row, stat or table name, an
index outside the table, unequal arrays, a non-finite value and a non-positive
cost. Go applies quality and condition itself. Size: a synthesized table of
13700 rows (450 stuffed defs x 25 stuffs plus 2450 plain defs, 40 stats and 3
costs each, 270 stats) is 3.9 MB; native timing and the real size are
unmeasured.

### Game-computed ThingDef flags

`DefinitionCatalog.thing_facts` (`ThingDefFacts`, #1733) is one row for every
ThingDef: what the game's own code says about the def, computed natively once
per load and read by Go, never ported. `food_kind` is set for a food a policy
can allow (a nutrition-giving ingestible that is no drug and no corpse): meal
tier by `FoodPreferability`, then kibble, hay, meat by
`FoodUtility.GetMeatSourceCategory`, `IsFungus`, `IsAnimalProduct`, a
vegetable or fruit food type, else other; `meal_ingredients` is set on meals
only (`FoodUtility.GetFoodKind`). `raw_meat` is `ThingDef.IsMeat` and
`medicine` is `ThingDef.IsMedicine`. `DefinitionCatalog.Foods`, `RawMeat` and
`Medicine` read them; a def without a row, a row for an unknown or repeated
def, an unknown kind or ingredients value, and ingredients off a meal (or a
meal without them) are contract errors. A frame carries no copy: the food
stock, upkeep item and policy facts name only the thing and its instance data.

### Joy buildings

`DefinitionCatalog.JoyBuildings` chooses the joy buildings for recreation
variety by a rule over catalog rows, never by name: a buildable planning
definition whose ThingDef gives a `BuildingProperties.joyKind`, with the power
its planning row draws. They are ordered by the joy one session gives (the
`JobDef` `joyGainRate` times `joyDuration` of the `JoyGiverDef` that offers
the building, the best giver when several do), then by the market value of
its adjusted costs at the planning row's stuff (cheaper first), then by name.
A joy building no `JoyGiverDef` offers, a giver whose job row is missing and a
cost the stat table cannot value are contract errors.

## Royalty facts

`ReadRoyaltyFacts` (#1599) reports the title ladder (seniority, favor needed, and the throne-room requirement
read from `RoyalTitleDef.throneRoomRequirements`: minimum impressiveness and
area, the accepted throne definitions and whether the throne is assigned),
the permit catalog (minimum title, permit points, worker class, whether the permit acts and
the favor a call spends) and each colonist's holdings per faction (title,
favor, permit points, taken permits, and per taken permit its native cooldown:
`last_used_tick` (`FactionPermit.LastUsedTick`, absent until first used) and
`cooldown_remaining_ticks`, 0 when ready, #1607; `Client.FreshRoyaltyFacts`
bypasses the reuse window to re-read a cooldown). Each rung also carries the title's
bedroom requirements (`RoyalTitleDef.bedroomRequirements`: minimum area and
impressiveness, floor, furniture rows; without a holder's ideo exemptions) and
`ceremonies` lists each offered or ongoing bestowing-ceremony quest
(`RoyalTitleUtility.GetCurrentBestowingCeremonyQuest`): quest id, colonist,
bestower, awarded title, accepted, bestower waiting in the lord's Wait toil,
started, spot and the lord's colonist attendees (#1602). `thrones` lists every
spawned player throne (`Building_Throne`) with its assigned owner, absent when
unassigned (#1601); a throne appears once it stands, so the planner treats one
the read does not list as built after it. It is separate from the pawn row and
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

## Biotech defs and pawn facts

Biotech facts follow the shared wire pattern (#1333): static defs ride the
definition catalog and per-pawn facts ride the canonical pawn row. There is no
separate read tool. Both are absent without Biotech (`ModsConfig.BiotechActive`).

`DefinitionCatalog.biotech` (#1678) holds, each sorted by name: `LifeStageDef`
rows (developmental stage, flags, stat effects); `RaceLifeStages` for every
humanlike race (`lifeStageAges` with the age each stage begins and
`lifeStageWorkSettings`, the minimum age per work type); `GeneDef` rows with
typed effects read from the def (disabled work tags, stat offsets and factors,
aptitudes, passion mods, capacity mods, enabled and disabled needs, forced and
suppressed traits, immunities, chemical dependency and tolerance factors,
biostats, exclusion tags); `XenotypeDef` rows with their genes; controllable
mech kinds (`PawnKindDef` of a mechanoid race with an overseer-subject comp:
weight class, bandwidth cost, work types and priorities); and
`MechWorkModeDef` rows (`work`, `escort` and `recharge` mark `MechWorkModeDefOf.Work`, `.Escort` and `.Recharge`). Effects come from the game defs, never Go name lists.
The Go client decodes the section into `bridge.BiotechCatalog` (rows by name,
refusing duplicates, unknown cross references and nonfinite numbers) with the
rest of the catalog, once per load token.

`PawnState.biotech` (`PawnBiotech`) carries a pawn's life stage and
developmental stage, learning need level and category, genes (endogene or
xenogene, active or overridden), xenotype, mechanitor bandwidth and controlled
mechs, a mech's overseer, work mode and control group index, and deathrest
state. An absent scalar is unknown, never zero; a failed sub-read leaves its
fields absent and adds a `ReadIssue` named `life_stage`, `developmental_stage`,
`learning`, `genes`, `mechanitor`, `mech` or `deathrest`. Go lifts the block into
`policy.PawnBiotech` on the work pawn (`WorkPawn.Biotech`) and the pawn
profile. `PawnProfile.Child` follows the developmental stage (Newborn, Baby,
Child) when it is known and the age rule otherwise. Both are derived state held
in Go memory (persistence-contracts.md); they add no store.

## Odyssey defs and facts

Odyssey (ground only, #1707) follows the shared wire pattern (#1333): static
defs ride the definition catalog and per-thing facts ride the canonical
building row. There is no separate read tool and no Odyssey intent. All of it
is absent without Odyssey (`ModsConfig.OdysseyActive`).

`DefinitionCatalog.odyssey` (#1708) holds, each sorted by name and read from the
game defs (never Go name lists): `BiomeDef` rows (hazard flags, densities,
`biomeMapConditions`, and the animal kinds and disease incidents with a nonzero
commonality in the inland, pollution and coastal tables; the races are
`AnimalRaceCatalog`'s, so a new animal needs no row of its own);
`TileMutatorDef` rows (the cave, vent and stockpile features of a world tile,
with the game conditions they add); `CompProperties_Hackable` thing defs
(defence, skill prerequisite, lockout, the quest a finished hack starts);
`MapPortalProperties` thing defs (pocket map generator, exit, size, tile
mutators); and the stockpile type enum with whether the world generator places
each value. `bridge.DecodeOdysseyCatalog` indexes them by name and refuses
duplicates, unknown cross references and nonfinite numbers.

`BuildingState.odyssey` carries a building's hack progress, defence, hacked,
locked-out and autohack state (`CompHackable`) and a portal's pocket map
existence and id, plus an ancient hatch's stockpile type and layout. An absent
scalar is unknown; a failed sub-read leaves its block absent and adds a
`ReadIssue` named `hackable` or `portal`. `ColonyFactsSnapshot.tile_mutators`
lists the colony map tile's mutators next to `biome`. Go lifts the blocks into
`policy.Hack` and `policy.Portal` (`bridge.BuildingHack`, `BuildingPortal`);
all of it is derived state held in Go memory (persistence-contracts.md) and
adds no store.

`ColonyFactsSnapshot.odyssey` (`OdysseySection`, #1709) is the colony-wide
Odyssey read, one keyed section with the snapshot's seq/tick (#1347); it is
absent without Odyssey and `Unavailable` when the read failed. It carries
`conditions` (every `GameCondition` active on the colony map with its def,
class, ticks passed and left, permanence and causing thing: lava flow, volcanic
ash, winter or debris, toxic fallout), `hazard_terrain` (the terrain defs on the
map whose own `dangerous`, `burnDamage`, `heatPerTick` or `toxicBuildupFactor`
flags make them hurt or contaminate, with the cells each covers),
`lava_emergences` (spawned `LavaEmergence` things) and `sites` (each
`AncientHatch` with an existing pocket map: pocket map id, stockpile type,
layout, colonists present and every `CompHackable` thing on the pocket map with
its progress, defence, hacked, locked-out and autohack state; positions are
pocket-map cells). Selection is by the game's own types and flags, never def-name
lists; an absent scalar is unknown, never zero. Go validates the rows
(`bridge.validateOdysseyColony`: unique ids per table, lava cells on the map,
nonnegative ticks, progress a fraction, no `ticks_left` on a permanent
condition) and projects them into `observation.OdysseyColony`
(`ColonyProjection.Odyssey`). Derived state held in Go memory
(persistence-contracts.md); it adds no store.

## Anomaly defs and facts

Anomaly (#1694) follows the shared wire pattern (#1333): static defs ride the
definition catalog and per-pawn and per-building facts ride the canonical rows.
There is no separate read tool and no Anomaly intent. All of it is absent
without Anomaly (`ModsConfig.AnomalyActive`).

`DefinitionCatalog.anomaly` (#1737) holds, each sorted by name and read from the
game defs (never Go name lists): `EntityCategoryDef` and `KnowledgeCategoryDef`
rows; `EntityCodexEntryDef` rows (category, how and when the entry is
discovered, the linked thing defs, provocation incidents and discovering
research projects); `AnomalyThingRow`, one per ThingDef that is an entity
(`RaceProperties.IsAnomalyEntity`), has a codex entry, is studiable, can be
held on a platform or holds an entity (the race's study yield, the def's
`MinimumContainmentStrength` base value and the `CompProperties_Studiable`,
`CompProperties_HoldingPlatformTarget` and `CompProperties_EntityHolder`
values); and `AnomalyIncidentRow`, one per `IncidentDef.IsAnomalyIncident`
(category, whether it is a `ThreatSmall` or `ThreatBig` threat, worker class,
chance and threat-point gates, the codex entry it discovers).
`bridge.DecodeAnomalyCatalog` indexes them by name and refuses duplicates,
unknown references (categories, codex entries), nonfinite or negative numbers
and an unnamed enum value. Creepjoiner form, benefit and downside rows (#1740: weights, combat-point
gates, requires/excludes, granted traits, skills, hediffs and abilities, a
downside's timing and worker) are included; monolith level defs are not, their
child adds them. The thing rows duplicate what the generated
ThingDef rows of #1720 will carry (comps, stat bases); they become Go views
over those rows when it lands. Def numbers in the anomaly catalog and `min_containment_strength` are the def values verbatim and are only checked for being finite: vanilla defs carry -1 (Alligator `studyAmountToComplete`, a holding target escape interval), whose meaning the consuming goal sources before it reads the field.

`PawnState.anomaly` carries `Pawn.IsEntity`, `IsMutant` and `IsShambler`, an
entity's `MinimumContainmentStrength`, its `CompHoldingPlatformTarget` state
(held on a platform and which, the ordered `EntityContainmentMode`, escaping,
bioferrite extraction, whether it can be captured) and its `CompStudiable`
state (enabled, completed, progress, points, knowledge gained, the knowledge
category and amount the game resolves). Three threat facts (#1739) feed the
defense tactics: `hidden_from_player` (`InvisibilityUtility.IsHiddenFromPlayer`:
the player cannot see or target the pawn, so no attack order names it),
`psychic_ritual_invoker` (the pawn's role in its lord's psychic ritual is the
ritual def's invoker role: the caster, who is focus-fired and shelled) and
`melee_only` (its `CurrentEffectiveVerb` is a melee attack and none of its
abilities is `ai_IsOffensive`; an entity or mutant that is melee only fights
as a charging pack, so squad defense and the manhunter tactic apply). Each is
read alone: a failed read is absent with a `ReadIssue` named for it.
Hostility stays `PawnState.hostile`.
A pawn with a creepjoiner tracker carries `anomaly.creepjoiner` (#1740): its
form and benefit def names (both shown to a player in the offer letter) and
`downside_triggered`, the tracker's private `triggeredDownside`, read natively
and true only once a timed downside has fired. The downside def is hidden
information: no pawn row carries it and nothing reads it (the catalog's
downside rows are static defs). Trait-only downsides and no downside never set
the flag, so "downside revealed" for the bot is what a player sees: the pawn's
visible traits and hediffs, or the flag. A failed read is absent with a
`ReadIssue` named `creepjoiner`. Creepjoiner offer letters
(`ChoiceLetter_AcceptCreepJoiner`) ride the colony census's `joiner_letters`
with `creepjoiner` true and a negative `expires_tick` when the letter has no
timeout (the game's sentinel, mirrored);
accepting sends the letter's own accept signal (`signalAccept`, then removes
the letter) through the same `DialogIntent`, guarded on the pawn being
spawned, and never by option index or label.
`BuildingState.anomaly` carries a holding platform's `CompEntityHolder` state
(the containment strength its room provides now, whether it is available, the
pawn it holds) and a studiable building's `CompStudiable` state. An absent
scalar is unknown;
a failed sub-read leaves its block absent and adds a `ReadIssue` named
`entity`, `held`, `holder`, `study` or a threat fact's name. Go lifts the blocks into
`policy.PawnAnomaly` and `policy.BuildingAnomaly` (`bridge.PawnAnomaly`,
`BuildingAnomaly`); all of it is derived state held in Go memory
(persistence-contracts.md) and adds no store. Thing rows (items, corpses) carry
no study state.

`ColonyFactsSnapshot.anomaly` (`AnomalySection`, #1738) is the colony-wide
Anomaly read, one keyed section with the snapshot's seq/tick (#1347); it is
absent without Anomaly and `Unavailable` when the read failed. It carries what
no row holds: `knowledge` (per `KnowledgeCategoryDef`: the project the research
manager funds from it, the knowledge stored for that project, whether any
project of the category can still be researched), `codex` (per
`EntityCategoryDef`: entries and discovered count) with `discovered_entries`
(the discovered `EntityCodexEntryDef` names), `held_entities` (each pawn a
holding platform holds on the colony map with its platform id, the join keys
into the pawn and building rows), `holding_platform_available` (the game's own
`StudyUtility.HoldingPlatformAvailableOnCurrentMap`) and `incidents`
(`GameComponent_Anomaly`: monolith spawned, level def and number, highest level
reached, questline ended, ticks since the last level change, ambient horror
mode, anomaly study enabled, the threat fraction the game gives Anomaly
incidents now, void awakening, an awoken corpse, whether a new metalhorror
implant can occur). A held entity's strength and need stay on its rows
(`HeldState`, `EntityHolderState`, `PawnAnomaly.min_containment_strength`).
Selection is by the game's own types, never name lists; an absent scalar is
unknown, never zero. Go validates the rows (`bridge.validateAnomalyColony`:
unique names and ids, discovered at most entries, nonnegative finite knowledge
and threat fraction, nonnegative levels) and projects them into
`observation.AnomalyColony` (`ColonyProjection.Anomaly`). Derived state held in
Go memory (persistence-contracts.md); it adds no store. Active game conditions
(for example the gray pall) are not here: the shared condition read lives in
the Odyssey section today.

## Room-role furniture

`ThingDefFacts.room_roles` (#1728, #1690, #1731) lists the roles the game's code
names a building definition for, sorted: `Toy`, `Decoration`, `Board` and `Desk`
(its `ThingDefOf` toy box, baby decoration, blackboard and school desk, the rows
its baby and school jobs name). Go derives the other two from the def rows:
`BabyBed` (`building.bed_crib`) and `DeathrestCasket` or `DeathrestAccelerator`
(a building carrying `CompProperties_DeathrestBindable`, a casket when its class
is a bed). Go refuses an unknown, duplicate or unsorted role and plans child
rooms and the deathrest chamber from the catalog rows carrying a role
(`policy.FurnitureRole`, `bridge.DefinitionCatalog.RoomRoleRows`). The
reference assembly carries no method bodies, so the game's room role workers'
own counting is unverified against these rules (native acceptance verifies).

## Biotech colony section

`ColonyFactsSnapshot.biotech` (`BiotechSection`, #1679) is the colony-wide
Biotech read, one keyed section with the snapshot's seq/tick (#1347); it is
absent without Biotech and `Unavailable` when the read failed. The pollution
cell grid is unchanged: the section carries `PollutionGrid` totals and the
player's pollution-clear area size, then rows for what makes pollution
(`polluters`: `CompToxifier`, `CompPolluteOverTime`), what removes it
(`pumps`: `CompPollutionPump`, with its disabled-by-artificial-buildings flag;
`atomizers`: `Building_WastepackAtomizer`), the wastepack stacks
(`wastepacks`: spawned things with `CompDissolution`, with frozen, outdoors,
in-atomizer and can-dissolve verdicts) and the producers of wastepacks
(`gestators`: `Building_MechGestator` with its active mech bill; `chargers`:
`Building_MechCharger`, each with the waste it holds). `babies` lists each
player Newborn or Baby with `ChildcareUtility`'s suckle and play verdicts, its
bed and its autofeeders; `breastfeeders` lists the pawns
`ChildcareUtility.CanBreastfeedPlayerPawns` names. Verdicts come from the game
(comps, buildings, `ChildcareUtility`), never def-name lists; an absent scalar
is unknown, never zero. Go validates the rows (`bridge.validateBiotechColony`:
unique ids per table, cells on the map, nonnegative counts) and projects them
into `observation.BiotechColony` (`ColonyProjection.Biotech`). Derived state
held in Go memory (persistence-contracts.md); it adds no store.

`pollution` also counts the polluted pollutable cells (`polluted_cells`) and
those outside the pollution-clear area (`polluted_uncovered_cells`, #1683).
`RoutineFacts.Pollution` (`policy.PollutionFacts`) projects the wastepack
verdicts and that count; it is unknown without Biotech, and `ManagePollution`
(maintained, priority 3, labor Hauling) is assessed only while it is known. Its
methods are the pollution-clear `AreaIntent` edit (polluted cells of the
planning window), `supply_allow` for a forbidden pack and the wastepack-guarded
HAUL (`wastepack_haul`). Siting freezer storage is not planned yet (open on #1683).

## Polluting-building flag

`observation.PlanningDefinition.Pollutes` (#1684, #1731) is a view over the def
row: true when the building def carries `CompProperties_Toxifier`,
`CompProperties_PolluteOverTime` or `CompProperties_WasteProducer` (the comps
behind the colony section's polluters and wastepack producers), false for any
other building, unknown for a def that is not a building. Siting uses it, never
a def-name list.

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
| AssignIntent.pawn_id/thing_id/expected_previous | ListPawns.pawn and owned bed; ListBuildings.building; RoyaltyFacts.thrones.thing for a throne |
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
