# RimGovernor backlog

This is the single queue for implementation gaps and gameplay acceptance. Work top-down within each priority. Check the current code
before adding an API; available native tools often need acceptance, not rebuilding.
Use the [player and developer docs](README.md) for current behavior and commands. Remove completed items once their evidence is recorded
in the checkpoint commit; do not append an implementation diary here.

## P0 — Reliable startup execution

- [ ] **B04 · Broader sustained foothold coverage.** Extend the two-day native
  acceptance matrix to more seeds, eight-colonist starts, scarce wood, low
  fertility and temperature variants. Include longer survival, changing seasons
  and production that replaces initial supplies. Sampled stable gates over a
  bounded window do not establish arbitrary long-term colony survival.
  Close eight-colonist startup deficits: establish crop labor and interim food before
  initial rations run out; extend verified furniture-aware startup storage to
  changed geometry and sustained recovery; and reconcile interrupted
  equipment/hauling and upkeep watchdogs from native outcomes.
  Extend bounded stable-patient feeding acceptance to withdrawal recovery and
  concurrent food production in sustained campaigns.
  Mixed hunting coverage must resolve unsafe-route holds without bypassing the
  native guard. Crop-only Peaceful trials are separate from Rough survival acceptance.
  Provide safe triage while hostiles remain when all doctors are controller-drafted;
  preserve player draft ownership and combat safety. Post-combat owned-draft release
  does not establish active-combat treatment. Repeat the sustained matrix after fixes;
  targeted medical/refusal passes do not close these survival requirements.
- [ ] **B04h · Complete startup and colony upkeep.** Implement the phased plan
  below through existing ColonyPlan goals, deterministic methods and Hands. Use
  observed deficits, urgency and player priorities rather than a fixed day-by-day
  script. Emergency work preempts development; independent affordable work may
  proceed together. Keep completed capabilities under maintenance as needs change.
  B04a owns food production, B04f emergency/power methods, B06/B06a spatial/farming
  choices, B06b facilities, B06c defensive layouts and B07 durable scheduling;
  this item owns their startup sequencing and the upkeep gaps between them.

  **Phase 1 — Finish needs and completion contracts.** Extend the
  [native upkeep audit](developers/contracts/upkeep-contracts.md) with safe enclosure/escape
  replacement and seasonal lead-time evidence. Roof-support previews, saved
  construction lineage and guarded straight-wall replacement are available.
  Preserve unknowns. Extend the
  maintained supply, cleaning, repair and fire contracts to the remaining goals:
  facilities, food-chain operation, animals,
  clothing and workforce changes. Each needs entry/recovery thresholds, a bounded
  method, ownership, progress evidence and an explicit blocker. Add stable scoring
  and hysteresis so minor changes do not rebuild facilities or reassign work.

  **Phase 2 — Secure landing supplies and sleeping.** Reuse suitable shelter and
  storage before building. Extend covered general storage to obstructed interiors
  and changed native capacity without disturbing player filters. Prioritize
  safe hauling by loss risk and survival value; extend verified covered-storage
  hauling quantity tracking to unavailable capacity and interrupted pawn work.
  Native split/merge accounting, pre-delivery loss and saved quantity/construction
    identities across paired restart and ordinary resumed pawn delivery are verified.
  Item loss must remain distinct from successful protection.
  Restore temporary hauling overrides when the deficit clears. Extend verified
  floor-to-bed upgrades to shortages, unavailable research/materials, unsafe
  temperatures, interrupted construction and changed player assignments across
  load/restart scenarios. Extend verified dining/recreation startup to changed
  access, removed furniture, unavailable sites/materials and paired restart.
  Preserve player schedules and ordinary needs-driven use.

    **Phase 3 — Fire, cleaning and repair upkeep.** Deliberate Home coverage for owned
    facilities and stockpiles preserves player exclusions, rejects stale area revisions
    and survives paired restart. Extend acceptance to changed facility geometry and
    sustained maintenance. Match safe firefighting
  and repair jobs to capable available workers; isolate dangerous fires and use
  explicit retreat/hold behavior when safe intervention is unavailable. Prioritize
    kitchen/clinic contamination; native repair priority favors essential structures
    over cosmetic work. Extend recovery across workforce and facility changes.
  Stage stone-block production and wood-wall replacement through B06b/B07 only
  after native support/access checks; retain roofs, enclosure and escape routes
  throughout each replacement batch. Verify extinguished fires, completed repairs
  and remaining support rather than designations or worker assignments alone.
    Guarded straight and corner replacement retain roof support and salvage access.
    Native acceptance covers ordinary construction, Manual interruption, material-loss
    retirement and paired restart without duplicate demolition. Extend replacement to
    changed geometry and broader enclosure/escape scenarios; add explicit recovery or
    reauthorization of retired batches and sustained resource-loss campaigns.

  **Phase 4 — Operate the food chain.** Extend B04a/B06b from bills and rooms to
  reachable ingredient staging, output storage, hauling and cleaning capacity.
  Keep dirty processing appropriately separated from clean preparation using
  observed native effects. Reuse suitable bills and preserve ingredient restrictions.
  Maintain meal buffers against actual demand, cook availability and spoilage;
  do not count planned harvest as stored food. Establish and maintain refrigeration
  when justified, including cooler placement/exhaust, power and measured storage
  temperatures. On failure, reassess food deadlines and prioritize safe hauling,
  repair or bounded cooking rather than assuming the freezer remains functional.

  **Phase 5 — Sustain animals and medical supplies.** Extend verified native pen
  construction and containment to interrupted construction, changed pen filters,
  unavailable handlers and larger starting herds. Extend verified native feed
  production and consumption to protected staging inside pens, unavailable benches
  and ingredients, mixed herds, recurring depletion and seasonal feed reserves.
  Retire owned reserve production when its animal demand is removed without
  altering player-owned bills.
  Account for animal consumption separately from human food and protect sensitive
  stores with appropriate areas/filters. Do not automatically slaughter, release,
  breed or change bonded-animal policy to resolve a feed deficit. Extend verified
  wild-medicine reserve replenishment to cultivated healroot, exhausted wild sources,
  recurring harvest ownership and unavailable staff. Coordinate production with
  B06b clinics and existing medical response.
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

- [ ] **B06e · Irregular rooms and circular/oval tribal huts.** Extend new room
  construction beyond rectangular shells through the existing shared plan and
  deterministic Hands. Reuse exact-cell adoption and furnishing of existing
  irregular rooms; this does not yet establish construction of arbitrary shapes.
  Represent exact connected interior cells, boundary walls and entrances, retaining
  bounds only for indexing and bounded reads. Provide deterministic rectangle,
  circle and ellipse generators, including orientation and size controls for oval
  teepee-style tribal layouts, then support connected irregular footprints.
  Player chat selects shape, size and style; geometry generation and game orders
  remain deterministic. Preserve rectangular callers and saved-plan compatibility.
  Extend overlap, zone, doorway, roof-support, resource-cost and access checks to
  exact geometry at admission and dispatch. Fit furniture using native footprints
  while preserving entrance aisles, narrow connectors and interaction cells.
  Discover available materials and native building definitions. Circular or oval
  ordinary-wall huts provide the initial footprint feature; literal teepee/tent
  appearance requires suitable installed content or separately scoped art and
  building definitions, with provenance retained for reused assets.
  Acceptance: completed circular and oval huts in multiple sizes/orientations,
  concave rooms, narrow connectors, constrained terrain, native enclosure/roofing,
  actual pawn access and furnishing use. Include partial construction, player
  edits, material shortages and paired restart without duplicate orders. Geometry
  fixtures and accepted blueprints alone do not establish usable native rooms.

- [ ] **B06f · Staged excavation and mountain-base rooms.** Build on B06e exact
  geometry and existing irregular-room adoption to excavate requested rooms and
  corridors through normal pawn mining. Keep this separate from resource-target
  surface mining, whose current safety guard deliberately excludes roof-adjacent
  excavation, including supported tunnels; do not relax that guard globally.
  Discover native rock, roof, support and mining eligibility. Plan bounded stages
  that preserve support and worker access, retaining natural pillars or completing
  required supports before dependent excavation. Treat unknown/fogged cells as
  unknown and reobserve newly exposed space before extending work. Hold and
  reassess on newly revealed threats, unsafe support or changed player geometry.
  Account for labor, debris hauling, construction materials and usable access;
  complete required doors, walls and furnishings before adopting habitable rooms.
  Use shared dependencies, durable identities and uncertain-write reconciliation;
  manual direction and colony/load/map changes invalidate pending work.
  Acceptance: actual pawn excavation of irregular rooms and connecting corridors,
  support retained throughout staged work, enclosure and functional room use,
  blocked access, interrupted mining, changed roofs/supports, revealed hazards and
  paired restart. Verify native outcomes without editor excavation or bypassing
  ordinary roof-collapse rules. See [spatial contracts](developers/contracts/spatial-contracts.md)
  and [mining contracts](developers/contracts/mining-contracts.md) for current boundaries.

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

- [ ] **Native event delivery and scheduling throughput.** Python work scheduling
  wakes on direction, review completion and budget-limited Hands completion,
  independently of dashboard refreshes. Compare fixed-input setup-to-pawn-work
  timings under uncontended native execution. Reuse RimBridgeServer's existing
  GABP event transport for native notifications: pinned GABS currently consumes
  attention subscriptions internally without forwarding general events to MCP,
  and the companion's clock events are durable journal reads. Add forwarding and
  clock publication through that transport, retaining journal cursors for reconnect,
  gaps and duplicate delivery. Keep lease renewal, fresh write guards, idle backoff
  and direction/load/plan invalidation; bridge operation completion is not pawn work.

- [ ] **Paused setup through the first simulation tick.** Use the
  [controller profiler](developers/testing/measure-throughput.md) to measure the complete path
  from enabling automation to the first supervised tick. Separate repeated
  validation, persistence, controller scheduling and bridge time; optimize the
  measured costs without weakening fresh write checks. Acceptance must actually
  reach a supervised execution window; a sample that ends while issuing paused
  setup orders does not close this item.

- [ ] **Bridge request overhead.** Use the boundary timings in
  [throughput measurements](developers/testing/measure-throughput.md) to split remaining MCP
  session time into GABS ownership preparation, transport/decoding and native
  scheduling. Profile remaining shared observation inputs before relaxing serialization.
  Construction preflight, allocation, site searches and material alternatives
  already use bounded preview batches.
  Hands coalesces zone/entrance reads within each read-only shell preflight;
  actual writes retain fresh checks.
  Native observations already support one
  request for the standard seven sections; retain identity invalidation, native
  interruption guards and durable mutation records in further optimizations.

- [ ] **Linux-volume storage for other native test launchers.** Audit remaining
  launchers for synchronous writes through host bind mounts. Extend the throughput
  and standard scenario launchers' private-volume contract where measurements justify it: unchanged
  SQLite/recorder durability, export only after stopping the worker, database
  integrity checks, retained failure evidence and recovery volumes/containers,
  and verified cleanup after success. Preserve live scenario dashboards and an
  explicit bind-storage comparison option. Throughput and standard scenario workers
  use this contract; verify each additional launcher's normal, failed and interrupted runs.

- [ ] **End-to-end native throughput acceptance.** Compare unprofiled runs with
  fixed images, inputs, speed/recording settings and uncontended Docker resources.
  Measure time to first simulated tick, completed pawn work and completed scenarios,
  including startup, setup, pauses and evidence export. Verify native outcomes and
  interruption behavior alongside timing. Faster previews, more dispatched orders
  or profiled component timings alone do not establish simulation TPS or scenario
  completion gains; retain failed trials and report the exact accepted scope.

- [ ] **Broader scenario observer coverage.** Measure retained-frame rendering and
  observation overhead during sustained campaigns. Bridge-only probes without a
  `BridgeRuntime` have no controller state to publish; add explicit observation
  adapters where needed without introducing another controller or game owner.

- [ ] **Contextual and queued action extensions.** The [native capability audit](developers/contracts/player-actions.md)
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
  [world progression procedure](developers/testing/world-progression.md) for exact scenario scope.
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
- [ ] **Colonist dossier rendering coverage.** Broaden the native portrait/follow
  probe to modded weapon icons, apparel/body types, removed pawns,
  load/map transitions, competing viewers and simultaneous main-view video.
  Measure simulation/rendering cost while the follow view polls; the one-second
  snapshot interval is not a video frame-rate or latency guarantee. Paused native
  camera/selection invariance and offscreen moving-pawn captures have a dedicated
  probe; do not infer these broader outcomes from compilation or HTTP fixtures.

- [ ] **Reusable-game coverage.** Run `game_reuse_acceptance.py` in a native Linux
  worker and repeat the real-model execution suite with `--reuse-game`. Headless
  Windows acceptance covers three baseline resets, restored supplies, released
  draft ownership, fresh controller state and revoked old clients; Linux fixtures
  do not establish native reuse or model interpretation. Other probes/campaigns
  retain their existing lifecycle; adopt reuse only with explicit reset contracts
  and keep fresh-process/static-state acceptance separate.

## G01 — Go controller rewrite

This is the implementation plan and single work queue for replacing the Python
production controller with Go. The steps below describe proposed behavior, not
capabilities already available. Existing B-series gameplay gaps remain open;
porting a feature does not establish its missing native acceptance.

### Decision and scope

Use Go for the external controller, retaining the React dashboard, native C#
colony bridge, GABS/RimBridgeServer transport and configured local LM Studio.
Own contracts in language-neutral schemas and generate Go structs, C# DTOs and
TypeScript types where applicable. Shared language is not required for shared
contracts; there is no established substantial cross-process logic reuse that
requires a C# controller. Native game eligibility and simulation remain in C#.

The objective is explicit types, smaller interfaces, predictable state ownership
and maintainable execution. Improved CPU performance and packaging are potential
benefits, not acceptance evidence. Measure complete native outcomes before claiming
a speedup. Do not combine the rewrite with new gameplay policy, a transport
replacement, UI redesign or a new planner architecture.

The production Go process must eventually own lifecycle, observations, shared
plans, deterministic policy and Hands, recovery, SQLite persistence, model chat,
HTTP/events, video orchestration and diagnostics. Python may remain for development
and native scenario tooling. A production Python sidecar is not a completed rewrite.

Development state is disposable. Start the Go runtime with a fresh database and
fresh game state; old saves, Python database imports, reverse compatibility,
rollback rehearsals and exact historical wire/signature parity are not acceptance
gates. Keep existing evidence as optional reference. Prioritize working vertical
slices and current behavioral tests; do not add compatibility capture campaigns.
Current-session durability, cancellation, one-writer ownership and ordinary native
outcomes remain required.

### Contracts and package boundaries

Proposed paths become real only when their owning chunk lands:

| Path | Ownership and restrictions |
| --- | --- |
| `contracts/` | Canonical versioned wire schemas, boundary tests and generation manifest; no game assemblies. |
| `go/` | One Go module with pinned toolchain/dependencies and `cmd/rimgovernor`; avoid a module per subsystem. |
| `go/internal/wire/` | Generated wire types and explicit boundary decoding/validation; generated files carry provenance. |
| `go/internal/domain/` | Owned IDs, observations, action variants, plan specification/progress, receipts and failures; no transport or database dependencies. |
| `go/internal/bridge/` | MCP discovery, capability/version negotiation, bounded reads, native call receipts and transport diagnostics. |
| `go/internal/store/` | Fresh Go SQLite state, transactions, inbox/outbox durability and restart recovery. |
| `go/internal/policy/` | Deterministic priorities, domain methods, resource admission and geometry; separate files/packages by capability. |
| `go/internal/hands/` | The sole automated native mutation path, guarded execution and reconciliation. |
| `go/internal/runtime/` | Session lifecycle, state ownership, scheduling, interruption and supervision. |
| `go/internal/model/` | Local inference, semantic request validation, budgets and advisory evidence. |
| `go/internal/server/` and `go/internal/presentation/` | Existing dashboard API/events, player controls, portraits, camera and video lifecycle. |
| `go/internal/testkit/` | Fake transport, injected clocks/IDs, replay readers and reusable fault injection; no production fallback. |

Use small consumer-owned interfaces for native reads/writes, storage, clocks and
model calls. Keep wire DTOs separate from internal state so transport evolution
does not spread optional fields throughout policy. Domain actions must be explicit
variants with validated constructors; state transitions must reject unsupported
variants. Do not assume Go switches provide exhaustive variant checking: add
coverage checks for action/handler registration.

Do not replace Python dictionaries with `map[string]any`, reflection dispatch or
unchecked string assertions in the core. Limit `json.RawMessage` and generic maps
to transport extensions, raw evidence and diagnostic payloads. Represent unknown
facts separately from known zero/false, distinguish missing and null where the wire
does, and use integer game ticks and distinct colony/map/load/action/direction IDs.
Keep native definition names discoverable strings rather than hardcoded game enums.

Schemas specify required fields, bounds, variants and unknown-field handling.
Generated structs need boundary validation. Use stable action identities within
the new Go store and refuse stale or unsupported work. C# DTOs must build against
the native `net472` target; serializer settings are explicit. Existing Python
serialization formats do not constrain the new disposable state format.

The existing `bridge_observation.schema.json` describes a Python projection, not
the entire native wire surface. Inventory actual tool schemas and native replies
before generalizing it. Repository-owned native contracts can be generated;
third-party/modded tools still require runtime discovery, capability checks and
validation. Unknown mutation semantics must remain unavailable to automation.

### Runtime correctness gates

Every chunk preserves these contracts:

1. One durable goal/action system and one active native writer. Advisers cannot
   issue orders; routine control performs zero model calls. Player controls use
   their existing explicit ownership path and invalidate automated work.
2. Persist intent before dispatch. A timeout/cancellation after dispatch means
   uncertain outcome, not safe retry. Observe native effects before recovery;
   receipts do not establish completed pawn work.
3. Capture colony/map/load, direction, plan revision and relevant native generations;
   recheck after awaits and before writes. Manual, load changes, rewinds and player
   direction cancel pending authority. Cancelling a Go context cannot undo a game order.
4. Serialize state commits and mutation admission. Slow I/O cannot block reception
   of player interruption indefinitely. Returned asynchronous results are applied
   only if their captured generations remain valid. Shutdown disarms supervision,
   drains/closes resources and does not start new work.
5. Preserve transaction boundaries, request deduplication and event cursors in
   new Go sessions. Unknown recovery evidence fails closed. Use a fresh Go store;
   never let Python and Go open the same live writable database.
6. Preserve native discovery, normal pawn work, fresh placement/resource checks,
   bounded execution windows, durable recording and UI drafts/last-good data.

Use the existing [controller](developers/contracts/controller-contracts.md),
[action](developers/contracts/action-contracts.md), [recovery](developers/contracts/recovery-contracts.md),
[persistence](developers/contracts/persistence-contracts.md),
[session](developers/contracts/session-contracts.md) and
[interface](developers/contracts/interface-contracts.md) contracts as behavioral requirements.
Document deliberate corrections separately; Python output is comparison evidence,
not an oracle that overrides those requirements.

### Team execution and landing protocol

Appoint one integration agent and at most three implementation agents per wave.
Each uses a separate `codex/go-<chunk>` worktree based on the latest integrated
dependency commit. Assign only dependency-ready work. The integrator owns shared
schemas, public interfaces, module dependencies, build/CI, launchers and backlog
status; workers propose shared changes before editing those files.

Each assignment names a chunk/subchunk, base commit, allowed paths, source modules,
contract pages, dependencies, deliverables, test commands and explicit exclusions.
Before starting, inspect current main: ongoing B-series fixes may change the source
behavior to port. Record the comparison revision and refresh relevant fixtures when
those fixes land. Do not silently revert them during integration.

Each row below is an independently reviewable landing unit. A row that exceeds
one coherent change must be split into numbered subchunks before assignment, with
their dependencies and coverage entered here. Do not submit an entire subsystem
rewrite in one opaque commit. Keep main runnable with Python as the default until
G01.12 passes. Incomplete Go paths are explicitly gated; no silent per-operation
fallback to Python and no dual-writer mode.

For each completed unit: run focused tests and contract neighbors, commit local
changes, report exact evidence and remaining limits. The integrator rebases onto
current main, resolves shared-file conflicts, reruns affected checks after material
changes and performs `git merge --ff-only` from a clean, coordinated main checkout.
If main advances or becomes dirty, stop that landing and coordinate; never reset
or overwrite another worker's edits. No merge commits and no pushes unless asked.
Update the relevant checkbox and evidence in the landing commit. Keep reports,
databases, native recordings and temporary tooling under ignored `.rimgovernor/` paths;
only intentional small, sanitized regression fixtures belong in source control.

### Sequenced chunks

- [x] **G01.00 — Inventory and comparison baseline.** Owner: integration agent.
  Inventory every production Python module, HTTP/event surface, native tool used,
  semantic command, completion kind, store table/migration, configuration option,
  launcher and optional media feature. Create a machine-readable coverage manifest
  under `contracts/` mapping each item to its Go owner, fixtures, native scenarios
  and migration status; mark tooling-only/vendor code explicitly. Capture sanitized
  representative observations, plans, receipts, failures, API responses and saved
  states, with source revision and provenance. Use existing throughput tools to
  establish uncontended Python timing where licensed inputs are available; otherwise
  record that gate as pending. Accept when the manifest accounts for all runtime
  entry points and each capability has a named test/acceptance owner. Do not infer
  completeness from file counts. Dependencies: none.

  Dependency-ready inventory subchunks (all compare against the current Python
  revision; integrator combines them before accepting 00):
  - [x] **00a:** production modules, semantic commands, completion kinds and
    domain/native capability ownership; contracts/domain-inventory.json.
  - [x] **00b:** HTTP/events, configuration, launchers and optional media surfaces;
    contracts/interface-inventory.json.
  - [x] **00c:** persistence/recovery surfaces and sanitized comparison fixtures;
    contracts/state-inventory.json and contracts/fixtures/.
  - [x] **00d:** integrator coverage validation, provenance and uncontended baseline
    availability; depends on 00a–00c. Representative native timing is in contracts/python-baseline.json.
    - [x] **00d.1:** reconcile production Docker helpers and canonical Go ownership;
      inventory argument-dependent native read/write boundaries (domain agent).
    - [x] **00d.2:** inventory platform discovery variables and tooling cache CLI
      options with source checks (interface agent; independent of 00d.1).
    - [x] **00d.3:** retain exact Python serialization/signature comparison cases
      and uncertain issued-action evidence (state agent; independent of 00d.1–2).
    - [x] **00d.4:** integrator final coverage review and baseline availability
      record; depends on 00d.1–3. The representative Python sample retains partial-construction holds
      and a warning pause. Do not stop another task's native scenario.

  Existing fixtures are optional behavior references. Add current boundary and
  outcome tests with their Go consumer chunks; no legacy-state capture is required.

- [x] **G01.01 — Go build and replay foundation.** Owner: integration agent.
  Add the module, minimal non-writing command, injected clocks/IDs and offline
  replay runner. Pin a supported Go release, MCP SDK, SQLite driver and generators
  after checking licenses, Windows/Linux support and dependency maintenance. Decide
  CGO requirements explicitly; do not promise static binaries before media/SQLite
  choices are tested. Add formatting, `go vet`, unit tests, supported race tests and
  Windows/Linux builds to CI while retaining current checks. Establish fixture
  normalization that ignores only documented nondeterministic fields, never IDs,
  action order, generations or uncertainty. Accept reproducible clean builds and
  a replay test that detects a deliberately altered action or receipt. Depends on 00.
  - [x] **01a:** pinned module/dependencies, attribution and a non-writing CLI;
    compile/test MCP and SQLite dependency support with explicit connection closure.
    Owner: integrator. Depends on the 00 inventory/baseline gate.
  - [x] **01b:** injected clocks/IDs and offline replay with documented normalization,
    exact numeric/identity/order handling and deliberate action/receipt corruption
    tests. Owner: replay agent. Depends on 01a.
    - [x] **01b.1:** bounded raw-evidence JSON comparison and injected clock/ID
      sources; no runtime adapters. Owner: replay agent.
    - [x] **01b.2:** read-only file replay command, errors and file-based tests.
      Owner: CLI agent; final validation depends on 01b.1.
  - [x] **01c:** retained Python/dashboard checks plus Go formatting, vet, unit/race
    tests and Windows/Linux build CI; clean platform validation and evidence.
    Owner: integrator. Depends on 01b.

- [ ] **G01.02 — Schema generation and first native contract.** Owner: integrator
  and native N01.02 implementer. Land the generator, then a bounded typed current
  request/reply and real native invocation. Expand schemas with actual consumers.
  Existing Python models are optional test helpers, not a compatibility obligation.
  Depends on 01; 03/04 and model transport can proceed after the tested Go
  generation API in 02a.1 while remaining platform checks run.
  - [x] **02a:** pinned repository-owned Go generator and a documented, closed
    schema subset; required/null/unknown and integer-token/UTF-16 validation,
    deterministic output manifest and drift checks. Owner: integrator with generator
    agents. Exercise the placement request as the first concrete input; native
    consumer changes and complete reply decoding remain gated by 02b–d.
    - [x] **02a.1:** canonical request schema, generator input/validation model and
      Go output with positive/negative generation checks. Owner: generator agent;
      integrator owns canonical schema, manifest and CI integration.
    - [x] **02a.2:** C# and transitional Python outputs from the same schema/model,
      with cross-language boundary fixtures. Owners: C# and Python generator agents;
      depends on the 02a.1 generator API. Unsupported schema features fail explicitly.
    - [x] **02a.3:** generation drift CI, reproducibility and full affected checks.
      Owner: integrator; depends on 02a.1–2.
  - [ ] **02b:** define the complete typed reply needed by the first migrated
    preview operation, including success/refusal and unknown facts. Owner: contracts
    integrator with native implementer; depends on 02a. Do not replicate every
    historical optional field when the current consumer does not need it.
  - [ ] **02c:** wire generated native request validation and typed preview reply
    through the real SDK to Go. Owner: native N01.02 implementer; depends on 02b.
    Coordinate with the unified native package paths. Native rules remain unchanged.
  - [ ] **02d:** one fresh isolated valid/refused/invalid invocation with no preview
    effects. Owner: native implementer; depends on 02c. This runs alongside 03/04;
    those chunks depend on the generation foundation, not historical parity.

- [ ] **G01.03 — Read-only transport and observation.** Owner: bridge agent.
  Port MCP process ownership, discovery, typed current observations, freshness,
  identity checks and bounded/cancellable calls. Define read versus mutation APIs;
  read-only mode rejects write-capable tools even when requested by name. Test
  transport failures/reconnects and one native read with no writes or clock changes.
  Add generated observation families as their Go consumers need them. Depends on 02a.1.
  - [x] **03a:** owned MCP subprocess/session, discovery and bounded read-only
    calls with cancellation/cleanup; test a real in-process SDK server and failed
    connections. Owner: bridge agent. No mutation API or runtime scheduler yet.
  - [ ] **03b:** typed current identity/status and observation facts, explicit
    unavailable values and freshness. Owner: bridge agent; depends on 03a and the
    domain fact types. Add families with policy consumers and one native read smoke.

- [x] **G01.04 — Typed plan and fresh Go store.** Owner: state agent. Split into
  04a plan/action/progress types and 04b SQLite persistence/restart. Use a new Go
  schema with explicit version checks; do not import Python databases or preserve
  old serialization signatures. Keep transactions, durable intent, deduplication,
  unknown-write reconciliation and action identities correct within new sessions.
  Test rollback, reopen/restart and connection closure with real temporary SQLite.
  Depends on 02a.1; extend variants with their actual handlers.
  - [x] **04a:** distinct IDs/generations, plan/spec/progress and a bounded building
    action variant with legal transitions. Owner: state agent; no legacy serializers.
    Extend action families only alongside their Hands handlers.
  - [x] **04b:** fresh versioned SQLite store, transactions/durable intent and
    reopen/restart checks. Owner: state agent; depends on 04a.

- [ ] **G01.05 — Deterministic planning kernel.** Owner: policy agent.
  Port plan readiness/dependencies, resource accounting, priority admission,
  hysteresis, geometry and method interfaces from `colony_policy.py`,
  `resource_accounting.py`, `spatial.py`, `room_geometry.py` and related modules.
  Build explicit typed inputs instead of passing the runtime object. Inject stable
  clocks/IDs; sort map-derived decisions and use deterministic tie breaking. Accept
  behavioral cases for deficits, unknown facts, competing projects, player priorities,
  cancellation and starvation/hysteresis cases. Property/fuzz tests cover reservation
  conservation, duplicate IDs and invalid geometry. No native writes. Depends on 04a.

- [ ] **G01.06 — Guarded Hands and runtime vertical slice.** Owner: executor agent.
  Split into 06a execution state machine, 06b lifecycle/supervision, and 06c native
  construction acceptance. Port the relevant paths in `hands.py`, `bridge_runtime.py`,
  construction grounding/preflight and projects. First support a bounded explicit
  building action end to end: typed plan, durable intent, guarded preview/write,
  receipt, later observed completion. Do not enable unsupported actions. Keep state
  ownership explicit and use context cancellation plus generation validation.
  Accept lost replies, cancellation before/after dispatch, stale plans, map/load
  changes, partial placement, resource loss and restart, followed by actual pawn
  construction in an isolated scenario. Benchmark scheduling without weakening
  guards. Depends on 03, 04b and 05.

- [ ] **G01.07 — Routine capabilities in bounded families.** Owners: domain agents.
  Each subchunk includes policy/method compilation, typed native arguments, Hands
  handler, postcondition reconciliation, restoration and native scenario parity.
  Use `colony_controller.py`, `colony_skills.py` and the manifest's domain modules;
  do not copy their large dispatch functions. Contracts/handler registration are
  integrated serially, then independent family implementations may proceed in
  parallel. Every supported action/completion kind must map to a tested handler.
  Depends on 06; family dependencies below are minimum prerequisites.
  - [ ] **07a:** emergency combat, draft ownership, critical medical triage and
    treatment/recovery. Accept player draft preservation, interrupted care and
    unsafe threat holds; do not claim unresolved active-combat care is solved.
  - [ ] **07b:** food acquisition/production, crops, cooking and work assignments;
    follows 07a for emergency preemption. Accept stock changes, ordinary pawn work,
    renewed deficits, unsafe routes and interrupted production.
  - [ ] **07c:** shelter/adoption, spatial reservations, storage/hauling, beds,
    Home coverage, repairs, fire and staged wall upgrades; follows 07b for startup
    sequencing. Accept exact geometry, retained supports, player exclusions,
    quantity/lineage accounting and changed-layout recovery.
  - [ ] **07d:** temperature, power, facilities, equipment, research, material
    extraction and resource development; follows 07c. Accept scarce-resource
    competition, actual equipped/produced outputs and unavailable prerequisites.
  - [ ] **07e:** mood, extended medicine/surgery, animals, waste, population and
    policy trade; follows 07a and 07b, plus facility dependencies named in 00.
    Accept recurring deficits, protected player policies, custody/admission and
    observed goods/health/containment outcomes, not command acknowledgments.
  - [ ] **07f:** caravans, quests and multi-map world progression; follows 07d
    and 07e. Accept departure, arrival, return/storage, failure and stale-map
    rejection under existing world-progression scenarios.

- [ ] **G01.08 — Local model and player semantics.** Owner: model agent.
  Split transport/budget/advice from command-family implementations. Port `model.py`,
  `model_router.py`, `request_budget.py`, `planner.py`, `player_commands.py`,
  consultation/scout/visual review and required knowledge/memory/evidence behavior.
  Preserve configured local endpoints, streaming, cancellation, context limits,
  structured-response validation, bounded repair and exact evidence retrieval.
  All semantic commands enter the same plan/executor. Unknown commands fail
  explicitly; advisers have no mutation interface. Accept scripted invalid replies,
  deduplicated chat submissions, cancelled streams and a real configured-model
  check of explicit player requests. Assert zero inference for routine events.
  Transport/budget work depends on 02a.1; plan submission depends on 04/06, and each
  command family waits for its 07 handler.
  - [x] **08a.1:** local-only HTTP chat transport, bounded responses, streaming,
    cancellation and explicit errors. Owner: integrator; depends on 02a.1. No
    plan submission, provider fallback or routine-control inference.
  - [ ] **08a.2:** prompt/context budgets and structured semantic validation.
    Owner: model agent; depends on 08a.1 and 04a for command types.

- [ ] **G01.09 — Dashboard API and presentation parity.** Owner: server agent.
  Split 09a cached HTTP/events and command endpoints, 09b player ownership/camera,
  and 09c portraits/follow/video. Port `bridge_server.py`, dashboard controls,
  scenario dashboard and presentation modules using an endpoint-by-endpoint
  compatibility manifest. Preserve status/error envelopes, event cursors, request
  IDs, drafts/last-good state, loopback defaults and player lease semantics. Keep
  privileged UI/editor actions outside model execution. Choose and validate media
  dependencies here; preserve documented optional modes and explicit unavailable
  states. Accept unchanged dashboard typecheck/tests/build, API fixture comparison,
  reconnects, two-viewer ownership conflicts, stale commands and rendered native
  camera/video acceptance. No production Python media service remains. 09a read
  endpoints depend on 03/04; mutations and media require 06 and relevant 07/08 work.

- [ ] **G01.10 — Whole-runtime integration.** Owner: integrator. Run Go as the
  sole controller in fresh disposable scenarios. Connect supported domain handlers,
  model chat, dashboard, interruption and restart; account for implementation gaps
  in the inventory. Test current behavior, not exact Python decision recordings.
  Depends on 07, 08 and 09.

- [ ] **G01.11 — Go packaging and launch.** Owner: integrator. Update Windows and
  Docker launch/build paths and scenario adapters to run the Go process. Keep private
  profiles, loopback dashboard ports and one-writer ownership. Start with fresh state;
  no save/database migration or rollback rehearsal is required. Python may remain
  for test tooling but cannot be a production controller sidecar. Depends on 10.

- [ ] **G01.12 — Acceptance and default switch.** Owner: integrator. Run Go checks,
  generation drift and affected dashboard checks. Verify fresh native startup,
  ordinary pawn work, explicit player control, interruption and Go-session restart.
  Keep known gameplay gaps explicit. Switch to Go when the integrated controller is
  functional and operable; historical parity and performance comparison campaigns
  are not gates. Do not claim unmeasured performance improvements. Depends on 11.

- [ ] **G01.13 — Retire production Python.** Owner: integration agent.
  After default-switch acceptance,
  remove Python production entry points, runtime-only dependencies and duplicate
  implementations. Preserve accepted fixtures, attribution and explicitly retained
  Python scenario/development tools. Update setup, architecture, source map,
  troubleshooting and CI to describe Go as the actual runtime. Accept a production
  image/install with no Python interpreter required, all manifest rows resolved,
  and no launcher or dashboard route silently using Python. Depends on 12.

### Dispatch waves and evidence checklist

Sequence the work by dependencies, not by assigning one agent the entire Python
directory. First land 00–02 serially. Then 03 and 04 can run in parallel; 05 starts
after 04a. Land the 06 vertical slice before broader mutations. After that, schedule
07 families alongside 08 and 09 within the team limit, respecting each handler's
dependencies. Integrate 10–13 serially. Independent acceptance workers may run only
with isolated inputs/resources; do not replace installed DLLs while any game runs.

Every handoff includes: base and result commits, changed contracts, commands and
exit codes, skips, fixture/native/model scope, artifact paths, migration/rollback
impact and remaining manifest rows. Native waits use
`rimgovernor.native_scenario.advance_game` while Python tooling remains; any Go-native
replacement must first match its interruption and tick-budget acceptance. New
Python script-based Docker runs use `scripts/container_scenario.py` and its dashboard
helpers. Follow [test selection](developers/testing/choose-tests.md),
[scenario launching](developers/testing/scenario-launcher.md) and
[throughput measurement](developers/testing/measure-throughput.md).

Do not mark a chunk complete because code compiles, a schema generates or a native
receipt succeeds. Complete it only when its stated behavioral gate is met. If
licensed inputs, installed models or platform coverage are unavailable, land only
the independently accepted gated subchunk and keep the blocked acceptance open.

## N01 — Unified RimGovernor native mod

Consolidate the native runtime and tool integrations into one
installable native mod, with typed contracts and clear runtime ownership. G01 owns
canonical schema generation and the controller rewrite; N01 owns C# implementation,
packaging and native acceptance.

### Active development scope

Game state is disposable. Do not build legacy save importers, preserve old CLR or
assembly identities for saves, require reverse compatibility, or run exhaustive
legacy parity campaigns. Fresh game and controller state are valid migration inputs.
Historical inventories and captures are reference material, not release gates.
Keep required third-party source/license notices; local development does not settle
unresolved redistribution rights.

This does not waive current-session correctness. Ordinary game rules, one automated
writer, stale-work invalidation, uncertain-write reconciliation, bounded shutdown
and native outcome checks still apply. Do not remove a live resource/safety guard
until its replacement protects already-admitted work.

### Target ownership

Ship `integrations/rimgovernor-native/` as `Mods/RimGovernor`, package ID
`davidarcher.rimgovernor.native`, with one About manifest and build entry point.
Keep Harmony, RimBridgeServer and GABS as dependencies. Use an early-loaded
`RimGovernor.Runtime` assembly for native state/bootstrap and a separately discovered
`RimGovernor.Bridge` tool assembly. Source folders are `src/Runtime/Persistence`,
`src/Runtime/Headless` and `src/Bridge`; split further by actual responsibilities as
families migrate. Keep bootstrap batch-gated so normal startup remains renderable.

SQLite owns goals, plans, action history, ownership intent and recovery decisions.
RimWorld owns simulation objects. Move controller bookkeeping out of saved native
components in bounded families when the consumer is wired; start fresh rather than
import old records. Retain only native guards, cleanup obligations and bounded
transition evidence needed for correct running jobs and disconnect behavior. A new
load invalidates pending authority and requires fresh admission.

Consume G01.02 schemas and generated DTOs rather than creating a second schema tree.
Use concrete request/result and operation types, explicit missing/null/error states,
int32 bounds, distinct identities and validated variants. Keep JSON/reflection at
named SDK boundaries; no anonymous public payloads, dynamic domain state or blanket
typing suppressions in migrated code. Use net472 and actual Unity/Mono-compatible
compiler/runtime features. Ratchet nullable analysis and warnings as errors as each
surface migrates. Mutations execute through a main-thread owner that revalidates
context immediately before effects.

### Sequenced landing units

Each slice is committed after relevant checks, then rebased and fast-forwarded into
main. Native package and Go production cutover remain independent.

- [x] **N01.00 — Working source inventory.** Export/build declarations, saved fields,
  operation variants, static state and package consumers are inventoried under
  `contracts/native-*` and the shared domain inventory. Existing legacy captures
  remain available as diagnostic examples. Additional legacy save, standalone
  fixture and exhaustive parity captures are out of scope.

- [x] **N01.01 — One active native package.** Native source/build owners. Consolidate
  sources into the target root and two loader-appropriate assemblies. Remove
  excluded duplicate helpers, retain source notices, declare dependencies/load order
  and provide one build/staging command with explicit compiler/game/SDK inputs and
  production/fixture output identification. Accept fresh-game startup and production
  discovery in batch and graphical modes, no fixture leakage or duplicate runtime
  components, and batch-only presentation suppression. No old-save acceptance gate.
  Companion redistribution permission remains a public release concern, not a gate
  on local source consolidation. Production and fixture builds pass; the same
  production artifact passes fresh Linux batch and graphical/Xvfb startup, all
  55 native tools, fixture isolation, component census and preview invariance.

- [ ] **N01.02 — First strict native contract.** Native contract owner with G01.02.
  Migrate placement previews through generated request/response DTOs, validated SDK
  boundary and typed operation. Reject unknown outer arguments before binder loss;
  fail closed when required raw validation is unavailable. Test missing/null,
  overflow, malformed grammar, variants and SDK failures using shared generated
  cases and fresh native invocation. Verify ordinary placement rules and no preview
  side effects. Breaking contract changes are allowed when current consumers move
  together; no historical byte-parity gate.

- [ ] **N01.03 — Typed observations.** Native observations owner with G01.03. Migrate
  identity/status and batch envelope, then pawns/health, supplies/buildings,
  rooms/zones/cells and remaining facts in bounded slices. Preserve unavailable
  information, section freshness and modded definitions. Check actual SDK decoding,
  invalid identities and read-only native behavior. Depends on 02.

- [ ] **N01.04 — Typed guarded operations.** Native operations owner with G01 Hands.
  Start with ordinary construction through admission, dry-run, receipt and observed
  pawn completion. Follow with settings/bills/zones, resources/upkeep, medical,
  animals/population, trade/world and explicit player operations. Keep editor/cheat
  tools outside automation. Accept refusal/dry-run without effects, stale commands,
  lost reply followed by observation and player override per migrated family.
  Depends on 02 and its required observations, not the entire Go port.

- [ ] **N01.05 — Runtime and presentation ownership.** Native runtime owner.
  Consolidate startup/patch health, supervisor/journal ownership, render leases,
  camera/input cleanup and platform adapters. Make initialization/shutdown bounded
  and idempotent. Required guard failure disables affected automation. Address the
  source-audited early missing-journal cache, delayed Watch/Trade cleanup, pending
  pawn images on destruction, render restoration after player edits and combat
  arms lacking game identity. Verify disconnect/lease expiry, load/map changes,
  repeated initialization, patch failure, batch pawn work and graphical rendering.
  Depends on relevant typed status/clock contracts; fix independently reproducible
  defects as bounded prerequisites.

- [ ] **N01.06 — Single persistence owner.** Native state owner with G01 store/Hands.
  Move controller metadata to SQLite by family and delete obsolete native writers,
  serialization and components. No legacy importers or old-format readers. Preserve
  minimal current-session guards/cleanup and sufficient transition evidence for
  split/merge/construction changes; lost evidence creates a hold, not completion.
  Verify fresh-state save/load and disconnected running jobs, invalidation on load,
  player override, duplicate/lost events and acknowledgement crash windows where
  applicable. Close when every retained native field has a concrete runtime need
  and the controller is the sole owner of its bookkeeping.

- [ ] **N01.07 — Use the unified package everywhere.** Integrator with launcher owner.
  Update setup/build scripts, private profiles, headless/container staging, artifact
  fingerprints and fixture/scenario launchers. Remove old source roots, duplicate
  builders and obsolete adapters. Reject mixed packages/stale artifacts. Never
  replace installed DLLs while a game runs. Accept fresh installation and isolated
  native runs on supported platforms; no copied-profile upgrade or rollback gate.
  Can land with 01 while typed families continue, using current supported consumers.
  Unified build/staging, source paths and current consumers are implemented and
  verified in Linux workers. Fresh installed Windows startup remains to verify.

- [ ] **N01.08 — Enforce the native standard.** Native integrator with DEV01. Expand
  compiler/nullability/boundary checks across migrated production code; record
  temporary exclusions here. Check generation drift, registration coverage and
  representative invalid payload/result variants. Remove unused helpers and
  suppressions after caller checks. Accept reproducible builds and the native
  behavior matrix covering the supported package and typed families. Depends on
  03–07; enforcement starts with 02.

### Verification

Use [test selection](developers/testing/choose-tests.md): focused checks during
iteration, full affected suites once before handoff. Shared packaging/contracts
also require dashboard checks. Native runs use `scripts/container_scenario.py`,
private inputs and fresh task-specific outputs/images. Supervised tick waits use
`rimgovernor.native_scenario.advance_game`; interruption tests use
`expected_letters=()`. Retain failures and concise build/input/report evidence under
`.rimgovernor/`. Compilation and receipts do not establish pawn work. Check current
behavior with disposable new games; do not recreate dropped compatibility gates.

## Development tooling

- [ ] **DEV01 · Enforce the development standard incrementally.** Follow the
  [development process](developers/development-process.md). Audit existing enforcement
  before adding checks. G01.01c owns Go formatting/vet/test/race/platform gates and
  G01.02 owns schema drift; keep those tasks there. Add scoped strict Python checking
  for retained tooling and changed typed boundaries, explicit TypeScript escape
  checks and compatible C# boundary/null checks. Start with bounded clean surfaces;
  record excluded legacy paths and expand coverage without blanket suppressions.
  Audit dependency locking and documented configuration precedence against the
  Twelve-Factor guidance. Accept reproducible checks that reject representative
  typing/contract regressions and docs that distinguish enforced rules from policy.

## Completion rule

For each item record the observed failure, focused fix, source revision, checks,
native outcome and remaining limitation in its commit. Protocol tests, scripted
native acceptance and real-model gameplay are separate evidence levels. Preserve
failed trials locally. Do not reintroduce duplicate transports, manager hierarchies,
CLI wrapper services, external consultants, fixed reviewer daemons, turn clocks,
instant equipment cheats or hardcoded game facts as unfinished reuse work.
