# RimGovernor backlog

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
  [native upkeep audit](reference/upkeep-contracts.md) with safe enclosure/escape
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
  ordinary roof-collapse rules. See [spatial contracts](reference/spatial-contracts.md)
  and [mining contracts](reference/mining-contracts.md) for current boundaries.

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
  [controller profiler](how-to/measure-throughput.md) to measure the complete path
  from enabling automation to the first supervised tick. Separate repeated
  validation, persistence, controller scheduling and bridge time; optimize the
  measured costs without weakening fresh write checks. Acceptance must actually
  reach a supervised execution window; a sample that ends while issuing paused
  setup orders does not close this item.

- [ ] **Bridge request overhead.** Use the boundary timings in
  [throughput measurements](how-to/measure-throughput.md) to split remaining MCP
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

### Contracts and package boundaries

Proposed paths become real only when their owning chunk lands:

| Path | Ownership and restrictions |
| --- | --- |
| `contracts/` | Canonical versioned wire schemas, compatibility fixtures and generation manifest; no game assemblies. |
| `go/` | One Go module with pinned toolchain/dependencies and `cmd/rimgovernor`; avoid a module per subsystem. |
| `go/internal/wire/` | Generated wire types and explicit boundary decoding/validation; generated files carry provenance. |
| `go/internal/domain/` | Owned IDs, observations, action variants, plan specification/progress, receipts and failures; no transport or database dependencies. |
| `go/internal/bridge/` | MCP discovery, capability/version negotiation, bounded reads, native call receipts and transport diagnostics. |
| `go/internal/store/` | SQLite transactions, archives, inbox/outbox durability and checkpoint compatibility. |
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

Schemas must specify required fields, bounds, discriminators, enum encoding,
unknown-field policy and compatibility versions. Generated structs alone do not
enforce these rules. Preserve exact persisted signatures and action identities:
default omission, property ordering, Unicode, numeric encoding and null behavior
need cross-language fixtures wherever they affect hashes. Do not rehash old actions
as new work. C# DTOs must build against the mod's `net472` target without introducing
modern runtime dependencies into RimWorld. Serializer settings must be explicit.

The existing `bridge_observation.schema.json` describes a Python projection, not
the entire native wire surface. Inventory actual tool schemas and native replies
before generalizing it. Repository-owned native contracts can be generated;
third-party/modded tools still require runtime discovery, capability checks and
validation. Unknown mutation semantics must remain unavailable to automation.

### Safety and compatibility gates

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
5. Preserve transaction boundaries, archives, request deduplication, event inbox
   cursors, paired saves and native journal identity. Unknown/missing recovery
   evidence fails closed. Never let Python and Go open the same live writable store.
6. Preserve native discovery, normal pawn work, fresh placement/resource checks,
   bounded execution windows, durable recording and UI drafts/last-good data.

Use the existing [controller](reference/controller-contracts.md),
[action](reference/action-contracts.md), [recovery](reference/recovery-contracts.md),
[persistence](reference/persistence-contracts.md),
[session](reference/session-contracts.md) and
[interface](reference/interface-contracts.md) contracts as behavioral requirements.
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
      record; depends on 00d.1–3. Matched uncontended outcome timings remain required
      for 12; the representative Python sample retains partial-construction holds
      and a warning pause. Do not stop another task's native scenario.

  Extend the initial synthetic fixtures to native captures, complete paired
  checkpoints, nonzero archive epochs and the boundary/overflow decoding matrix
  with the corresponding Go consumer chunks. Inventory checks establish source
  coverage, not Go or native behavioral parity.

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

- [ ] **G01.02 — Schema generation and first native contract.** Owner: contracts
  agent; shared-file changes coordinated by integrator. Land generation tooling
  first, then migrate one bounded request/response such as placement previews.
  Generate C# and Go models plus applicable dashboard types. Retain the existing
  Python consumer through compatible JSON; generate Python models during transition
  where this avoids a second schema source. Add drift checks to CI. Verify required,
  null, unknown, overflow, enum and invalid-variant handling, plus native refusal
  replies. Accept round trips across C#/Go/Python and an isolated native invocation
  showing unchanged effects and errors. Expand contracts incrementally with their
  consumer chunks rather than generating the entire API speculatively. Depends on 01.

- [ ] **G01.03 — Read-only transport and observation.** Owner: bridge agent.
  Port `bridge.py`, `bridge_game.py`, `bridge_observation.py` and relevant native
  contract/recording adapters. Preserve MCP process ownership, discovery, capability
  gating, batched and legacy reads, observation freshness, identity checks and
  separate queue/session/native timings. Define explicit read versus mutation APIs;
  read-only mode must reject write-capable tools even if requested by name. Accept
  fake-server failures/reconnects and native typed-fact parity for batched/legacy
  observations, with zero writes and no game-clock changes. Depends on 02.

- [ ] **G01.04 — Typed plan and durable store.** Owner: state agent. Split into
  04a plan/action/progress types, then 04b SQLite and recovery compatibility. Port
  `colony_plan.py`, `store.py`, relevant `session_checkpoint.py` and archive contracts.
  Preserve transaction/savepoint semantics, after-commit compaction, exact signatures,
  inbox/outbox, chat request deduplication, method epochs and archive lookups. Read
  copied existing databases without touching originals. Establish explicit storage
  format/version checks and test whether Python can read Go-written state; do not
  assume reverse compatibility. Accept crash/fault injection before and after each
  transaction/dispatch boundary, retained unknown writes and paired restart without
  duplicate work. Verify Windows connection closure and SQLite integrity. Depends on 02.

- [ ] **G01.05 — Deterministic planning kernel.** Owner: policy agent.
  Port plan readiness/dependencies, resource accounting, priority admission,
  hysteresis, geometry and method interfaces from `colony_policy.py`,
  `resource_accounting.py`, `spatial.py`, `room_geometry.py` and related modules.
  Build explicit typed inputs instead of passing the runtime object. Inject stable
  clocks/IDs; sort map-derived decisions and use deterministic tie breaking. Accept
  replay parity for deficits, unknown facts, competing projects, player priorities,
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
  benchmark against the same semantic cases. Assert zero inference for routine
  events. Core depends on 04 and 06; each command family waits for its 07 handler.

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

- [ ] **G01.10 — Whole-runtime integration and passive comparison.** Owner:
  integration agent. Account for every manifest row and compare complete controller
  decisions, holds, receipts and persistence against fixed recordings. Passive Go
  comparison consumes recorded/copied observations only; it cannot acquire native
  control, pause/advance time or write the Python database. Separately run Go as
  the only controller in disposable scenarios. Exercise all supported command and
  completion families, pending-action restarts, lost acknowledgments, shutdown,
  viewer interruption and multiple maps. Accept no unexplained semantic diffs;
  intentional bug fixes need their own contract-based tests and review. Depends
  on all 07 families, 08 and 09.

- [ ] **G01.11 — Packaging, checkpoint transfer and rollback rehearsal.** Owner:
  integration agent, with bridge/store owners. Update Windows launch/setup/build,
  Docker images/workers, scenario adapters and observation discovery to explicitly
  select Python or Go, with Python still default. Preserve private profiles,
  automatic loopback dashboard ports, licensed-input isolation, storage durability
  and evidence export. Some scenario scripts instantiate Python runtime classes;
  port those assertions or add a documented test adapter that drives the Go
  process, not a hidden Python controller. Rehearse stop/pause, lease release,
  database/native-save backup, offline migration, Go startup in Manual and fresh
  identity verification before automation. Refuse simultaneous controller startup.
  If reverse storage compatibility is not proven, rollback restores the paired
  pre-cutover database AND native save; never combine an old DB with newer game
  progress. Explain that this rollback loses post-cutover progress. Accept clean
  install, restart, migration failure and rollback on Windows and Linux. Depends on 10.

- [ ] **G01.12 — Acceptance and default switch.** Owner: integration agent.
  Run full Go tests/vet/race checks where supported, contract generation checks,
  the full affected Python compatibility suite and dashboard checks on the final
  integrated revision. Run isolated native startup, interruption/recovery,
  paired-save, player controls and all covered domain scenarios, plus the existing
  bounded sustained-colony matrix. Record source/image/input hashes and retain
  failures; existing B-series gaps stay explicit. Compare uncontended, unprofiled
  Python/Go runs with identical game/model/storage/render settings, measuring first
  supervised tick, completed pawn work/scenario wall time, bridge calls, CPU/RSS,
  recording cost and interruption latency. Agree numeric regression thresholds
  from the 00 baseline before examining Go results; do not substitute microbenchmarks
  or weaken durability to pass. Switch Go to default only when parity, rollback and
  operability gates pass and performance regressions meet the recorded budget.
  Faster gameplay is not required if the maintainability case holds, but any
  regression must be explicit and accepted. Depends on 11.

- [ ] **G01.13 — Retire production Python.** Owner: integration agent.
  After default-switch acceptance and the documented rollback support window,
  remove Python production entry points, runtime-only dependencies and duplicate
  implementations. Preserve accepted fixtures, attribution and explicitly retained
  Python scenario/development tools. Update setup, architecture, source map,
  troubleshooting and CI to describe Go as the actual runtime. Accept a production
  image/install with no Python interpreter required, all manifest rows resolved,
  and no launcher or dashboard route silently using Python. Keep only the migration
  support needed by the stated checkpoint compatibility policy. Depends on 12.

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
helpers. Follow [test selection](how-to/choose-tests.md),
[scenario launching](how-to/scenario-launcher.md) and
[throughput measurement](how-to/measure-throughput.md).

Do not mark a chunk complete because code compiles, a schema generates or a native
receipt succeeds. Complete it only when its stated behavioral gate is met. If
licensed inputs, installed models or platform coverage are unavailable, land only
the independently accepted gated subchunk and keep the blocked acceptance open.

## N01 — Unified RimGovernor native mod

This is the implementation plan for combining `integrations/headless-rim` and
`integrations/colony-bridge` into one installable **RimGovernor** native mod and
bringing its C# code under the [development process](how-to/development-process.md).
The source-baseline slices below are landed; native compatibility acceptance and
package implementation remain open. G01 owns the Go rewrite and canonical schema
generation; N01 owns native packaging, implementation quality and game acceptance.
The current deployment remains supported until the replacement passes its gates.

### Outcome and scope

Ship one repository-owned package at `integrations/rimgovernor-native/`, installed
as `Mods/RimGovernor`, with proposed package ID `davidarcher.rimgovernor.native`.
It provides colony observations, guarded operations, persisted native identity,
supervised execution, player/render controls and optional headless presentation.
Keep Harmony, RimBridgeServer and GABS as dependencies; this does not absorb their
implementations or replace transport. Keep policy, plans and automated Hands in
the controller, ordinary rules and simulation in RimWorld, and native eligibility
and execution guards in this mod. No new gameplay capabilities are part of the merge.

One mod means one About manifest, release and installation path, not necessarily
one DLL. Preserve the early-loaded `RimGovernor.ColonyIdentity` assembly and its
saved type names initially. The current bridge extension loads separately under
`BridgeTools/Observations`; moving saved components there would break Verse's early
type discovery. Place bootstrap/headless code in an early-loaded assembly and
retain the SDK-discovered tool assembly until loader acceptance justifies changing
it. These retained types are migration compatibility, not a commitment to keep
controller bookkeeping in native saves. Remove obsolete components after their
state ownership and migration gates pass; cosmetic renames must not force an
unverified save migration.

### State ownership: SQLite first

The controller's SQLite store is the sole authority for goals, plans, action
history, completion tracking, ownership intent and recovery decisions. RimWorld
owns actual pawns, buildings, bills, designations and other simulation state.
Do not maintain a second authoritative copy of controller bookkeeping in C#
GameComponents or MapComponents. Native observations are evidence for reconciling
SQLite records, not a competing plan/progress database. The mod does not open the
controller database directly; the controller remains its single writer.

N01.00 must classify every existing saved field, including construction/haul
lineage, mining/drilling records, wall replacement, production limits, equipment
claims, recovery areas and Home exclusions. For each record specify its sole
owner, whether native game state can reconstruct it, its disconnected behavior,
and its save/load migration. Default controller metadata to SQLite. A field may
remain in the native save only with a concrete need, a minimal representation,
and an acceptance case demonstrating why reconstruction or controller storage is
insufficient. Existing placement in the identity assembly is not justification.

| State | Target persistence and recovery |
| --- | --- |
| Controller intent, progress and ownership history | SQLite; reconcile against fresh native evidence before restoring any authority. |
| Actual game objects and settings | Ordinary RimWorld saves; do not shadow them as authoritative controller state. |
| Native execution guards | Prefer per-load, leased configuration installed by the controller. Define safe disconnect/load behavior per guard; never silently remove a resource limit while already-admitted pawn work can still consume resources. Retain minimal native state only where the safety contract requires it. |
| Blueprint/frame/building and item split/merge transitions | Typed native events consumed durably into SQLite. Prefer reconstructible evidence; retain only a bounded delivery journal if missed transitions cannot be recovered safely. This journal is delivery evidence, not a second action ledger. |
| Colony and save timeline identity | Minimal save marker sufficient to associate a loaded snapshot with controller history; a colony ID, tick count or transient load token alone cannot distinguish branched saves. Define snapshot/branch association and ordinary save/load behavior with G01.04/G01.08. |

Event contracts must specify stable IDs, colony/save lineage, ordering, duplicate
delivery, durable acknowledgement, replay and retention. Acknowledge only after
the SQLite transaction commits. Define crash windows, disconnected operation,
journal overflow and missing ranges; lost evidence must create an explicit hold
or safe reconciliation, never fabricated completion or blind retry. Do not assume
an in-memory queue or polling preserves every identity transition.

Loading an older or branched game save must not apply newer SQLite progress or
ownership claims. Paired checkpoints are the preferred exact recovery path, but
ordinary player saves, autosaves and loads must also be detected. Reconnect in
Manual, invalidate pending authority, match the saved timeline and reconcile or
require an explicit recovery choice when association is ambiguous. Preserve newer
history separately rather than overwrite it. A save without controller history
must remain playable; automation requires fresh admission and must not reclaim
old ownership. Native state removal must preserve these guarantees, not merely
reduce the number of serialized fields.

### Current pressure points and intended boundaries

The inspected baseline is `fcdbea1a`; refresh affected sources and G01 status before
implementation. These examples identify migration seams, not a completed audit.

| Current surface | Intended owner and change |
| --- | --- |
| `headless-rim/src/Startup.cs`, `HeadlessModeManager.cs`, `HeadlessPatches.cs` | Bootstrap owns validated startup mode and ordered, idempotent Harmony installation. Preserve early icon/atlas patches, late presentation patches and Linux-safe initialization timing. Missing required targets must make the mode unavailable rather than report successful startup. |
| `colony-bridge/src/RenderDemandTool.cs`, video, portraits and player input | Presentation owns render leases, camera restoration and platform adapters. Batch headless cannot become renderable through a lease; rendered Linux/Xvfb and Windows remain distinct supported modes. |
| `BridgeCommon.cs` | The SDK adapter owns raw argument recovery and SDK reflection. Its journal lookup exists because the binder drops unknown keys; generated DTOs alone cannot fix that loss. Validate before information is discarded, or refuse guarded writes when raw validation is unavailable. |
| `ObservationBatchTool.cs` and observation tools | Typed observation services return concrete section DTOs and explicit unavailable evidence. Preserve sequential section reads, before/after ticks, timings and identity checks; do not relabel the batch an atomic snapshot. |
| Gameplay `*Tool.cs` and guard helpers | Thin SDK entry points call typed capability operations. Keep resolution, eligibility, dry-run and actual native mutation together within the correct main-thread admission boundary. |
| `src/identity/`, clock journal, draft and policy ownership | Move controller bookkeeping to SQLite; justify minimal save metadata and delivery journals field by field. Keep transient authority per load and preserve recovery evidence through explicit migration. |
| Two build scripts, profile staging and artifact checks | One native build/release manifest owns output paths, versions, dependency/input hashes and production versus fixture artifacts. |

Use coherent folders for bootstrap, generated contracts, SDK/platform adapters,
observations, capability operations, persistence, supervision and presentation.
These are responsibility boundaries, not a requirement for a framework or an
assembly per folder. Extract only code with a real caller and a clear dependency
direction. Tool adapters depend on typed operations; operations do not call other
exported tools or pass the whole bridge context around as a service locator.

### Native typing and wire agreement

- Consume the versioned schemas and pinned generation manifest from **G01.02**.
  Do not invent an independent C# schema tree or freeze today's anonymous JSON as
  the permanent API. Keep existing `home/*` names and compatible wire behavior
  during packaging; coordinate any breaking change with the G01 contract owner.
- Generate concrete C# request/response DTOs, then validate and convert to owned
  operation types. Use distinct colony, map, load, action and direction IDs,
  integer ticks, explicit operation/result variants and validated bounds. Discover
  modded definition names from the game; they are not closed compile-time enums.
- Specify missing versus null versus known zero/false, discriminator and enum
  encoding, numeric overflow, collection limits and unknown-field policy per
  schema. Unsupported mutations and failed authority validation fail closed.
  Refusal, unavailable evidence, accepted order and observed completion are
  distinct results. Preserve uncertain dispatch in the controller on lost replies.
- Configure the actual SDK/JSON serializer explicitly: field names, defaults,
  nulls, numbers and enum encoding. Test the serialized SDK response, not only a
  DTO round trip. Preserve exact legacy signature bytes where hashes/identities
  depend on them; never rehash a saved action into new work.
- No `dynamic`, dictionary-shaped domain state, anonymous public payloads or
  unchecked casts in migrated code. If the SDK requires `Task<object>`, box a
  concrete DTO only at that adapter. Restrict `JObject`, `JToken`, object maps and
  reflection to validated transport, retained evidence or named compatibility
  adapters with tests and reasons. Do not ban Harmony's necessary game reflection
  by disguising it in generic helpers.
- Keep `net472` and installed Unity/Mono compatibility. Pin compiler/dependency
  inputs; enable nullable analysis on migrated surfaces using a compatible
  compiler, warnings as errors and scoped analyzer rules. Do not add unsupported
  runtime libraries, blanket suppressions or `!` to conceal missing facts. Verify
  generated code and annotations on the actual native runtime.
- One main-thread owner admits native mutations. Capture identities before
  queuing, revalidate inside the callback immediately before effects, and reject
  stale work after Manual, direction, map/load or ownership changes. Cancellation
  after dispatch cannot undo an order. Bound queues and shutdown; explicitly
  restore only owned camera, speed and draft state, never later player changes.

### Sequenced landing units

Split a row further if it exceeds a small verified slice. Each subchunk names its
owner, affected paths, dependencies, concrete acceptance and remaining coverage.
Commit accepted increments without enabling incomplete behavior by default.

- [ ] **N01.00 — Native inventory and compatibility baseline.** Owner: native
  integrator. Inventory all exported tools and variants, patch targets, static
  state, saved types/keys, reflection escapes, excluded duplicate helpers, build
  flags, dependencies and deployment consumers. Extend the existing `contracts/`
  inventories with the G01 owner rather than duplicate their call-site lists;
  include native tools not called by Python. Map each to schema owner, fixtures,
  native scenarios and migration status. Capture actual discovery/replies and
  copied save/reload fixtures with provenance. Audit upstream GPL-3.0 headless
  source and companion license obligations against `THIRD_PARTY.md`; retain
  notices, pinned revisions and corresponding source in the unified distribution.
  Accept when every entry has an owner and explicit uncovered acceptance; identify
  source files excluded from compilation before deleting apparent duplicates.
  Deliver the field-level state ownership audit above with G01.04/G01.08, including
  removal targets, justified native exceptions and disconnected/save-branch tests.
  Dependencies: none; refresh against G01.00 and intervening gameplay fixes.

  Landed baseline slices (source findings do not close native acceptance):

  - [x] **N01.00a — Export and build-source baseline.** Native surface owner:
    [shared domain inventory](../contracts/domain-inventory.json) now records
    108 exports (55 production, 53 fixture), five projects, exact advertised
    declarations/defaults and three compile exclusions. Existing G01 call-site
    rows remain unchanged. `scripts/check_native_inventory.py` checks source
    drift and rejects omitted exports, altered signatures and omitted exclusions;
    the shared coverage command runs it.
  - [x] **N01.00b — Saved-field source audit.** Native state owner:
    [state ownership inventory](../contracts/native-state-ownership.md) maps
    all 88 serialized field/key pairs across nine components and seven records
    to proposed sole owners, reconstruction limits and migration/disconnect
    requirements. Native exceptions are proposals awaiting G01 agreement and
    acceptance, not authorization to remove existing protections.
  - [x] **N01.00c — Runtime/package source audit.** Native packaging owner:
    [runtime inventory](../contracts/native-runtime-packaging.md) and its
    source index cover 117 native/fixture files, patch targets, lexical
    static/reflection anchors, dependencies, fixture flags, deployment consumers
    and repository provenance gaps.
  - [x] **N01.00d — Retained reply comparison seed.** Native integrator:
    [three actual SDK pairs](../contracts/fixtures/native-replies-baseline.json)
    preserve identity, placement preview and spatial refusal from the retained
    Linux baseline with line/file and input hashes. Exact text/structured payload
    parity was checked; this is historical evidence, not fresh discovery.
  - [ ] **N01.00e — Compatibility baseline closeout.** Native integrator with
    G01.02/04/08: capture fresh complete production/fixture SDK discovery, including
    external RimBridgeServer exports, duplicate registration and binder behavior;
    enumerate implemented variants beyond advertised signatures and audit mutable
    static fields beyond lexical anchors. Retain copied saves populated with each
    native state family and normal/batch fresh-reload evidence. Agree field-level
    exceptions, timeline and delivery contracts with G01 owners; resolve companion
    source/distribution permission and verify headless corresponding-source/notices
    packaging. Refresh source inventories for intervening native changes. Close
    N01.00 only when these gaps have concrete evidence and per-entry ownership;
    N01.01 package migration remains gated by this closeout.

- [ ] **N01.01 — Unified package without behavioral refactoring.** Owner: native
  integrator. Move sources/provenance under the target root, preserve loader paths
  and saved identities, and provide one reproducible build/package entry point.
  Move headless initialization into the package with the existing batch-only gate;
  normal interactive startup must not install presentation suppression. Declare
  dependencies/load order explicitly. Reject simultaneous old/new packages before
  duplicate component or patch registration. Build into fresh staging output;
  fixture flags must produce separately identified non-production artifacts.
  Accept one discovery registration per tool, one component instance per saved
  owner, normal and batch startup/load, and production discovery with no fixture
  tools. Keep default launchers on the old package until N01.07. Depends on 00.

- [ ] **N01.02 — First strict C# vertical contract.** Owner: native contract
  implementer, coordinated with G01.02. Use G01's selected first surface (placement
  previews if that remains its choice): schema -> generated DTO -> validated
  request -> typed operation -> real SDK response -> Python/Go decoding. Solve
  raw unknown-argument validation at the SDK boundary and test binder behavior.
  Add generation drift, serializer and invalid/missing/null/overflow/variant tests,
  including SDK adapter failure. Accept cross-language fixtures and an isolated
  native invocation with unchanged preview/refusal behavior. Packaging is not a
  dependency: this may land in the old path and move with 01. A bounded read-only
  preview increment depends on 00's applicable source inventory and live preview
  discovery/binder fixtures, plus G01.02's generation foundation. Unrelated saved
  state and distribution closeout may remain open in 00e; this does not close 00e
  or authorize package cutover. Contributes its native acceptance to G01.02.

- [ ] **N01.03 — Typed observation families.** Owner: native observations
  implementer; coordinate with G01.03. Migrate identity/status and the batch
  envelope first, then pawns/health, supplies/buildings, rooms/zones/cells and
  remaining facts in separately tracked slices. Replace anonymous objects/maps
  with typed results behind existing entry points. Keep partial unavailability,
  section freshness and modded definitions visible. Accept producer/consumer
  fixtures, batched/legacy parity, invalid identities and read-only native probes
  with no clock, selection or game-state changes. Depends on 02; requires 01 only
  when accepting the unified artifact.

- [ ] **N01.04 — Typed guarded mutation families.** Owner: native operations
  implementer; coordinate with G01.06/G01.07 Hands slices. Start with one ordinary
  construction order through admission, dry-run, receipt and completed pawn work.
  Follow with separately inventoried settings/bills/zones, resource and upkeep,
  medical/animal/population, trade/world and explicit player operations. Use typed
  variants and registration-coverage checks, shared validated resolvers and narrow
  guard inputs. Keep editor/cheat capabilities outside production automation.
  Accept refusal without effects, dry-run without effects, stale queued commands,
  lost reply followed by observation, player override and actual native outcomes
  for each family. Depends on 02 and the required 03 observations; do not wait for
  the entire Go port when the compatible Python consumer can exercise the slice.

- [ ] **N01.05 — Lifecycle, patch and presentation ownership.** Owner: native
  runtime implementer. Consolidate startup configuration, patch registration,
  supervisor/journal ownership and render/input lifecycle. Track required versus
  optional patch failures in typed capability health; loss of a required guard
  disables affected automation and safely ends supervised execution. Make startup
  and cleanup idempotent, bound background resources and isolate Windows APIs.
  Accept lease expiry, lost controller, forced pause, map/load change, repeated
  game reuse, shutdown and injected patch/journal failures. Verify native long
  events/autosaves and normal pawn work in batch mode, visible-window rendering,
  Linux/Xvfb frames/input, and camera restoration without simulation changes.
  Depends on 01–02 and relevant typed status/clock contracts.

- [ ] **N01.06 — Consolidate persistence and verify save recovery.** Owner: native
  state implementer with G01.04/G01.08 store/recovery owners. Implement the 00
  field-level decisions in bounded families: first timeline association and event
  delivery, then migrate controller records to SQLite and remove obsolete native
  writers/components. Define precedence for conflicting legacy records; do not
  merge contradictory progress into success. Make legacy imports versioned and
  idempotent, retaining provenance and recovery evidence. Keep compatibility
  readers/types only as long as supported old saves require them.
  Accept old-package save -> unified load -> save -> fresh reload with stable
  colony identity, rotated load token, preserved evidence and no duplicate orders.
  Verify normal saves/autosaves, older and branched saves against newer SQLite,
  missing databases, disconnected pawn work, player overrides, duplicate/lost
  events, queue overflow and crashes before/after durable event acknowledgement.
  Verify each retained native guard's safe disconnect/load behavior and require
  fresh authority before recovery writes. Test paired Python and available Go
  checkpoint recovery; a consumer lacking the new persistence contract remains
  on compatible artifacts until its migration lands. Test reverse compatibility
  rather than assume it; rollback uses the retained old package plus pre-migration
  save and controller checkpoint when new state is unreadable. Depends on 01,
  relevant 02 contracts and the owning consumer/store slices; complete before
  changing defaults. Close only when every saved field has one authoritative
  owner and each remaining native persistence exception has acceptance evidence.

- [ ] **N01.07 — Deployment cutover and legacy removal.** Owner: integrator,
  coordinated with G01 launcher/release owner. Update setup/build scripts, private
  profiles, `headless.py`, `container_worker.py`, `campaign_manifest.py`, input
  caches, Docker builds, scenario DLL checks and fixture launchers from the 00
  inventory. Stage a complete versioned package and validate hashes/dependencies
  before activating it; never replace DLLs while any RimWorld instance runs.
  Reject mixed generations and stale cached artifacts with actionable errors.
  Accept fresh install, copied-profile upgrade, rollback and isolated peer survival
  on Windows and Linux. Verify the default Python consumer and the current gated
  Go consumer with negotiated supported contract versions. Remove old directories,
  duplicate builders and transition adapters only after their inventory rows are
  closed. Update project identity, source map, setup and provenance docs. Depends
  on 01–06 and compatible consumers; does not authorize Go production cutover.

- [ ] **N01.08 — Enforce and close the native standard.** Owner: native integrator
  with DEV01. Ratchet compiler/nullability/analyzer and boundary-escape checks over
  the migrated production surface, keeping explicit temporary exclusions in this
  backlog. Test representative forbidden payloads and invalid result variants so
  enforcement is demonstrable. Close all tool, patch, persistence and packaging
  inventory rows; retain documented third-party adapter exceptions with boundary
  tests. Remove dead helpers and obsolete suppressions only after caller/fixture
  verification. Accept a clean reproducible release, contract drift checks and
  the full affected native acceptance matrix. Depends on 03–07; enforcement starts
  in 02 rather than waiting for this final audit.

### Integration and release evidence

G01's integrator owns canonical schema/generator changes; the native integrator
owns native implementation and packaging. Agree each schema version and consumer
readiness before changing public contracts. N01.00/01 can proceed while Go contracts
land; N01.02 joins G01.02 rather than waiting for G01.02 to finish the same native
work. Later families follow consumer readiness. Refresh inventories and captured
fixtures when concurrent gameplay fixes land; never overwrite them with an older
copy during the move. Keep native package and Go controller cutovers independent.

Follow [test selection](how-to/choose-tests.md): focused native/contract checks per
slice, full affected suites once before handoff; shared contract or packaging
changes also require controller and dashboard suites. Add fast C# validator and
state tests without licensed inputs where possible, then test real SDK wiring and
packaged assemblies against licensed game inputs. Compilation and serialization
are not pawn-work acceptance. Performance claims require isolated measurements
with matching inputs/render modes and budgets agreed before results are examined.

Native scenarios use `scripts/container_scenario.py`, its dashboard helpers and
`rimgovernor.native_scenario.advance_game`; interruption acceptance passes
`expected_letters=()`. Retain fresh task-tagged images, reports and failures under
`.rimgovernor/`. Report exact source/artifact/schema versions, commands, exit codes,
skips and platform coverage. Unavailable native evidence leaves its checkbox open.
B-series sustained-survival gaps are not closed by packaging or refactoring.

## Development tooling

- [ ] **DEV01 · Enforce the development standard incrementally.** Follow the
  [development process](how-to/development-process.md). Audit existing enforcement
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
