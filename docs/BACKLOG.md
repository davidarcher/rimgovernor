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

- [ ] **Reactive native control and maximum-speed play.** Let players watch the AI
  play at superhuman simulation speeds without avoidable polling, fixed-window
  waits or controller idle time. Relevant native changes must stop time when a
  decision is needed, wake the controller immediately, and resume ordinary pawn
  work as soon as the decision is ready. Separately support uncapped headless
  execution ("ULTRAULTRA fast") so long-run playtests run as quickly as hardware
  and the simulation permit. Preserve normal game rules and simulation ticks;
  speed must not come from skipped work, fabricated outcomes or weaker checks.

  **Ownership and baseline.** Implement through the shared Go goal/Hands executor,
  native operation evidence and existing clock/session owner; coordinate G01/N01
  contracts without adding a second planner, transport or production Python
  subsystem. Python already wakes on direction and controller-task completion,
  but native observations and journal delivery remain polled. Measure current
  Python and gated Go paths separately: unchanged reads, bytes, native scan time,
  bridge round trips, lock waits, persistence, setup-to-first-tick latency and
  completion-to-next-action latency. Reuse the
  [throughput profiler](developers/testing/measure-throughput.md) and adjacent
  setup/bridge workstreams; quantify savings rather than inferring them from timers.

  - [ ] **Typed event delivery and recovery.** Reuse RimBridgeServer's GABP event
    transport. Pinned GABS consumes attention subscriptions internally without
    forwarding general events to MCP; add forwarding and native publication.
    Extend the clock journal/cursor pattern to supported operation outcomes,
    authority/context changes, safety interruptions and relevant observation
    invalidations. Carry exact world/load/map, tick, sequence and action/attempt
    identities where applicable. Subscribe with a snapshot/cursor handoff that
    cannot lose changes between initial observation and live delivery. Persist
    consumer progress consistently with applied evidence; handle reconnect,
    replay, duplicates, gaps, overflow and context replacement explicitly.
    Bound buffers and retention, coalesce routine changes, and keep transport or
    disk backpressure off the simulation thread. Lost evidence must cause a hold
    and reconciliation, never fabricated completion or an automatic uncertain write.

  - [ ] **Native action watches and clock stops.** Arm bounded typed watches before
    dispatch/clock advancement, including synchronous completion during dispatch.
    Use existing causal hooks for exact construction and pawn-job outcomes;
    accepted operations and vanished jobs are not completion. Latch terminal or
    decision-relevant changes in native code and pause at the first safe tick
    boundary before another simulation tick is admitted, including within an
    accelerated frame batch. Publish evidence after establishing the stop; network
    delivery and controller processing must not be on the pause-critical path.
    Support selected-attempt any/all conditions, cancellation/interruption,
    safety triggers and maximum game-tick deadlines under the existing clock
    authority. Record observed event and actual pause ticks, including unavoidable
    within-tick ordering. Detect stalls with tick-based progress deadlines and
    supported progress measures; distinguish no progress, known blockers and
    unknown causes. Do not pause for every irrelevant world change.

  - [ ] **Reactive reconciliation and scheduling.** Wake the existing executor on
    events and player direction, consume authoritative correlated evidence, and
    refresh only affected facts when an event supplies invalidation rather than
    a complete outcome. Replace routine unchanged action/status/full-colony reads
    and arbitrary execution-window waits as coverage lands. Retain initial and
    reconnect snapshots, targeted uncertainty/gap recovery, low-frequency
    consistency checks, lease renewal and atomic fresh native write guards.
    Plan/direction/load changes invalidate watches and queued decisions. Batch
    independent ready work and resume promptly after durable reconciliation;
    model inference remains limited to explicit semantic requests/advice.

  - [ ] **Player speed and uncapped headless execution.** Expose deliberate player
    speed selection with live observation, responsive Manual/pause and clear stop
    reasons. Decouple rendering/dashboard cadence from simulation and decision
    cadence so watching does not require a full colony scan per frame. Add an
    explicit isolated-test uncapped mode that removes artificial wall-clock/FPS
    pacing and fixed tick-window round trips while running ordinary ticks at
    maximum sustainable throughput. Keep finite scenario budgets, native safety
    stops, authority expiry and external player changes effective inside large
    tick batches. Audit frame/wall-clock-dependent guards and mod behavior; do
    not silently bypass forced slowdown or advertise unsupported acceleration.
    Restore owned speed/boost/render state on stop, failure and context changes.

  - [ ] **Verified slices and acceptance.** Start with one wall: subscribe before
    dispatch, observe normal construction lineage, pause natively on completion
    or exact cancellation, and wake/reconcile without progress polling. Add fast
    contract/replay tests for ordering, duplicate/gap delivery, snapshot races,
    lost replies, immediate outcomes, backpressure and invalidation. Then run
    targeted native headless and rendered acceptance for completion, interruption,
    stalls, Manual, disconnect/lease expiry and load/map replacement at high
    speed. Assert event-to-pause tick gaps and retained uncertainty; a fast receipt
    is not pawn completion. Expand watches by supported operation/observation
    family. Compare matched finite scenarios at normal, player-fast and uncapped
    headless speeds using actual pawn outcomes, survival invariants, game days per
    wall minute, end-to-end scenario duration, controller pause time, event/reaction
    latency and native calls/bytes. Require no missed relevant transitions or
    duplicate effects; do not require identical stochastic trajectories. Finish
    with explicitly scheduled bounded long-run campaigns demonstrating faster
    playtests and sustained player-visible play, with measured remaining polling
    and explicit limits for unsupported event families.

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

**Done means:** Go owns the production controller from startup through shutdown,
all existing controller responsibilities have a working Go replacement, and the
production install/image needs neither Python nor a Python sidecar. Delete the
replaced Python runtime, its entry points and runtime-only dependencies. Python
may remain only for explicitly identified development/scenario tools.

**Current starting point:** Go has transport, generated contracts, SQLite state,
shared action execution, building and temporary-draft player controls, melee
execution internals, local-model interpretation, dashboard observations and an
opt-in supervised clock with interruption acknowledgement. It is still a partial
controller; Python remains the production default. Reuse these implementations.

Work serially. Port existing behavior; do not combine this with new gameplay,
planner redesign, UI redesign or generalized hardening. State is disposable:
no old-save/database migration, compatibility layer or rollback rehearsal.
Keep normal game rules, one writer, Manual cancellation, uncertain-write recovery
and observed pawn outcomes. Follow [AGENTS.md](../AGENTS.md) and the
[test selection guide](developers/testing/choose-tests.md). Land verified commits
in main by fast-forward, without pushes; reuse checks when relevant code is unchanged.

### Remaining work

The list is ordered for delivery: complete routine planning and action families,
connect chat and player services, then switch and
remove Python. Existing G01 IDs remain useful for inventory references. Do not
expand these into another nested task tree; remove a row when its outcome is met.

- [ ] **G01.05 — Complete routine planning.** Port deficit detection, method
  selection, priorities, resource accounting, dependencies, hysteresis and spatial
  planning from the current Python controller. Connect decisions to the existing
  shared plans and Hands as their action families become available. Cover unknown
  facts, competing projects, player priorities, renewed deficits and cancellation.
  Routine events must make no model calls.
  Go pure policy now covers common foothold gates, food/temperature/wood latches,
  development ranking and bounded starter-site/fragmented-field proposals. General
  placement search now ports the Python candidate order and checks whole
  native footprints against indoor constraints and protected cells; selected actions
  use the existing method admission contract. Shared
  action progress retains committed capacity through uncertain cancellation.
  Maintained goal/method records now link to shared plans in fresh Go SQLite state.
  Reviews retain unknown effects, reopen recovered deficits and atomically cancel
  linked actions on cancellation or context invalidation. Persisted dependencies
  gate Hands on observed completion; building-method admission atomically reserves
  whole-project costs and footprints against competing shared plans. Routine reviews
  now persist explicit need assessments and latch history atomically with goal
  updates, including Manual/context invalidation and cancellation preservation. Typed
  native core/planning reads now feed Go projections; paused native parity covers
  counts, stock, sleeping/temperature/storage, definitions and selected cells.
  A paused Go read bracket requires matching ticks and known native generations
  and rejects expired/cancelled reads before publishing facts.
  The player-gated reviewer now commits native needs with authority rechecks;
  its same-tick emergency census supplies medical/combat needs through shared
  emergency rules, retaining unknowns for incomplete or conflicting evidence.
  Manual and fresh acquisition invalidate routine work without native reads.
  The clock scheduler can attach that reviewer at its paused pre-window boundary,
  after cleanup obligations drain. `serve --routine-reviews` wires this path under
  explicit player/clock control. Targeted live service acceptance covers native
  core/emergency facts, fifteen persisted needs, unknown food forecast, Manual
  invalidation, joined shutdown and disabled restart.
  The deterministic food forecast now accounts for diet/access, holder-owned stock,
  competing demand and rot deadlines. Typed native human food inputs have populated
  stock parity and Go/Python forecast replay. The combined native census now feeds
  routine FoodDays with animal competition; populated native replay reaches a durable
  food deficit. Native crop definitions now provide harvest yield and diet-specific
  human/animal demand. Routine reviews request planning facts and budget each crop's
  growing cells against its growth cycle plus the configured food reserve; mixed
  crop coverage stays separate from stored-food runway. Missing definitions or demand
  preserve unknown coverage. Native parity and durable review tests cover this path;
  further use of crop/patient forecast fields remains open. Complete native farm and cooking
  censuses feed edible growing-cell capacity and usable, unsuspended food-bill needs.
  Unknown counts or availability remain unknown. Populated native replay verifies
  thirty growing rice cells and a cooking recovery in the durable Go review.
  The native naming-window census now supplies naming deficits and explicit absence;
  unavailable or obstructed observations remain unknown. Native dialog/replacement
  acceptance and durable replay verify need changes without confirming names.
  Routine reviews now bracket exact-ID equipment reads with the native colony and
  emergency census to derive available armed capacity. Missing pawn rows, equipment
  or conflicting counts/states remain unknown; stale ticks/generations reject the
  review. Targeted native service acceptance verifies an observed equipment shortage
  reaching the durable defense deficit, Manual invalidation and disabled restart.
  Work proposals now port stable specialist selection, construction skill precedence,
  labor sharing, numbered/checkbox semantics, requirements and explicit overrides.
  Typed pawn work reads feed default assignment readback into routine work coverage;
  missing availability, mode, skills or work data remain unknown. Native replay
  matches Python's complete proposals; live service acceptance verifies the work
  deficit, Manual and disabled restart. Open selected player buildings and admitted
  shared projects now supply native construction-skill requirements. Supplementary
  definitions stay inside the paused bracket; unknown requirements stay unknown,
  and unresolved cancellation retains requirements until native effects settle.
  Native HospitalBed acceptance and Go/Python replay verify a skill-eight project
  deficit, Manual invalidation and disabled restart. Plan-scoped player work
  preferences now persist with revision checks and authenticated replace/read APIs.
  Updates invalidate old reviews and methods atomically; new reviews consume the
  saved revision, reject stale inputs and preserve unknown native capabilities.
  Native acceptance verifies preference updates, clear/replay, revisioned work
  reviews, Manual and disabled restart; Go/Python native replay agrees. Requirements for
  other action families remain open; settings execution belongs to its action family.
  Superseded invalidated autopilot goals now retire from active capacity while
  retaining immutable history; uncertain effects and cleanup prevent retirement.
  Completion observed after cancellation now yields historic cost/geometry holds
  to fresh native stock and placement facts without reviving cancelled intent.
  Settled autopilot method plans now retire from active capacity with retained
  exact history and per-world observation floors; stale admission/dispatch stays
  blocked after restart. Current plans, unfinished dependencies, uncertain effects,
  and cleanup remain pinned. Observed unsuccessful building methods now yield their
  reservations to fresh native stock/placement and retire with the same durable
  observation floors; unknown outcomes remain held.
  A player-gated indoor-sleeping compiler now turns reviewed shelter deficits into
  complete pending shared methods using native definition/room/placement facts,
  protected admissions and stable epoch identities. Cleanup needs now derive from
  the shared owned-draft journal. Opt-in `--routine-sleeping-plans` attaches compilation
  to the paused service review boundary; startup stays disabled and preview failures
  block new clock windows. `--routine-methods` connects eligible building methods to
  shared Hands under the existing player direction, with journal rechecks and pending
  player work taking priority. Targeted native acceptance verifies three indoor
  sleeping spots completed through shared Hands with one attempt each, unchanged
  player authority, Manual invalidation and disabled restart. The shared compiler
  also selects a single campfire for known cooking deficits, waits for existing
  facilities/projects, and respects player resource reservations. The opt-in cooking
  service path has targeted native acceptance alongside sleeping construction:
  one campfire completes with one attempt under shared player authority, while
  cooking remains a deficit until a usable food bill exists. Native power-trader
  censuses now feed per-consumer-network headroom, electrical demand and disabled
  consumer recovery into routine power needs. Incomplete facts remain unknown;
  unrelated networks cannot cover a consumer. Populated native power acceptance and
  Go/Python replay verify a durable deficit, Manual invalidation and disabled restart.
  Shared session resource rules now reach routine method admission and Hands;
  repeatable service flags configure reserves and spending restrictions. Fast tests
  cover blocked and unaffordable cooking methods. Native acceptance verifies a wood
  stop rule leaves player work pending, admits no routine campfire and issues no
  construction orders, with Manual invalidation and disabled restart. Pending
  construction now records complete attempt-correlated inspection proof; later-tick
  native net stock can replace its original cost hold while geometry remains pinned.
  Unknown effects restore the conservative hold. Native acceptance verifies two
  five-wood walls under a 290-wood reserve from 300 available wood: the second is
  admitted while the first is unfinished, both complete with one attempt each,
  and Manual/disabled restart preserve authority and accounting evidence.
  Routine reviews now persist optional development ranking, known worker capacity,
  deficit fractions, game-tick waiting age and selection history. Shared player
  projects and unresolved optional methods consume capacity; method admission
  rechecks current commitments atomically. Manual clears selection, and changed
  direction/world or rewound ticks reset age. `--routine-project-limit` bounds new
  optional projects. Native acceptance verifies observed worker capacity, the
  accepted player's occupied slot, deficit ranking, Manual clearing and disabled
  restart. Fast checks cover aging/restart, unknowns, cancelled uncertain projects,
  corrupt history and player admission between review and method commit.
  Service reviews now exclude disabled optional planners from selection while
  preserving their needs and accepted commitments. Configured method availability
  is separate from native evidence and cannot invent recovery.
  The starter shelter compiler now prefers existing indoor space, then admits a
  whole native-grounded wood wall-and-door shell with observed door dependencies.
  A bounded post-construction clock allowance waits for normal roofing; furnishing
  requires fresh roofed indoor facts. Targeted native acceptance verifies all 32
  shell pieces and three sleeping spots completed with single attempts, native
  indoor capacity recovery, a satisfied shelter goal, the complete operation trace,
  Manual invalidation and disabled restart. A normal clock deadline reached during
  renewal preflight preserves authority only after fresh matching completion proof.
  Ongoing medical care now has a separate maintained priority-2 need using the
  same bracketed native pawn census. Complete bad-condition/rest facts distinguish
  chronic care from urgent tending. Durable tracked patients cannot recover through
  disappearance, death, missing health or restart; Manual preserves that evidence,
  while world replacement and tick rewind reset it. Medical order execution remains
  in G01.07a. Native Python parity covers a colonist with permanent injuries and
  missing parts who needs neither tending nor medical rest; the care deficit and
  patient evidence survive Manual and disabled restart. Fast checks cover missing
  health, disappearance, recovery/renewal, cancellation, restart and corrupt history.
  Startup supplies now retain the first known native cohort across Manual,
  direction changes and restart. Complete reads shrink it without adopting later
  forbids or reviving released cells; unavailable reads preserve unresolved cells.
  Native acceptance clears the original cohort through player Allow, then verifies
  that re-forbidding the same supplies does not reopen the need or issue operations
  across Manual and repeated same-database restarts. Fast checks cover unknowns,
  an initially empty cohort, later cells, world/rewind reset and corrupt history.
  Comfort planning now projects native dining surfaces, accessible seating and
  recreation facilities, retains observed use across Manual/restart, and selects
  table, adjacent chair and recreation construction through shared building plans.
  Ordinary use waits are bounded by observed construction in the current direction;
  construction receipts do not certify comfort recovery. Native acceptance verifies
  all three buildings, dining and recreation use, and Manual history retention.
  Recreation previews and observed capacity require native playing-cell clearance
  and safe access. Disabled restart of this completed native sequence remains open.
  Expansion maintains one spare indoor sleeping place beyond observed population,
  using the shared indoor furnishing and whole-shell compiler. Existing housing
  deficits block optional expansion; shared project capacity, reservations and
  Manual invalidation still apply. Native acceptance verifies one additional indoor
  place, single-attempt construction, capacity recovery and disabled restart.
  Broader room-development methods remain open.
  Equipment policy now has typed census review, stable replacement selection and
  bounded production proposals with material preservation, shared budget inputs
  and existing-bill protection. Native gear projections feed the complete routine
  census and durable equipment need, preserving unknown evidence and renewed
  deficits across restart and Manual cancellation. Production proposals retain
  typed work/skill requirements for shared allocation and player work preferences.
  Native workshop/work requirement projections and acceptance remain open; proposals are not
  connected to execution yet. Equipment remains visible as `method_unavailable`
  without occupying a development slot until its execution family is available.
  Four direct upkeep contracts now project complete native item, structure, fire
  and filth sections into durable needs. Repair censuses exclude native definitions
  that do not use hit points. Missing sections preserve established risk;
  an unknown first observation does not invent an emergency. Target ordering and
  progress metrics match the Python contracts. Populated native parity and Go
  replay verify all four target lists, metrics and durable needs, including Manual
  and restart. Bounded-read unknowns remain explicit. Method composition remains
  open, alongside the other five upkeep contracts.
  Medical reserves now project observed medicine stacks and usable resources into
  a hysteretic maintained need. Captured native replay verifies stock caps and
  entry/recovery thresholds; durable tests cover unknowns, Manual, restart and
  renewed deficits. Replenishment method composition remains open.
  Further need inputs, method selection and execution composition for
  additional routine methods remain open: equipment, comfort/expansion, the ten
  `colony_upkeep.CONTRACTS` needs, player resource targets, and policy-generated
  population, husbandry, mood, waste and disaster-recovery needs.
  Connect their planners to available action families; remaining action execution
  stays in G01.07a–f.

- [ ] **G01.07a — Finish defense and essential medical care.** Connect movement
  and melee to complete defense plans using the existing owned-draft lifecycle;
  verify target outcomes, injury interruption, player overrides and restart cleanup
  through Go. Restrict supported ranged attacks explicitly to direct bullets
  before enabling them; the native Ranged mode also permits explosives. Port
  critical tending, interrupted/repeated treatment, patient rest settings and
  medical monitoring with fresh doctor/patient facts and observed care outcomes.
  Do not redo the accepted temporary-draft service implementation.

- [ ] **G01.07b — Port food and work allocation.** Food acquisition/production,
  crops, cooking and work assignments must run through shared planning and Hands.
  Preserve emergency preemption, route safety, recurring deficits and interrupted
  production. Verify stock changes caused by ordinary pawn work.

- [ ] **G01.07c — Port shelter and upkeep.** Complete shelter/adoption, room and
  footprint planning, spatial reservations, storage/hauling, beds, Home coverage,
  repairs, fire response and staged wall upgrades. Reuse building execution.
  Preserve player exclusions, structural supports, quantity accounting and
  recovery after layout changes. Follow food/startup priorities.

- [ ] **G01.07d — Port colony development.** Temperature, power, facilities,
  equipment, research, mining/material extraction and resource development.
  Preserve prerequisites and scarce-resource competition; verify actual outputs,
  equipped items and completed research rather than command receipts.

- [ ] **G01.07e — Port remaining colony management.** Mood, extended medicine and
  surgery, animals, waste, population and trade policy. Preserve existing player
  policies, recurring needs and care commitments. Verify goods, health, custody
  and containment outcomes. Use the food, medical and facility paths above.

- [ ] **G01.07f — Port world progression.** Caravans, quests and multiple active
  maps: packing, departure, arrival, return/storage, rewards and failure recovery.
  Preserve supply/home-staffing checks and stale-map rejection. Reuse existing
  world-progression scenarios after the needed development/management paths work.

- [ ] **G01.08 — Connect local-model chat and player commands.** Use the existing
  local transport, context budgeting and typed interpreter. Port the remaining
  command families, consultation/scout/visual review and required knowledge,
  memory and evidence retrieval; route accepted commands into the same plans and
  Hands. Wire streaming, deduplication, cancellation and explicit unsupported-command
  errors into the service. Use configured LM Studio models only. Verify scripted
  invalid/cancelled replies and representative real-model player requests;
  advisers have no native mutation capability.

- [ ] **G01.09 — Finish player services and presentation.** Expose current action
  hold/observation-failure reasons from the worker through the API and dashboard,
  scoped to action/world/direction and cleared when stale. Finish player controls
  for the ported command families, camera/input ownership, portraits, follow and
  video orchestration without a Python media service. Complete trusted save/load
  admission against the attached game: drain writers and owned resources, verify
  pause, reject stale direction/foreign instances, then use the existing native
  lifecycle boundary. Preserve drafts/last-good data and verify reconnects,
  competing viewers and actual rendered/input outcomes. Existing read endpoints,
  observation panels and building/draft/clock controls do not need reimplementation.

- [ ] **G01.10 — Run the complete controller in Go.** Compose routine policy,
  all supported action families, chat, dashboard, clock, recording/diagnostics and
  session recovery in one production process. Resolve remaining responsibility
  gaps against the current Python source and the
  [domain](../contracts/domain-inventory.json),
  [interface](../contracts/interface-inventory.json) and
  [state](../contracts/state-inventory.json) inventories. Inventories are a coverage
  aid, not proof their old status labels are current. Verify fresh startup,
  ordinary colony work, player interruption, world/load changes and restart with
  Go owning the entire path. No per-operation Python fallback or dual writer.

- [ ] **G01.11 — Package and launch Go everywhere.** Update Windows and Docker
  build/install/launch paths, configuration and scenario adapters to run the Go
  service with the dashboard and required media components. Keep loopback access,
  private profiles and one-writer ownership. Produce a runnable install/image
  without production Python; identify any Python tools retained for development.

- [ ] **G01.12 — Accept Go and make it the default.** After integration and
  packaging, run applicable automated checks and targeted native acceptance for
  fresh startup, ordinary pawn work, player/chat controls, interruption and
  same-session persistence/restart. Reuse existing valid evidence. Switch all
  production defaults and documented launch paths to Go once the controller is
  functional and operable. Historical byte parity, performance campaigns and
  unimplemented new B-series gameplay are not rewrite gates.

- [ ] **G01.13 — Delete the Python production controller.** Remove replaced code
  under `controller/rimgovernor`, Python production entry points, duplicate runtime
  implementations and runtime-only dependencies. Relocate any assets or utilities
  still required by Go before removing their old directory. Preserve source/license
  notices, useful fixtures and explicitly retained scenario/development tooling.
  Update setup, architecture, source map, troubleshooting and CI. Verify the
  production install starts and serves every supported path without a Python
  interpreter, and no launcher, API, chat or media path silently invokes Python.

### Scope and completion evidence

Port existing implemented behavior. Unfinished B-series features remain their own
backlog; they must not turn this rewrite into a new gameplay development campaign.
For each delivered capability, keep checks and native/model scope in its commit
and local artifacts, then remove its completed backlog entry. A schema, build or
receipt alone does not establish working gameplay. Shared native implementation
work remains in N01; change those boundaries only where a Go consumer needs it.

- [ ] **Clock-worker stop test synchronization.** Make
  `TestClockWorkerTransportBlockedWriteRetainsOwner` distinguish transport deadline
  cancellation from completed local stop invalidation before asserting authority
  state. Preserve the blocked-write ownership and joined-shutdown assertions.

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

Consume the existing canonical Protobuf schemas and generated DTOs; do not create a second schema tree.
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

- [ ] **N01.03 — Typed observations.** Native observations owner with the Go observation adapters. Migrate
  colony status and batch envelope, then pawns/health, supplies/buildings,
  rooms/zones/cells and remaining facts in bounded slices. Preserve unavailable
  information, section freshness and modded definitions. Check actual SDK decoding,
  invalid identities and read-only native behavior. Produce exact scoped snapshot
  tokens for all mutation targets, health/census/production policy, complete wall
  geometry and drill lifecycle, trade-line/cargo-group identities and architect
  catalogs. Stable pages bind frozen query/context; initialization belongs load
  hooks rather than read calls. Depends on 02's verified first adapter.

  Basic typed status and bounded cell reads are implemented. Explicit all-false
  cell fields support map-bound discovery; terrain, roof, visibility and traversal
  are available, while other requested fields report Unsupported. Status does not
  issue general entity CAS snapshots. Available pawn draft controllers expose
  narrowly scoped draft-control tokens and canonical ownership claims. Go read-only service polling is accepted against
  the actual native status adapter: two fresh HTTP observations, unchanged paused
  identity/tick/generation, exclusive GABS handoff and joined shutdown. Complete
  operation-event coverage verifies identity/status calls only. Remaining families
  and frozen continuation pages remain open.

  Bounded building reads include exact walls, blueprints, frames, full occupied
  cells, materials, hit points and construction work/resources. Native acceptance
  covers queued, frame, finished and cancelled states. Entity CAS, settings,
  bills, inspect detail, service/thermal facts and power-network enumeration remain
  explicit unsupported/incomplete scopes rather than fabricated defaults.

  Bounded supplies reads cover exact native definitions, full ownership quantities,
  spawned stock and spawned-root inventories/containers with complete item lists.
  Headless and rendered acceptance verifies WoodLog/Steel plus populated carried,
  container and fogged stock against native census totals, unchanged paused context,
  and explicit unknown held counters when excluded. Dedicated corpse/trader and
  nested-owner fixtures, traversal/collection overflow, modded definition fixtures,
  frozen paging and entity CAS remain open. These reads exclude worn gear, orbital
  trader stock and delivered construction materials; they do not prove trading or
  production availability beyond the stated census scope.

  Bounded pawn reads cover exact intersecting filters, known false values and
  explicit detail availability for health, needs, gear, biography, work, schedules
  and animal training/production. Headless and rendered acceptance compares complete
  colonist/animal censuses and native details, verifies refusal bounds, and preserves
  paused identity/ticks. Social detail and general entity CAS remain unsupported;
  corpse and additional populated detail fixtures remain open. Draft-control CAS
  and exact owned/unowned claims are available when verified native hooks and a
  current-map draft controller are present.

  Bounded research reads cover project progress, prerequisites and ordinary native
  eligibility, optional unlocks and map-local bench/researcher facts. Headless and
  rendered acceptance verifies 122 projects, three researchers and populated
  bounded unlocks, with unchanged saved progress/knowledge/slots and paused context
  before a separate native getter audit. Populated benches/facilities, active
  Anomaly slots, frozen paging and research CAS remain open. Global unlock expansion
  may exceed the child bound; narrow project queries return complete collections.

  Bounded room reads cover exact geometry, native statistics and optional cells,
  boundary contents and memberships. Headless and rendered acceptance verifies
  populated indoor structures and exact filters/refusals against native reads,
  preserving paused identity/ticks. Populated bed, pawn and stockpile memberships,
  frozen paging and room CAS remain open.

- [ ] **N01.04 — Typed guarded operations.** Native operations owner with G01 Hands.
  Start with ordinary construction through admission, dry-run, receipt and observed
  pawn completion. Follow with settings/bills/zones, resources/upkeep, medical,
  animals/population, trade/world and explicit player operations. Keep editor/cheat
  tools outside automation. Accept refusal/dry-run without effects, stale commands,
  lost reply followed by observation and player override per migrated family.
  Implement the unsaved bounded admission ledger, actual effect attribution and
  causally fresh same-tick readbacks. Exact owned draft cleanup remains possible
  after revocation and ordinary-ledger exhaustion. Honor production replacement
  presence, per-setting-entry outcomes, actual trade transfer and unsuccessful
  progress without inferring completion from vanished jobs/bills.
  Depends on 02 and its required observations, not the entire Go port.

  Guarded `PlaceBuilding`, operation preview, immutable attempt receipts and
  causally tracked construction progress are implemented. Fresh graphical and batch
  acceptance proves normal pawn-built walls and Go SQLite restart observation
  without redispatch, plus Manual, cancellation, draft/order invalidation, expiry,
  conflict and replay. Required live transition patches are verified before
  admission; oversized evidence retains encodable uncertainty. Other construction commands
  remain Unsupported. Fresh Go restart also observes exact player cancellation
  as terminal unsuccessful/cancelled without redispatch or simulation advancement.
  Actual Go HTTP service acceptance covers submit/replay without native writes,
  explicit acquisition and one blueprint, Manual disable, joined exclusive GABS
  handoffs, ordinary pawn completion and disabled same-database restart observation
  without reacquisition or redispatch. Service and orchestrator event traces are
  checked independently for complete attribution.
  A paused actual Go service also holds an otherwise legal stocked wall during a
  positively observed standing threat with a complete healthy colonist census.
  After explicit Manual, joined shutdown and exact fixture threat removal, the same
  database restarts disabled; a new explicit Acquire admits exactly one blueprint.
  Full event traces distinguish emergency reads from background polls and prove
  zero unsafe writes. This gate establishes admission, not completed pawn work.
  Temporary `SetDrafted` and exact `ReleaseOwnedDraft` have paused native acceptance
  for causal claims, immutable replay, already-owned NoChange, stale CAS refusal,
  cleanup after Manual and confirmed lease expiry, and exact cleanup replay.
  Successful player orders invalidate claims; refused cleanup preserves the observed
  player job. Unowned player drafts cannot be adopted. Other pawn orders,
  persistent draft policy, fault-injected uncertain setters, context replacement
  and actual-game cleanup with an exhausted ordinary ledger remain open.
  Exact owned `MovePawn` has native acceptance for real arrival, correlated
  job/target progress, immutable replay, same-position NoChange and player-order
  interruption. Queued orders remain pending. Cleanup attribution survives an
  in-flight lease expiry without restoring write permission; fault-injected native
  expiry and uncertain-order recovery still require game acceptance.
  Guarded melee `AttackTarget` has native acceptance for exact animal target
  snapshots, attributed target death, immutable replay, player-order interruption,
  refused adoption of player drafts, fresh owned recovery and cleanup after Manual.
  Completed-before-Manual evidence remains observable. Compiled checks cover
  unrelated/nested damage refusal and exact live melee-hook repair. Extend game
  acceptance to attributed downing, unrelated damage and uncertain dispatch.
  Ordinary direct-bullet ranged attacks have headless native acceptance for exact
  projectile-attributed target death, player override, refused draft adoption,
  fresh owned recovery, replay and completed-before-Manual cleanup. Compiled
  actual-Harmony checks cover notification side damage, shields, misses, nested
  damage, tracking loss, job pooling and individual melee/ranged hook repair.
  Extend native acceptance to those refusal cases and projectiles already in
  flight at interruption.
  Ordinary injury-only explosive projectiles retain exact projectile-to-explosion
  lineage across ticks. Native frag-grenade acceptance verifies attributed target
  downing and death across ordinary deep water, player override, fresh ownership, replay and
  completed-before-Manual cleanup. Compiled checks cover delayed damage, nested
  factories, notification damage, shield detonation, lost tracking and hook repair.
  Ambiguous factory prefixes and damage prefixes that can replace identity-bearing
  arguments disable supported attribution. Extend native acceptance to in-flight
  interruption and those refusal cases. Overhead, beam, fire/gas/spawn payloads and
  custom projectile/damage-worker paths remain unsupported.
  Native lost-reply fault injection,
  instant/replacement construction cases and remaining operation families are open.

- [ ] **N01.05 — Runtime and presentation ownership.** Native runtime owner.
  Recover partial draft-hook initialization without requiring a game restart;
  failed initialization currently keeps snapshots and mutations unavailable.
  Consolidate startup/patch health, supervisor/journal ownership, render leases,
  camera/input cleanup and platform adapters. Make initialization/shutdown bounded
  and idempotent. Required guard failure disables affected automation. Address the
  delayed Watch/Trade cleanup, pending
  pawn images on destruction, render restoration after player edits and combat
  arms lacking game identity. Verify disconnect/lease expiry, load/map changes,
  repeated initialization, patch failure, batch pawn work and graphical rendering.
  Replace UTC-based lease expiry with monotonic duration checks while journal
  diagnostics retain Unix timestamps. Implement typed clock/lifecycle attempt
  lookups, superseded loads, verified pause states and correlated uncertain UI
  results. The early missing-journal cache now retries after delayed SDK loading;
  its isolated regression passes. The pinned Protobuf runtime closure and notices
  load in fresh graphical and batch games. Unsaved monotonic authority and trusted
  control admission have verified native invalidation hooks. Actual graphical
  and batch cancellation, draft/order changes and expiry revoke authority; ordinary pause
  preserves it. Game-level load/map transitions, disconnect integration and the
  remaining lifecycle/presentation owners remain open.
  All seven typed clock methods use original-grant ownership, monotonic leases,
  the shared bounded attempt ledger and immutable observed event context. Runtime
  checks cover same-thread revoke/reacquire refusal, failed pause, missing hooks
  and corrupt journal rows. Native acceptance covers bounded ticks, owned controls,
  replay, cross-family conflicts and Manual revocation. Additional native load/map
  replacement, hook/journal fault injection and injury/presentation cases remain open.
  Typed camera, selection and loaded-map colonist roster reads are implemented.
  Native graphical acceptance verifies camera facts, an explicitly selected pawn,
  exact roster and unchanged paused context; headless camera/selection are explicitly
  unavailable while roster facts remain readable. Selection does not enumerate
  gizmos/inspect tabs or grant captured input authority. Multi-map rosters, Zone/Plan
  selection, native overflow cases and remaining presentation methods remain open.
  Lifecycle Save/Load admission belongs to the Go/GABS session owner: current
  instance/direction, Manual, joined writers, draft/input cleanup and verified pause.
  Native authority inactivity does not establish that admission. Reuse SDK save/load
  dispatch and add exact native publication/new-Game evidence. Save acceptance must
  distinguish an observed overwrite from an old complete file after a swallowed
  native save error; load acceptance must correlate readiness to its own new Game.
  Cover identical-content overwrite, failed publication, replay, timeout, supersession
  and instance-lifetime request lookup across map replacement before advertising support.
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
  before adding checks. Reuse the existing Go formatting/vet/test/race/platform
  and schema-drift checks. Add scoped strict Python checking
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
