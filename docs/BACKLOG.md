# RimBot backlog

This is the single queue for implementation gaps, remaining reuse audits and
gameplay acceptance. Work top-down within each priority. Check the current code
before adding an API; available native tools often need acceptance, not rebuilding.
Understanding belongs in [explanation](explanation/overview.md), exact contracts in
[reference](reference/README.md), and procedures in [how-to guides](how-to/README.md).
Use the [documentation map](README.md) to navigate. Remove completed items once their evidence is recorded
in the checkpoint commit; do not append an implementation diary here.

## P0 — Reliable startup execution

- [ ] **B04 · Broader sustained foothold coverage.** Extend the two-day native
  acceptance matrix to more seeds, eight-colonist starts, scarce wood, low
  fertility and temperature variants. Include longer survival, changing seasons
  and production that replaces initial supplies. Sampled stable gates over a
  bounded window do not establish arbitrary long-term colony survival.
  Resolve the combat/medical hold when all available doctors are controller-drafted
  and a defender needs tending; preserve player draft ownership and combat safety.
- [ ] **B04h · Complete startup and colony upkeep.** Implement the phased plan
  below through existing ColonyPlan goals, deterministic methods and Hands. Use
  observed deficits, urgency and player priorities rather than a fixed day-by-day
  script. Emergency work preempts development; independent affordable work may
  proceed together. Keep completed capabilities under maintenance as needs change.
  B04a owns food production, B04f emergency/power methods, B06/B06a spatial/farming
  choices, B06b facilities, B06c defensive layouts and B07 durable scheduling;
  this item owns their startup sequencing and the upkeep gaps between them.

  **Phase 1 — Finish needs and completion contracts.** Extend the
  [native upkeep audit](reference/upkeep-contracts.md) with filtered storage
  capacity, safe roof-support
  replacement and seasonal lead-time evidence. Preserve unknowns. Extend the
  maintained supply, cleaning, repair and fire contracts to the remaining goals:
  sleeping use, facilities, food-chain operation, animals, medical reserves,
  clothing and workforce changes. Each needs entry/recovery thresholds, a bounded
  method, ownership, progress evidence and an explicit blocker. Add stable scoring
  and hysteresis so minor changes do not rebuild facilities or reassign work.

  **Phase 2 — Secure landing supplies and sleeping.** Reuse suitable shelter and
  storage before building. Add covered general storage with native filters and
  capacity for vulnerable supplies, separate from food-specific storage. Prioritize
  safe hauling by loss risk and survival value; extend verified covered-storage
  hauling to split/merged stacks, unavailable capacity and interrupted deliveries.
  Item loss must remain distinct from successful protection.
  Restore temporary hauling overrides when the deficit clears. Upgrade floor
  sleeping to affordable beds through B06b, preserving usable sleeping capacity
  during replacement and respecting assignments and player furniture. Verify
  actual bed use, access and safe sleeping temperatures. Introduce dining and
  recreation after urgent survival deficits; preserve player schedules and avoid
  unnecessary schedule rewrites when ordinary needs-driven behavior suffices.

  **Phase 3 — Fire, cleaning and repair upkeep.** Maintain deliberate home-area
  coverage for occupied facilities and protected stock without expanding cleaning
  work across arbitrary terrain. Preserve player area edits. Match safe firefighting
  and repair jobs to capable available workers; isolate dangerous fires and use
  explicit retreat/hold behavior when safe intervention is unavailable. Prioritize
  kitchen/clinic contamination and damaged essential structures over cosmetic work.
  Stage stone-block production and wood-wall replacement through B06b/B07 only
  after native support/access checks; retain roofs, enclosure and escape routes
  throughout each replacement batch. Verify extinguished fires, completed repairs
  and remaining support rather than designations or worker assignments alone.

  **Phase 4 — Operate the food chain.** Extend B04a/B06b from bills and rooms to
  reachable ingredient staging, output storage, hauling and cleaning capacity.
  Keep dirty processing appropriately separated from clean preparation using
  observed native effects. Reuse suitable bills and preserve ingredient restrictions.
  Maintain meal buffers against actual demand, cook availability and spoilage;
  do not count planned harvest as stored food. Establish and maintain refrigeration
  when justified, including cooler placement/exhaust, power and measured storage
  temperatures. On failure, reassess food deadlines and prioritize safe hauling,
  repair or bounded cooking rather than assuming the freezer remains functional.

  **Phase 5 — Sustain animals and medical supplies.** Detect starting animals and
  native pen requirements; establish temporary safe containment or a suitable pen,
  then verify actual containment, reachable feed and seasonal feed reserves.
  Account for animal consumption separately from human food and protect sensitive
  stores with appropriate areas/filters. Do not automatically slaughter, release,
  breed or change bonded-animal policy to resolve a feed deficit. Maintain medical
  reserves, audit native healroot eligibility and acquisition/production options,
  and coordinate replenishment with B06b clinics and existing medical response.
  Preserve patient care policies; unavailable supplies or staff remain visible.

  **Phase 6 — Prepare for seasonal and workforce changes.** Derive preparation
  urgency from native growing conditions, temperature exposure, consumption and
  lead times. Connect B06a crop/greenhouse choices to food reserves, animal feed,
  heating fuel and clothing acquisition/production. Respect outfits, research and
  resource policies; verify worn protection rather than crafted-item receipts.
  Reassess workload when colonists join, become ill or lose capabilities; protect
  essential hauling/cleaning and backup coverage without taking over player choices.
  Prefer upgrades that relieve observed bottlenecks; avoid speculative stockpiles
  and research queues unrelated to admitted needs.

  **Acceptance and rollout.** Land each phase with deterministic fixtures for
  thresholds, unavailable facts, resource competition, player overrides and
  no-progress recovery, followed by isolated native acceptance before expanding
  scope. Include interrupted writes, partial construction, manual intervention,
  map/load changes and paired restart without duplicate orders or lost ownership.
  Run scenarios for scattered supplies, no safe storage, bed shortages, dirty
  kitchens, blocked hauling, nearby fire, unsafe fire, supported wall replacement,
  freezer outage, starting pen/pet animals, feed scarcity, medicine shortage,
  unavailable workers and cold-season clothing/fuel deficits. Verify completed
  hauling, sleeping, repairs, food replenishment, containment and actual equipment
  use. Extend B04 campaigns across seeds, colony sizes and resource/season variants;
  record stock/need trends, interruptions, blockers, labor/travel cost and recovery
  after initial supplies run down. Keep foothold status distinct from demonstrated
  sustained survival; do not claim support from blueprints, labels or one happy path.
  Strategy references: wiki [Quickstart](https://rimworldwiki.com/wiki/Quickstart_Guides)
  and [Basics](https://rimworldwiki.com/wiki/Basics). Their scenario-specific advice
  guides requirements; installed native rules and player policy govern execution.

## P1 — Functional colony planning and recovery

- [ ] **B06a · Deterministic crop and farm plot selection.** Coordinate with B04a
  food control and B08 forecasts. Replace distance-first rice patch packing with
  bounded, explainable crop/site scoring shared by starter and expansion methods.
  Prefer nearby rich soil for rice/corn, but penalize travel, hauling, fragmentation
  and unsafe access enough that distant rich soil can lose to suitable local soil.
  Use stable tie breaks and configurable distance/score limits; calibrate the
  tradeoff with measured pawn work rather than inventing a universal distance cutoff.
  Prefer contiguous additions to compatible controller-managed farms before new
  compact fields; reserve isolated 1x1 cells for constrained-soil fallback. Preserve
  player crop choices, existing plants, other zones, entrances and reserved routes.
  Reuse native `home/zone_cells` add/preview support through durable shared actions
  with exact before/after geometry, direction guards and uncertain-write recovery;
  do not let native add transfer cells from another zone.
  Current planning facts expose fertility and free-ground geometry within 22 cells
  of the colony center, but only rice crop definitions. Start with local fertility,
  compactness and adjacency ranking; distance is only a proxy until native route
  evidence is available. Audit existing definition/query tools before extending
  observations for corn/potatoes, fertility sensitivity, sow/harvest work, effective
  yield, light/rest cycles and remaining growing season. Use installed native
  definitions and difficulty modifiers, not fixed wiki statistics.
  Prefer rice when first-harvest urgency or a short season dominates; consider corn
  when reserves and the growing window support its longer cycle and lower labor.
  Prefer potatoes on nearby growable stony soil/gravel when better soil is too far
  away, subject to the same urgency check; bare rock is not growable soil. Rank
  conservative nutrition per tile/day alongside time to first harvest and labor.
  Rich soil improves growth speed, not yield per harvest. Keep projected production
  separate from stored food and observed growing cells; unknown season/access data
  must not certify food security or trigger destructive crop switching.
  Extend selection to crop/site/infrastructure combinations: outdoor soil, sun-lamp
  greenhouses over natural soil, sun-lamp hydroponics and eligible dark fungus rooms.
  Favor controlled environments as observed resources, power reliability, seasonal
  limits and travel/space costs justify them; do not use a fixed late-game flag.
  Compare reuse of existing facilities with the incremental material, construction
  labor and operating costs of new ones. Score additional productive season,
  actually usable illuminated cells, heating/cooling, grower labor and hauling.
  Read native lamp schedules, light footprints, basin loads and crop compatibility;
  daylight peak demand, continuous basin power and thermal demand must fit the
  power network, not merely a nighttime surplus. Sun lamps do not require basins;
  corn can use greenhouse soil but cannot use hydroponics. Distinguish interrupted
  growth in soil from crop loss in unpowered basins, including solar flares and
  thermal failures; retain food reserves and avoid concentrating all production
  behind one failure mode. Coordinate infrastructure prerequisites with B04f.
  For fungus, verify native availability, suitable darkness, temperature, soil and
  food preferences; start with existing suitable rooms before planning a facility.
  Replace blanket indoor-farm refusal only with verified crop-specific light and
  environment contracts. Unknown infrastructure observations must block admission.
  Acceptance: nearby versus distant rich soil, poor-soil potato selection, urgent
  rice versus adequately buffered corn, short/unknown seasons, contiguous expansion,
  fragmented terrain, preserved player zones, changed direction and lost receipts.
  Cover greenhouse reuse versus new construction, daytime power shortfalls, lamp
  coverage, incompatible basin crops, winter heating, outages and dark-room eligibility.
  Add deterministic fixtures, then native scenarios observing sowing, travel,
  harvest and replenished food stocks; zone receipts alone do not establish success.
  Strategy references: RimWorld Wiki [rice](https://rimworldwiki.com/wiki/Rice_plant),
  [corn](https://rimworldwiki.com/wiki/Corn_plant),
  [potatoes](https://rimworldwiki.com/wiki/Potato_plant) and
  [plant growth](https://rimworldwiki.com/wiki/Plants#Fertility),
  [sun lamps](https://rimworldwiki.com/wiki/Sun_lamp),
  [hydroponics](https://rimworldwiki.com/wiki/Hydroponics),
  [outage consequences](https://rimworldwiki.com/wiki/Events_Guide) and
  [nutrifungus](https://rimworldwiki.com/wiki/Nutrifungus).
- [ ] **B06b · Deterministic room and facility planning.** Cover every role in the
  RimWorld Wiki [room-role catalog](https://rimworldwiki.com/wiki/Rooms#List_of_roles):
  Room (generic), Bedroom, Prison cell, Dining room, Rec room, Hospital, Laboratory,
  Workshop, Storeroom, Barracks, Prison barracks, Kitchen, Tomb, Barn, Throne room,
  Temple, Nursery, Playroom, Classroom, Deathrest chamber, Containment cell and
  Ceremonial chamber. Retain functional variants such as breweries, drug labs,
  research labs, clinics, freezers and B06a greenhouses even when they share a
  native role or have no dedicated role label. Coordinate with B04f development, B06 spatial planning
  and B07 scheduling; extend the shared goal/action system and deterministic Hands.
  Trigger projects from observed unmet demand and player priorities, reuse existing
  rooms/equipment first, and support compatible shared rooms instead of requiring
  a separate building for every role. Rank bounded sites using access, hauling,
  available space, native placement constraints and construction/operating costs.
  Extend resource-target production beyond existing benches: discover native
  recipes, research and facility prerequisites, then stage missing shells,
  equipment, power/fuel, ingredient storage, bills and capable worker coverage.
  Include multi-stage brewing and intermediate products without duplicate bills
  or unbounded stockpiles. Preserve player bill settings, resource reserves and
  drug policies; production targets do not authorize changing consumption policy.
  Define role-specific observed requirements: clinic bed designation, medicine
  access, doctor coverage and cleanliness; dining seating/table access near food;
  usable recreation appropriate to observed needs; production bench interaction
  cells, lighting, temperature and reachable inputs/output storage. Discover native
  definitions and room evidence instead of hardcoding assumed room bonuses.
  Maintain a per-role implementation/acceptance matrix with native prerequisites,
  furnishings, assignments, capacity, applicable stats and observed-use predicates.
  Gate specialized rooms on installed content and actual colony demand, including
  prisoners, animals, children, titles, ideology, deathrest and containment needs;
  catalog coverage is not an instruction to construct every room in every colony.
  Discover native role scoring and compatibility rules: the displayed role alone
  does not establish all supported uses. Respect bed ownership/designations,
  forbidden furniture combinations and purpose-specific roof/indoor requirements.
  Admit dependency chains only with known prerequisites and shared resource
  budgets; expose missing research, power, materials or staffing as blockers.
  Preserve existing plants, rooms, routes and player furniture; invalidate pending
  work after player direction, map/load changes or edited facility geometry.
  Acceptance: reuse versus new build, constrained placement, competing projects,
  missing prerequisites, interrupted construction, edited bills and restart without
  duplicate orders. Start with dining/recreation and one bill-based workshop,
  then clinic and multi-stage production; retain the remaining catalog roles as
  explicit pending matrix entries until each has native acceptance. Native scenarios must observe completed
  facilities and actual eating, recreation, treatment or manufactured output;
  blueprint acceptance, room labels and bill receipts alone do not prove function.
- [ ] **B06c · Defensive layouts and killbox strategy.** Add deterministic methods
  for layered perimeter defense, controlled approaches, chokepoints and killboxes,
  coordinated with B06 spatial planning, B04f emergency response and B09 combat.
  Compare bounded terrain-aware layouts against available defenders, native weapon
  ranges, line of sight, cover, materials, research and power. Reuse defensible
  terrain and existing structures; stage affordable cover and fallback positions
  before costly walls, traps or turrets. Preserve civilian access, hauling routes,
  entrances, retreat paths and safe defender deployment during construction.
  Validate firing arcs, friendly-fire exposure, melee contact, trap access/rearming,
  doors, repair access and shared resource reservations using native observations.
  Treat enemy routing through a killbox as a conditional tactic, not a guaranteed
  colony invariant. Include explicit fallback/hold behavior for attacks that bypass
  or invalidate the approach, including breaches, sappers, sieges and internal/drop
  threats; verify actual native threat behavior rather than assuming funnel use.
  Connect completed defensive geometry to bounded rally, engagement and withdrawal
  methods through the shared plan and Hands. Recheck layout and threat observations
  after player edits, damaged structures, changed equipment and colony/load changes.
  Acceptance: constrained terrain, staged construction, safe civilian/defender
  routes, actual cover and firing behavior, ordinary approaching threats, bypassed
  defenses, retreat and repair after damage. Controller geometry tests and placed
  blueprints do not establish enemy pathing, raid victory or sustainable defense;
  require isolated native scenarios with observed movement and combat outcomes.
- [ ] **B06d · Lighting, floors, routes and facility utilities.** Extend B06b room
  methods and B04h upkeep with maintained environment requirements, using the
  existing shared goals, resource accounting and Hands. Audit native observations
  and actions before adding APIs; terrain `supportsLight` means structural support,
  not illumination. Distinguish safe/legal placement from functional performance.
  Lighting: observe light at actual work and interaction cells, identify relevant
  native penalties/requirements, and choose affordable fixtures and coverage with
  power/fuel dependencies. Reuse existing light, respect player preferences and
  darkness-dependent crops, and repair coverage after outages or layout changes.
  Keep ordinary workplace lighting separate from B06a crop-light requirements.
  Flooring: select native materials by role-specific cleanliness, movement, beauty,
  flammability, availability and cost. Prefer targeted kitchen/clinic improvements
  and measured traffic bottlenecks before decorative coverage. Preserve growing
  soil, player floors, access and room function during bounded installation or
  replacement; include material production and hauling in the admitted budget.
  Routes: use observed pawn reachability and travel evidence to connect beds,
  workplaces, stores, dining and defense positions. Score travel reduction against
  paving/construction cost; protect door interactions, widths, retreat and civilian
  paths. Do not mistake local flood-fill connectivity or straight-line distance
  for map-wide safe routing. Re-evaluate after obstacles, doors or threats change.
  Cleanliness: define observed targets for kitchens and clinics, assign bounded
  cleaning response when normal work coverage fails, and distinguish removable
  filth from floor/building contributions. Site butcher work and other dirty
  processing using native cleanliness effects; avoid contaminating shared clean
  workspaces. Verify cleaned cells/room stats and restore temporary work overrides.
  Power: implement B04f generation/connectivity methods that choose available native
  generators, fuel supply, conduits and storage against actual connected loads,
  operating schedules and reliability needs. Account for daylight peaks, sustained
  deficits and stored energy; expose unknown capacity or unavailable research as
  blockers. Verify each critical consumer is connected and powered, not just that
  some network has surplus. Include refueling, repairs and bounded outage recovery.
  Refrigeration: complete B04h freezer operation with verified enclosure/roofing,
  cooler orientation, unobstructed heat rejection, seasonal cooling demand and
  protected electrical capacity. Measure food-storage temperature and spoilage
  recovery; a placed cooler or nominal thermostat is insufficient evidence.
  Acceptance: dark/partially lit benches, protected fungus rooms, changed layouts,
  filthy versus inherently dirty rooms, kitchen/butcher separation, interrupted
  flooring, unreachable stores, costly detours, disconnected consumers, exhausted
  fuel/batteries, day/night load changes and hot-weather freezer failure. Add
  deterministic fixtures and isolated native runs observing illumination, cleaning,
  completed floors, actual travel, generation/refueling and maintained temperatures.

- [ ] **B29 · Colony-wide development priorities.** Arbitrate comfort, research,
  production, defense and expansion through the existing deterministic priority tree
  and shared plans. Extend the bounded storage/defense/resource admission order to
  comfort (B19) and expansion (B06/B06b) as their methods become
  available. Replace coarse worker-count capacity with native profession-specific
  labor, bottleneck, lead-time and risk scoring alongside shared resource commitments.
  Preserve emergency precedence, player goals and explicit deferral reasons.
  Calibrate deficit/age/hysteresis weights and verify starvation resistance under
  actual competing native demands, capacity loss/recovery, player interruption,
  uncertain writes and paired restarts, then sustained B04 campaigns. Controller
  replay establishes bounded admission and retained identities, not actual pawn
  progress or completion-time forecasts. Advisers may suggest priorities but cannot
  own invariants or bypass admission; do not create a second planner/executor.

All B19–B29 methods require native capability discovery, durable identities,
player-direction/load guards, uncertain-write reconciliation and observable
completion through the shared goal/action system. Use focused deterministic tests
and isolated native scenarios, then sustained B04 campaigns. Existing reads,
commands or forecast outputs do not establish autonomous management. Prioritize
remaining capability work after urgent startup gaps, with native acceptance for
each bounded method.

## P2 — Coverage, inspection and evaluation scale

- [ ] **Broader scenario observer coverage.** Measure retained-frame rendering and
  observation overhead during sustained campaigns. Bridge-only probes without a
  `BridgeRuntime` have no controller state to publish; add explicit observation
  adapters where needed without introducing another controller or game owner.

- [ ] **Contextual and queued action extensions.** The [native capability audit](reference/player-actions.md)
  identifies live menu opening that can execute an order, option execution without
  a menu-session token, and no explicit queued-job postcondition. Add guarded
  contextual/dropdown/reverse-designator requests only with exact current selection,
  menu identity, native eligibility and observed effects. Verify real Shift-queued
  jobs, replacement/cancellation and queue completion before exposing this fallback.
- [ ] **Animal and prisoner command extensions.** Expose individual master, allowed-area
  and following requests, then accept normal tame/release/pen and pair-separation
  workflows. Extend prisoner capture and interaction
  settings with actual prisoner outcomes through the shared executor.
- [ ] **Personal policy and storage-range extensions.** Add typed food/drug/apparel
  policy creation, editing and assignment with native readback. Distinguish assigned
  restrictions from actual consumption/wearing. Cover stockpile quality/hit-point
  ranges separately from the supported configurable special-filter flags.
- [ ] **Dashboard native acceptance.** Verify Outpost time controls against a
  rendered isolated session: pause, normal/fast/superfast, danger refusal, new
  player direction and load invalidation. Verify action follow on pawn, building
  and placement writes, turning it off and loading another map. Validate that
  native lead/capture timing makes the selected action visible. Add manual-camera
  suppression and configurable/decoupled cinematic pacing before claiming a
  continuous high-speed director.
- [x] **B15 · World progression.** Shared expedition and quest commands evaluate
  participant eligibility, route/time risk, supplies, capacity, home staffing and
  return storage against player policy, economic reserves and B22 care commitments.
  Scoped native observations distinguish packing, departure, arrival, quest
  acceptance, fulfillment, rewards and stored returns; uncertain writes are not
  replayed. Native acceptance covers loaded round trips, settlement gifts and
  goodwill, acquired quest goods and received rewards, failed/expired objectives,
  short-supplied party recovery, competing cargo, emergency stops and multiple
  active maps with stale-map refusal. Two days of prepared-colony survival and
  cold-exposure readiness refusal are accepted; sustained seasonal survival and
  autonomous foothold coverage remain in B04. See the
  [world progression procedure](how-to/world-progression.md) for exact scenario scope.
- [ ] **B30 · DLC gameplay systems.** Maintain an installed-content capability and
  acceptance matrix; B06b room coverage does not establish the associated gameplay.
  Discover native definitions and prerequisites, gate methods on actual colony need
  and player policy, and keep missing workflows explicit. Extend the shared goals
  and Hands rather than adding a DLC-specific execution path.
  **Ideology:** needs/precepts, role assignments, ritual eligibility and outcomes.
  **Royalty:** title obligations, permits, psycast eligibility and bounded use.
  **Biotech:** childcare/education and growth, gene workflows, mechanitor control,
  mech production/repair/charging, waste/pollution, deathrest and hemogen supply.
  **Anomaly:** entity capture/containment, study, extraction and ritual workflows,
  with observed containment risk and recovery from failures.
  **Odyssey:** gravship construction/support, departure/arrival prerequisites,
  exploration and destination-specific survival through discovered native contracts.
  Keep irreversible choices and escalation of optional threats within player
  direction. Accept each workflow separately with native state changes and actual
  pawn/entity outcomes; test unavailable DLC, mixed content, interruptions and
  multi-map transitions. Add endgame objective planning only for selected player
  goals, with prerequisites and completion evidence appropriate to installed content.
- [ ] **B16 · Sustained throughput.** Windows two-worker clock/lifecycle isolation
  passed; parallel inference throughput remains unmeasured. Identify boosted-speed
  pause causes; measure useful completed tests/minute, memory, startup, inference,
  observation and action overhead across colony ages and render/capture modes.
  Interrupted burst TPS is not sustained episode throughput. Use the read-only
  `scripts/dashboard_throughput.py` to retain wall TPS including pauses and exclude
  load/rewind/disconnection intervals. Compare isolated rendered, suspended and
  headless runs from the same checkpoint, with follow off and no competing workers.
  Measure observation age in game ticks, danger-to-pause latency, player-stop latency,
  verified outcomes per minute and missed safety gates at each candidate speed.
  Keep Ultrafast test-only until its reaction envelope is accepted.
  Batch only the retained starting-supply targets through discovered native contracts:
  the current method emits up to eight cell-wise Unforbid operations with repeated
  validation. Whole-map unforbid would broaden scope to unrelated player restrictions.
  Profile per-operation preview, identity, native dispatch and readback cost before
  removing redundant reads; uncertain writes still require observation before retry.
  Consider adaptive game-tick review windows for stable colonies, preserving native
  hazard supervision and a bounded observation age. Archive/reuse verified checkpoints
  for long-lived scenarios while keeping fresh-start acceptance separate.
- [ ] **Colonist dossier rendering coverage.** Broaden the native portrait/follow
  probe to modded weapon icons, apparel/body types, removed pawns,
  load/map transitions, competing viewers and simultaneous main-view video.
  Measure simulation/rendering cost while the follow view polls; the one-second
  snapshot interval is not a video frame-rate or latency guarantee. Paused native
  camera/selection invariance and offscreen moving-pawn captures have a dedicated
  probe; do not infer these broader outcomes from compilation or HTTP fixtures.

- [ ] **Windows checkpoint deletion.** Close SQLite read connections before
  deleting a saved pair. `test_explicit_deletion_preserves_other_pairs_and_native_saves`
  currently fails with a locked `bridge.sqlite` on Windows, including on unchanged
  main. Verify deletion preserves the other pairs and native saves.

- [ ] **Reusable-game coverage.** Run `game_reuse_acceptance.py` in a native Linux
  worker and repeat the real-model execution suite with `--reuse-game`. Headless
  Windows acceptance covers three baseline resets, restored supplies, released
  draft ownership, fresh controller state and revoked old clients; Linux fixtures
  do not establish native reuse or model interpretation. Other probes/campaigns
  retain their existing lifecycle; adopt reuse only with explicit reset contracts
  and keep fresh-process/static-state acceptance separate.

## Completion rule

For each item record the observed failure, focused fix, source revision, checks,
native outcome and remaining limitation in its commit. Protocol tests, scripted
native acceptance and real-model gameplay are separate evidence levels. Preserve
failed trials locally. Do not reintroduce duplicate transports, manager hierarchies,
CLI wrapper services, external consultants, fixed reviewer daemons, turn clocks,
instant equipment cheats or hardcoded game facts as unfinished reuse work.
