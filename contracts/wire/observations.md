# Observation contracts

The Observations records (`Observations.cs`) define simulation reads and reusable settings snapshots.
They contain no command selector, mutation branch or untyped JSON member. Each read tool has its
individually named Request and Reply record.

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
The food-channels acceptance cases check lab berry-bush forage and a cow's milk fullness.

## Delivery ledger

`ColonyFactsSnapshot.DeliveryLedger` is the required `DeliveryLedger` facts record of cumulative
production-site delivery counters within a `LoadToken`; see the
[delivery ledger](../../docs/developers/contracts/forecast-contracts.md#delivery-ledger).
`NativeDeliveryLedger` counts and builds it; a read that cannot count throws, which fails the colony facts read.

## Realized consumption

`ReadConsumption` reads the saved `ConsumptionState` ring: per (ThingDef, reason)
cumulative counters plus hourly increments (2500-tick hours, 60 days), packed into one
versioned base64 string in the save. A request names `since_hour`; the reply lists every
completed hour after it, sparse (`current_hour` is still filling and never listed;
`first_hour` is the first hour covered). `count` is signed: an ejected refuel or a removed
shell subtracts. Reasons: recurring `bill_ingredient`, `medicine_tend`, `food_eaten`,
`drug_dose`, `animal_feed`, `nutrient_paste`, `fuel_loaded`, `shell_loaded`, `apparel_wear`;
project `construction`; loss `rot`, `deterioration`, `fire`, `sold`, `stolen`,
`destroyed_other`. The brain (`policy.ConsumptionLedger`) keeps the hours in memory, keyed by the
load, and asks for the whole window once after a load or restart. Only recurring reasons
feed a resource runway. A hook native misses under-counts; nothing gates on it.

## Storage occupancy

`ReadStorageOccupancy` (`observations_read_storage_occupancy`) reads the saved
`StorageOccupancyState` ring: the item stacks each player storage group (stockpile zone
or storage building, by load id) on a home map held at each hour's first tick (2500-tick
hours, 15 days), packed into one versioned base64 string in the save that writes a group's
row only when its count changed. A request names `groups` and `since_hour`; the reply lists,
per requested group with samples, every sample after `since_hour` in ascending hour order
(`current_hour` is the hour running). A group not listed has no samples. The brain reads its
zones and built shelves over the inflow window and derives `policy.StoreInflow`; see
[storage](../../docs/developers/architecture/storage.md#store-inflow).

## Deep resources and mineral scanners

`ColonyFactsSnapshot.deep_resources` reads discovered `DeepResourceGrid` entries,
aggregated into eight-connected lumps of the same definition. Each row reports
total remaining units, cell count and the member nearest its centroid (ties x,z).
Depleted cells disappear; touching discoveries of the same definition merge.
The census returns every lump, scanner and drill.
A failed read leaves the section's `observed` absent (the native side logs the reason once), never partial known-empty facts.

Drill rows (`DeepDrillState`) cover every spawned colonist deep drill: powered
state, the native next deposit under the drill (`resource`, `remaining`, absent
when `depleted`) and a standing Deconstruct designation. The brain requires depletion,
designation and power to be stated, a positive exact deposit on an undepleted
drill and none on a depleted one; the removal guard dispatches only a depleted,
still-present drill (every colonist drill is the brain's; no ownership
ledger).

Ground and long-range scanner rows carry built, powered and recent-working state;
long-range rows also expose the selected output mineral when readable. Remaining
ticks estimate work to the guaranteed-find threshold at the last user's speed,
excluding idle time and the native discovery-check interval of up to 59 ticks.
Random discovery may occur sooner. Missing timing stays unknown. The brain preserves an
absent/unavailable section as `ColonyProjection.DeepResources` unknown, while an
observed empty section establishes no discovered lumps or built scanners.
The deep-resources acceptance case checks seeded aggregation, both scanner kinds and a
yielding drill beside a depleted one; a colony snapshot test selects
a depleted drill for removal from the deep drill step's recorded read.

## Required semantic validation

- A reply carries the observed facts, or a request failure where the reply has one. A read that fails inside the native side answers with the facts absent, and the native side logs each distinct reason once per read kind (`ReadFailureLog`).
  Observed snapshots have actual identity/tick/generation context. ReadScope checks
  expected identity only; advancing ticks or revoked authority never blocks
  reconciliation reads. Identity mismatch is stale, never silently rebound.
- Optional facts distinguish unavailable from observed zero/false. An omitted
  requested fact or repeated section requires a `ReadIssue` naming its record member
  path. Omitted unrequested detail is not an empty observed section. Known-empty
  collections have explicit complete metadata and zero counts. The helper section
  choices preserve whole-section failure independently of sibling facts.
- Common unavailable reasons distinguish NOT_REQUESTED, NOT_APPLICABLE, HIDDEN,
  and NATIVE_COMPONENT_MISSING from failed/stale/limited observations.
- There is no paging. Every list, nested collection and geometry is returned
  complete; reads carry no count caps. No sampled geometry,
  stock count, candidate, or diagnostic target can claim completeness.
- A whole serialized reply has no size cap and never silently removes fields. Text follows common bounds.
  Stock/count quantities are nonnegative; trade transfer/minimum/maximum counts
  are signed (negative sells). Ratios and all measurements are finite. Units are named
  on facts; a percentage/fraction conversion is an adapter responsibility.
- `SnapshotRef` binds context, exact entity ID, and an opaque token for settings
  CAS. Tokens never mean permission or completed effects. Bill-stack tokens bind
  the bench and ordered stack, not only a bill index; trade tokens bind the session
  and absolute row identities. A label is never an identity.
- One exact opaque source entity ID is reused across reads and writes, with no
  fuzzy or suffix matching. Hediff identity is scoped to a health snapshot plus
  definition and body-part index; no stable ID is invented. Duplicate
  indistinguishable conditions remain ambiguous and cannot authorize an exact write.
- Definition IDs and native state names remain open strings. Protocol outcomes
  such as a corpse class are closed enums and unknown values are rejected.
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
  harvestable acquisition items, hunt rows (every wild animal bearing a corpse, with
  fogged and mental-state flags) and the raw `hunt_census` (per colonist: Hunting
  and Cooking work state, primary weapon verbs, shield and a route verdict per eligible colonist x prey pair (safe, unsafe or skipped by the hunt route budget); per
  butcher bench: usability and bills) that policy decides hunts from,
  food corpses, Boolean qualifying food storage,
  forbidden supply cells, the player faction's tech level (`player_tech_level`,
  the floor of the construction tier), and planning definitions/cells
  including per-cell `doorway` (a door, door blueprint or door frame) so indoor
  furnishing keeps entrance aisles clear. Planning definitions are explicit
  requested open definition names. Every colony facts member is required:
  `planning` is `requested` (with the planning facts) or `not_requested` when
  the request set `planning: false`, and `sleeping_temperature` is `indoor`
  (min and max) or `no_indoor_bed`. The read returns the record or fails; there
  is no reply outcome wrapper.
- `dialog` section: the topmost open force-pausing
  `Verse.Dialog_NodeTree` (window id, type, title, text, interactive) with its
  current node's options in native order, each carrying its index, label,
  `selectable` and whether activating it resolves (closes or
  advances) the dialog rather than opening a hyperlink. Absent when no such
  dialog is open. The initial naming dialog is not observed: the mod confirms it itself.
- Need relief requires current needs, queued/current job identity,
  player-forced/interruptibility/native priority, timetable, carry/fire/draft/
  mental/dead/downed and medical rest facts. PawnState/Settings/JobEvidence carries
  these; operation admission does not stand in for later recovered need levels.
- `JobEvidence` reports the pawn's real current job. `preemptible` is set when the
  pawn is controlled with no committed coupled order, so the job is vanilla job
  selection a coupled order may replace (#3670, [pawn activities](../../docs/developers/contracts/pawn-activities.md)).
- BillStack, BuildingSettings, PawnSettings, GearLoadout,
  ResearchSnapshot and TradeSheet expose SnapshotRef for exact
  compare-and-set. Read tokens cannot revive authority or prove successful writes.
- PawnHealth carries surgery facts: `missing_parts` (each missing or
  destroyed part at its common missing ancestor, its parent and the vital flag)
  and `operations` (every available medical recipe per target part with its
  kind, vanilla `success_chance` for the best eligible doctor, best colony
  medical bed and best permitted medicine on the map, eligible doctor count,
  `ingredients_on_map`, `violation` and `lethal`). The brain never recomputes them.
  `ReadMedicalCatalog` is retired.
- Gear loadouts carry the loadout model's inputs and an optional blocker; the brain
  derives every deficit and replacement need from the model (the native deficit
  and replacement-need fields are reserved). Blocked pawns can still have
  equipment needs. The complete candidate list belongs to that exact loadout
  token. Planning read issues distinguish an
  unavailable gear census from a complete census with no eligible replacements.
- PlanningFacts.environment is the controlled-growing census inside the 45x45
  planning region: sun lamps with the native growth cells (specialDisplayRadius,
  not glow radius), power draw, schedule-aware lit_now and network; plant growers
  with fertility, sow tag, current crop and can_sow; proper indoor rooms with
  temperature and open-roof counts; and every power network's current
  generation split into solar and wind, consumption and capacity
  watt-days. Planning definitions add sow_tags and grow_min_glow for plants and
  power_w, glow_radius, sow_tag and grower_fertility for buildings. An
  `environment` section issue withholds the census; row counts are bounded.
- The colony upkeep projection includes independent complete visible item,
  structure, fire and filth censuses, each returning every row. A section issue
  requires no rows and prevents recovery; missing required row fields remain
  unknown. Item deterioration uses the native base rate, so covered items still
  need valid storage. Home flags limit fire, repair and cleaning needs. Native
  playing clearance is required before recreation capacity is accepted.

## Other reads

- Camera, selection, UI, tabs, gizmos, notifications and screenshots are
  presentation-owned. Process ownership, save/load and clock journal readback are
  lifecycle/clock-owned.
- Exact typed rows permit aggregation/presentation by consumers. Room cell queries
  use GetCells plus ListRooms by native room ID. Semantic diagnostics (inspect
  text, lock/refusal reasons, hidden/read failures, thermal sides and ingredients)
  remain typed facts or bounded diagnostic strings.

## Catalog rows and constants

The definition catalog read carries no def rows or game constants. `DefinitionCatalogRequest` (its `Scope` is
required) only has native prepare the load's def stat table and race facts on the game thread. It answers the empty
`DefinitionCatalogAck`; a refused or unavailable prepare throws (`NativeFaults`). The brain's `DefinitionCatalog`
(`Bridge/Catalog`) is then built in the mod over the game's own `Def` objects (`NativeCatalogFactory.Create`,
installed as `BrainCompositionOptions.CatalogFactory`; see
[brain-host](../../docs/developers/architecture/brain-host.md)), and what the game computes in code
(`CatalogDerived`) is read in process.

The brain catalog's currency (`ThingDefOf.Silver`, the def `Tradeable.IsCurrency` tests, read as
`ItemFacts.Currency`; an empty value or a def with no row is refused) and wort (`ThingDefOf.Wort`) def names
ride no wire field: the in-process `NativeCatalogFactory` reads them from `ThingDefOf` when it builds the catalog.
`DefinitionCatalog.derived` holds `full_rot_rate_c`, the temperature at which
`GenTemperature.RotRateAtTemperature` first reaches its full rate of 1: its
curve is literals inside the game function, so native bisects the function
itself and throws naming it when the rate never reaches 1 or is not finite; the brain
reads it as `FoodStorageObservation.ChilledMaxC`, the limit for refrigerated
food. Drugs, chemicals
and the preventive drug are not constants: `ItemFacts` derives them from the
`CompProperties_Drug`, `ChemicalDef` and `HediffDef` rows. There is no plant-glow constant: each
def carries `growMinGlow`.

The catalog read validates the context and a present `derived`; the brain refuses a non-positive or nonfinite constant it reads. Catalogs are cached
per load token.

The brain catalog matches a family by base class over the game's own types, never by a
name list. `ActiveCondition.condition_class` is the concrete `GetType().FullName`.
`DefinitionCatalog.OutdoorsPermanentlyDark(biome)` is
true when a `BiomeDef.biomeMapConditions` condition's `conditionClass` is or
derives from `RimWorld.GameCondition_NoSunlight`; the colony projection carries
it as `RoundsFacts.OutdoorsDark` and `CropClimate.OutdoorsDark`, unknown
without a biome read or catalog. Native `FoodClimate.sowing_now` folds
the same fact: false in a permanently dark biome (read from the game defs with
`typeof(GameCondition_NoSunlight).IsAssignableFrom`), unset when the biome or a
condition class cannot be read.

### Stat values and adjusted costs

Stat reads go through the load's def stat table (`DefStatTable`, [stat evaluation](../../docs/developers/contracts/stat-classes.md)):
the mod computes it from the game's own StatWorker/StatPart code in one game-thread pass during this read, so no
stat value, allowed stuff or adjusted cost list travels in the catalog.

`DefinitionCatalog.StatValue(def, stuff, stat)`, `ShownStatValue`,
`AdjustedCosts(def, stuff)` (`ThingDef.CostListAdjusted`: stuff volume and
difficulty applied), `AllowedStuffs(def)` (`GenStuff.AllowedStuffsFor`, sorted by
defName), `TerrainWorkToBuild` and `TerrainAdjustedCosts`
evaluate on demand for any (def, stuff) pair, including a stuff the game would
not offer for the def (a scenario's jade knife). An unknown def or stuff, a
stat the game hides for the def and a non-finite value are errors, never a
default. `DeteriorationRate`, which a planner
reads of every `ThingDef` and the game hides for a def that does not set it,
is read whether shown or not.
### Game-computed race facts

What the game's own race code says about each race (`RaceGameFacts`: the foods `RaceProperties.CanEverEat`
accepts, tameness decay, the wildness tame-chance factor and the adult feed per day) rides no wire field: the
definition catalog read computes it on the game thread once per load (`NativeRaceFacts.EnsureForLoad`, beside the
stat table) and `NativeCatalogFactory.Create` hands it to the brain catalog.

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

There is no royalty read. The title
ladder (seniority, favor needed, throne and bedroom requirements) and the permit
catalog (minimum title, permit points, worker class, whether the permit acts and
the favor a call spends) are static defs read from the game's Def objects
(`RoyalTitleDef`, `RoyalTitlePermitDef`; `DefinitionCatalog.WithTitleDefs`).
Each colonist's holdings and psycasts are its `PawnState.royalty`, decoded by `bridge.PawnRoyaltyFacts` into `policy.RoyaltyFacts`, where an
absent scalar is unknown. The neuroformer stock, bestowing ceremonies and
thrones are the colony section
`ColonyFactsSnapshot.royalty` (`RoyaltySection`), absent without
Royalty and without `observed` when the read failed, read on every colony frame.
`neuroformers` lists the psylink neuroformer and each psycast neurotrainer with
held, craftable and tradeable; `ceremonies` lists each offered or ongoing
bestowing-ceremony quest (`RoyalTitleUtility.GetCurrentBestowingCeremonyQuest`):
quest id, colonist, awarded title, accepted, bestower waiting in the
lord's Wait toil and started;
`thrones` lists every spawned player throne (`Building_Throne`) with its
assigned owner, absent when unassigned. `policy.RoyaltyFacts.WithColony`
joins the section to the read; a colony frame without the section leaves the
royalty fact unknown, and the section's presence is the Royalty-applicable
gate (`ColonyProjection.RoyaltyOf`). Reads are derived state
(persistence-contracts.md); they add no store.

## Animal races

There is no race read. The definition
catalog's `ThingDef` rows carry every race's whole `RaceProperties` and its
comps, `PawnKindDef` rows the combat power, and the stat table the race's
carrying capacity, wildness, market value and minimum handling skill. What only
the game's own race code can say rides the race's `RaceGameFacts` (published in process, not on the wire): `animal` (`RaceProperties.Animal`), `mechanoid`
(`IsMechanoid`), `insect` (`Insect`), the `trainables` the race can ever learn
(`Pawn_TrainingTracker.CanAssignToTrain`'s race rules) and the `edible_defs`
(`RaceProperties.CanEverEat`, over the foods the facts mark). the brain catalog
derives `policy.AnimalRaceCatalog` from those rows once per load
(`DefinitionCatalog.AnimalRaces`): body size, life expectancy, the predator
flag and the manhunter chances are row fields, a pest is an animal whose
`foodType` includes `Tree` (the alphabeaver), periodic products come from the
milk, wool, egg and spawner comps, and the feed a bench can make is the
`RecipeDef` products the race can eat. An absent stat is unknown, never zero.
It is derived state held in memory (persistence-contracts.md); it adds no
store and no per-animal race copy: `AnimalState` and `PawnState` carry no race
scalar, `FoodEater` no food list.

## Biotech defs and pawn facts

Biotech facts follow the shared wire pattern: static defs ride the
definition catalog and per-pawn facts ride the canonical pawn row. There is no
separate read tool. Both are absent without Biotech (`ModsConfig.BiotechActive`).

The life stage, race, gene, xenotype, mech kind and work mode defs are read from the Defs
themselves (`LifeStageDef`, `RaceProperties.lifeStageAges` and
`lifeStageWorkSettings`, `GeneDef`, `XenotypeDef`, `PawnKindDef`,
`MechWorkModeDef`); no wire catalog field carries them.
The brain builds `bridge.BiotechCatalog` as a facade
over the catalog's rows once per load token: genes by name, the humanlike
races' work minimum ages, the controllable mech kinds (a `PawnKindDef` of a
mechanoid race with an overseer-subject comp; its bandwidth cost is the race's
`BandwidthCost` stat value) and the role modes, with `GeneTuning` carried
through.

`PawnState.biotech` (`PawnBiotech`) carries a pawn's life stage and
developmental stage, learning need level and category, genes (endogene or
xenogene, active or overridden), xenotype, mechanitor bandwidth and controlled
mechs, a mech's overseer, work mode and control group index, and deathrest
state. An absent scalar is unknown, never zero; a failed sub-read leaves its
fields absent and adds a `ReadIssue` named `life_stage`, `developmental_stage`,
`learning`, `genes`, `mechanitor`, `mech`, `deathrest`, `gene_lifecycle` or
`extractable`. Gene-lifecycle fields: `xenogerm_regrow_ticks_left` and
`xenogerm_coma_ticks_left` are the `HediffComp_Disappears` timers of
`XenogermReplicating` and `XenogerminationComa` (known 0 with no such hediff;
a pawn with regrow ticks left dies if extracted again), `in_extractor` a pawn
held by a gene extractor, and `extractable` the game's own
`Building_GeneExtractor.CanAcceptPawn` verdict (true when any owned extractor of
the pawn's map accepts) with `extractable_reason`, the first refusal text (ASCII,
absent when the game gave none). `extractable` is absent for an unspawned or
non-humanlike pawn and on a map with no extractor, so the brain never reimplements the
passOnDirectly, archite or coma rules. The brain lifts the block into
`policy.PawnBiotech` on the work pawn (`WorkPawn.Biotech`) and the pawn
profile. `PawnProfile.Child` follows the developmental stage (Newborn, Baby,
Child) the Biotech block names; a colony without Biotech has no block and no
child pawns, and a Biotech block without the stage fails the lift. Both are derived state held
in memory (persistence-contracts.md); they add no store.

## Odyssey defs and facts

Odyssey (ground only) follows the shared wire pattern: static
defs ride the definition catalog and per-thing facts ride the canonical
building row. There is no separate read tool and no Odyssey intent. All of it
is absent without Odyssey (`ModsConfig.OdysseyActive`).

The Odyssey static defs (biomes, `TileMutatorDef`s, `CompProperties_Hackable` thing defs,
`MapPortalProperties` thing defs, biome diseases, each `BiomeDef`'s wild animal kinds and the
stockpile types) are read from the game's Def objects by the brain's catalog; the wire catalog
carries no Odyssey block.
`BuildingState.odyssey` carries a building's hack progress, defence, hacked,
locked-out and autohack state (`CompHackable`) and a portal's pocket map
existence and id, plus an ancient hatch's stockpile type and layout. An absent
scalar is unknown; a failed sub-read leaves its block absent and adds a
`ReadIssue` named `hackable` or `portal`. The brain lifts the blocks into
`policy.Hack` and `policy.Portal` (`bridge.BuildingHack`, `BuildingPortal`);
all of it is derived state held in memory (persistence-contracts.md) and
adds no store.

`ColonyFactsSnapshot.odyssey` (`OdysseySection`) is the colony-wide
Odyssey read, one keyed section with the snapshot's seq/tick; it is
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
lists; an absent scalar is unknown, never zero. The brain validates the rows
(`bridge.validateOdysseyColony`: unique ids per table, lava cells on the map,
nonnegative ticks, progress a fraction, no `ticks_left` on a permanent
condition) and projects them into `observation.OdysseyColony`
(`ColonyProjection.Odyssey`). Derived state held in memory
(persistence-contracts.md); it adds no store.

## Anomaly defs and facts

Anomaly follows the shared wire pattern: static defs ride the
definition catalog and per-pawn and per-building facts ride the canonical rows.
There is no separate read tool and no Anomaly intent. All of it is absent
without Anomaly (`ModsConfig.AnomalyActive`).

The Anomaly static defs (EntityCategoryDef, KnowledgeCategoryDef, EntityCodexEntryDef, the entity thing
rows and the anomaly IncidentDef rows, Creepjoiner form, benefit and downside rows) are read from the game's
Def objects by the brain's catalog, never from brain name lists; the wire catalog carries no Anomaly block. Def numbers
are the def values verbatim and are only checked for being finite: vanilla defs carry -1 (Alligator
`studyAmountToComplete`, a holding target escape interval), whose meaning the consuming goal sources before it reads the field.

`PawnState.anomaly` carries `Pawn.IsEntity` and `IsMutant`, an
entity's `MinimumContainmentStrength`, its `CompHoldingPlatformTarget` state
(held on a platform, the ordered `EntityContainmentMode`, escaping,
the extract-bioferrite flag, whether the platform has an attached bioferrite
harvester (`harvester_attached`), `bioferrite_per_day`
(`CompProducesBioferrite.BioferritePerDay`, 0 while BioferriteExtracted stands),
whether it can be captured) and its `CompStudiable`
state (completed, progress, points, knowledge gained, the knowledge
category and amount the game resolves). Three threat facts feed the
defense tactics: `hidden_from_player` (`InvisibilityUtility.IsHiddenFromPlayer`:
the player cannot see or target the pawn, so no attack order names it),
`psychic_ritual_invoker` (the pawn's role in its lord's psychic ritual is the
ritual def's invoker role: the caster, who is focus-fired and shelled) and
`melee_only` (its `CurrentEffectiveVerb` is a melee attack and none of its
abilities is `ai_IsOffensive`; an entity or mutant that is melee only fights
as a charging pack, so squad defense and the manhunter tactic apply). Each is
read alone: a failed read is absent with a `ReadIssue` named for it.
Hostility stays `PawnState.hostile`.
A pawn with a creepjoiner tracker carries `anomaly.creepjoiner`:
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
`entity`, `held`, `holder`, `study` or a threat fact's name. The brain lifts the blocks into
`policy.PawnAnomaly` and `policy.BuildingAnomaly` (`bridge.PawnAnomaly`,
`BuildingAnomaly`); all of it is derived state held in memory
(persistence-contracts.md) and adds no store. Thing rows (items, corpses) carry
no study state.

`ColonyFactsSnapshot.anomaly` (`AnomalySection`) is the colony-wide
Anomaly read, one keyed section with the snapshot's seq/tick; it is
absent without Anomaly and without `observed` when the read failed. It carries what
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
implant can occur) and `monolith` (#2436, absent unless the monolith is
spawned: the monolith's `monolith_id` (#2437, the thing a give-job targets);
`can_activate` from `Building_VoidMonolith.CanActivate`; the next level
def from `GameComponent_Anomaly.NextLevelDef` with the codex category and count
its def requires and the entries still undiscovered; the active game conditions
the next level lists as unreachable, e.g. `UnnaturalDarkness` for VoidAwakened;
whether the Gleaming interaction (`CompGleamingMonolith.CanInteract`) is
available; spawned and activated `VoidStructure` counts, the ids of the structures
still interactable, and whether a `VoidNode` exists on any loaded map (it stands in
the pocket map once the Gleaming monolith is used) with its id and the free
colonists spawned there; and the void awakening stage, derived from the
`stageStructure.N` quest tags of the spawned structures and absent while the
`EndGame_VoidAwakening` quest is not running). Level effects and requirements
come from the game defs through the native read, not the brain tables. A held entity's strength and need stay on its rows
(`HeldState`, `EntityHolderState`, `PawnAnomaly.min_containment_strength`).
Selection is by the game's own types, never name lists; an absent scalar is
unknown, never zero. The brain validates the rows (`bridge.validateAnomalyColony`:
unique names and ids, discovered at most entries, nonnegative finite knowledge
and threat fraction, nonnegative levels) and projects them into
`observation.AnomalyColony` (`ColonyProjection.Anomaly`). Derived state held in
memory (persistence-contracts.md); it adds no store. Active game conditions
(for example the gray pall) are not here: the shared condition read lives in
the Odyssey section today.

## Room-role furniture

The brain catalog lists the roles the game's code
names a building definition for, sorted: `Toy`, `Decoration`, `Board` and `Desk`
(its `ThingDefOf` toy box, baby decoration, blackboard and school desk, the rows
its baby and school jobs name). The brain derives the other two from the def rows:
`BabyBed` (`building.bed_crib`) and `DeathrestCasket` or `DeathrestAccelerator`
(a building carrying `CompProperties_DeathrestBindable`, a casket when its class
is a bed). The brain refuses an unknown, duplicate or unsorted role and plans child
rooms and the deathrest chamber from the catalog rows carrying a role
(`policy.FurnitureRole`, `bridge.DefinitionCatalog.RoomRoleRows`). The
reference assembly carries no method bodies, so the game's room role workers'
own counting is unverified against these rules (native acceptance verifies).

## Biotech colony section

`ColonyFactsSnapshot.biotech` (`BiotechSection`) is the colony-wide
Biotech read, one keyed section with the snapshot's seq/tick; it is
absent without Biotech and without `observed` when the read failed. The pollution
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
is unknown, never zero. The brain validates the rows (`bridge.validateBiotechColony`:
unique ids per table, cells on the map, nonnegative counts) and projects them
into `observation.BiotechColony` (`ColonyProjection.Biotech`). Derived state
held in memory (persistence-contracts.md); it adds no store.

Gene-building rows sit in the same section:
`gene_banks` (any building with `CompGenepackContainer`: powered, capacity,
held pack ids, auto-load), `gene_assemblers` (`Building_GeneAssembler`: powered,
working, `max_complexity` from `MaxComplexity()`, linked bank ids; while
working also progress, total work, selected pack ids, archites owed and the
`CanBeWorkedOnNow` verdict), `gene_extractors` (`Building_GeneExtractor`:
powered, working, `SelectedPawn`, occupant; while working ticks remaining and
cumulative power-cut ticks), `genepacks` (each pack held by a bank, with
`bank_id`, or spawned loose, with a cell; genes as GeneDef defNames, hit
points, `Genepack.Deteriorating`, auto-load) and `xenogerms` (spawned items
with genes and the pending implant target). Complexity, metabolism and archite
totals on packs and xenogerms are the game's own `GeneSet` values, never
summed in the brain. Each xenogerm also carries `implant_metabolism`: the
game's `GeneUtility.MetabolismAfterImplanting` for every colonist, slave or
colony prisoner on its map (one row per pawn id; the implant is refused below
the biostat range minimum). Run facts are absent while idle, a banked pack has no cell, and
the xenogerm name is not on the wire (user text). The brain validates unique ids per
table, nonnegative counts, distinct gene ids and that a pack is in exactly
one place, a bank listed in `gene_banks` or a cell
(`bridge.validateGeneBuilding`), and projects into
`BiotechColony.GeneBanks`/`GeneAssemblers`/`GeneExtractors`/`Genepacks`/
`Xenogerms`. Read-only: no write or goal consumes these yet.

`pollution` also counts the polluted pollutable cells (`polluted_cells`) and
those outside the pollution-clear area (`polluted_uncovered_cells`).
`RoundsFacts.Pollution` (`policy.PollutionFacts`) projects the wastepack
verdicts and that count; it is unknown without Biotech, and so is the
`biotech/pollution` Need (Wellbeing, labor Hauling, #3261). Its Method is the
forbidden packs' `supply_allow`, the pollution-clear `AreaIntent` edit
(polluted cells of the planning window) and the wastepack-guarded HAUL
(`wastepack_haul`); an allowed pack is hauled on a later Method. Admission
takes only the allows the review's `PollutionSupplies` cohort lists (#3317).
Siting freezer storage is not planned yet (open on #1683).

## Polluting-building flag

`observation.PlanningDefinition.Pollutes` is a view over the def
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
what the Empire quest planner maximises. the brain read carries
`FactionState` id and hostile beside the quests, and a quest with no
`map_id` or one off the identity map is off-map. A `QuestPart_GiveRoyalFavor`
outside every choice's part list emits a reward with absent `choice_index`:
its favor applies independently of reward selection. The brain represents this with
choice -1 and does not count it as an option.

`QuestReward.items` contains exact native item def names and stack counts.
`goodwill` carries the signed faction relation change.
`psylink` counts neuroformers or the link awarded by a bestowing ceremony.
Bestowing rows carry the title's `permit_points` and permit
def IDs. These benefits are observed from native reward types; policy does not
parse `kind`, labels or descriptions. `QuestState.choice_part_count` reports
the number of independent native choice parts; more than one remains unsupported.

Reward selection prioritizes favor while a recorded holder lacks the next
title's favor, then items that meet observed resource, construction or food
shortages, then the remaining native benefits. Ties choose the lowest option
index. Independent favor contributes equally to every option.

`QuestState.objectives` carries a closed `QuestObjectiveKind`: produce_item,
harvest_plant, kill_animals, host_lodgers, accept_requirement_unmet,
load_pawns, load_named_pawns, monument, expiry, or unknown. Definition and stuff
are native def names; count and produced are native required and completed units.
Missing optional fields remain unknown. `deadline_ticks` is an absolute game
tick; expiry is the offer expiry. Named pawn objectives carry exact pawn IDs.
`unmet_requirement` is native display text for an acceptance refusal, never
policy input. Unsupported parts emit unknown without raw part data. These rows
are derived observations, not persisted intent.

`QuestObjective.active` is the native activable part's enabled state; it is
absent for a non-activable part. Decree work rows use the earliest enabled
native timeout as their absolute deadline. A decree's `asker_pawn_id` and
`asker_faction_player` come from its `DecreeUnmet` situational-thought recipient,
the exact requester reference in the native decree script. Conflicting or
missing recipients remain unknown. `violent_quests_allowed` is the current
storyteller difficulty setting.

Completed monuments belonging to an ongoing quest remain protected at the native
write boundary: construction and reinstall previews refuse their sketch area;
removal guards refuse sketch pieces, its terrain and the marker. The guard reads
live quest state and ends when the quest ends. Unrelated disallowed structures
can still be removed. This is an intent precondition, with no additional store.

## Obtainable operation preconditions

References are `Ref {id}`; an `EntityRef` is only a row head (id,
definition, label, map, position). A write's CAS token is a `SnapshotRef`
sibling of the reference (`pawn_snapshot`, `source_snapshot`, ...), required,
context-scoped and bound to the exact ID; a reference without one cannot supply
an EntityPrecondition. Missing producer support yields unavailable; tokens are
never fabricated from a label, position, tick alone, or public wire bytes.
Tokens cover the relevant native facts and domain-specific settings, not authority.

| Operations precondition | Read path |
|---|---|
| DesignateIntent.thing_id | GetCells.thing.snapshot / ListPawns.pawn.snapshot / ListBuildings.building.snapshot |
| WorkSettingsIntent.pawn_id | ReadPawnSettings (same pawn ID) |
| ProductionBillIntent.bench_id | ReadBills.bench (same bench ID) |
| RemoveProductionBillIntent.bench_id/bill | ReadBills.bench and ReadBills bill id (the bill id the placing receipt's `BillEffect`/`SurgeryEffect` returned) |
| ZoneIntent.zone (Ref) | ListZones.zone.id; ListBuildings storage row id |
| AssignIntent.pawn_id/thing_id/expected_previous | ListPawns.pawn and owned bed; ListBuildings.building; ColonyFactsSnapshot.royalty.thrones.thing for a throne |
| NeedReliefIntent.pawn/job/schedule | ListPawns.pawn.snapshot, JobEvidence, PawnSettings.schedule |
| PawnOrderIntent WEAR pawn/target | ReadGear.pawn.snapshot, candidate.item.thing.snapshot, GearLoadout.snapshot |
| HusbandryIntent.animal_id/target_id | ListPawns pawn with animal details (same animal ID), allowed area and master IDs |
| PrisonerInteractionIntent.pawn_id | ReadPopulation.pawn and current interaction |
| SetDrafted/AttackTarget/PawnOrderIntent | ListPawns.pawn.snapshot; exact target snapshot from ResolveTarget/GetCells/entity reads |
| OpenTrade.trader/negotiator | ListTraders.trader.snapshot/negotiator.snapshot |
| SetTradeLines/AcceptTrade/EndTrade.session | ReadTradeSheet.snapshot; trader and negotiator from ReadTradeSession |
| SetTradeLines.line_id | ReadTradeSheet.lines.line_id, scoped to frozen sheet; not an inferred DefName/index |

PlaceBuilding uses its placement preview and write authority precondition; it has
no separate EntityPrecondition.

Draft claims expose exact claim_id, original Owner (brain session, player
direction) and pawn scope. Boolean drafted does not prove ownership; unowned and
unavailable are distinct. Cleanup compares the unchanged claim even after
authority is revoked. Trade line IDs are opaque and sheet-scoped, catalog group
IDs catalog-scoped; neither is derived from labels or definition names.

Clearance: `GetClearanceTargets` reads visible, deconstructible non-player buildings touching Home. Salvage evidence on the rows outside Home is read only when the request sets `include_salvage`; the rounds and the planner that execute remote salvage set it, the shelter ruin holds and claims do not. It retains partial Home overlap, sealed ancient-danger membership, counterfactual roof blockers, faction and a standing deconstruct designation (no ownership flag). The same read lists the chunk stacks standing in Home (`chunks`: forbidden, stored, hauling destination). The `home-upkeep/clearance` Need consumes both.

Trade session and sheet headers carry one `common.TradeTarget`, whose settlement arm names the player caravan. `ReadTradeAcquisition` exposes native request prerequisites, all passing ships, queued arrivals/comms work and optional exact-crew packing estimates. Presence remains significant; [controllable trade](../../docs/developers/contracts/controllable-trade.md) defines eligibility and reconciliation.

