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
all existing implemented Python responsibilities have working Go replacements,
and the production install/image needs neither Python nor a Python sidecar.
Delete replaced runtime code, entry points and runtime-only dependencies. Python
may remain for explicitly identified development/scenario tools.

**Current state:** partial controller, not a drop-in replacement. Shared goals,
resources, cancellation, typed reads, building execution, owned drafting, melee
internals and several construction planners exist. Most other routine features
stop at needs or proposals. Production launchers still run Python. See the
[source and evidence review](developers/go-migration-review.md) for the capability
assessment, evidence limits and inspected revision. Completed work belongs there
or in component docs/commits, not in this remaining-work list.

**Delivery rule:** complete the workflows below through existing Python behavior:
observation, decision, admitted shared method, Hands, observed result, interruption
and renewed need. G01.05 and G01.07 are overlapping responsibilities in those same
workflows, not sequential projects. Do not finish every planner before adding
execution. Keep their IDs for references; count each deliverable once.

Work serially and reuse existing code/native operations and applicable evidence.
No new gameplay, planner/UI redesign, historical state migration, compatibility
layer or generalized hardening campaign. State is disposable. Native changes need
an actual missing consumer contract. Keep strict typed boundaries, normal game
rules, one writer, player exclusions, unknown facts and uncertain-write recovery.
Local commits are authorized. Land verified commits in main by fast-forward,
without pushes, when the target checkout is safe; preserve other developers' work.

### Remaining gameplay workflows, in delivery order

- [ ] **G01.05 — Routine planning integration across the workflows below.**
  Use Python `ColonySkills.compile`, `priority_nodes`, the ten upkeep contracts
  and their delegated modules as the finite reference. Port missing method
  selection together with its executable action family. Finish remaining
  crop/patient forecast use, workshop and other non-building work requirements,
  retry/watchdog behavior and prerequisites where the corresponding Python
  workflow consumes them. Preserve priority/age/hysteresis, whole-project resource
  and spatial accounting, player work preferences, unknown observations, competing
  projects, renewed deficits and cancellation. Routine events make no model calls.
  Close this ID only when routine paths in G01.07a–e are composed; the evidence
  belongs to those workflows, not a duplicate planning acceptance campaign.

  **Burndown plan.** Deliver serial vertical slices in the order below. One owner
  carries each slice from reference behavior through Go composition and acceptance;
  the same owner integrates its verified commit. These are substeps of G01.05 and
  G01.07, not additional features or separate acceptance campaigns.

  **Implementation first; native acceptance at the end.** Defer new in-game tests
  until the Go rewrite's implementation, integration and packaging are code complete
  through G01.11, including the replacement paths needed to retire Python. Use fast
  unit, fixture, replay, contract and composed runtime tests to land implementation
  slices on main. Code complete means typed actions, persistence, handlers and
  service wiring exist and pass applicable automated checks; proposals alone do not
  qualify. Keep incomplete capabilities gated and gameplay evidence explicitly open.
  The native outcomes below are deferred G01.12 acceptance cases, not prerequisites
  for starting the next slice or landing code. Reuse existing evidence at that final
  gate, then fix failures with fast regressions and targeted native reruns. This
  sequencing applies across G01 and its required N01 prerequisites, overriding their
  per-feature native-test timing for this rewrite. It does not waive gameplay
  acceptance before the production-default switch or final G01.05 closure.

  - [x] **05.1 — Bound reference coverage before the first implementation slice.**
    Map every current `ColonySkills.compile` branch, `priority_nodes` producer and
    the ten `colony_upkeep.CONTRACTS` entries to one G01.07a–e workflow, its existing
    Go implementation, missing action/selection wiring and observed postcondition.
    Include delegated dynamic goal producers, prerequisites and wait-only methods.
    Keep unresolved rows here under their owning workflow; put completed evidence
    in commits. Explicitly assign cross-cutting startup/worker cleanup prerequisites
    to their first consumer. Naming confirmation remains G01.08; world progression
    remains G01.07f. Neither is silently omitted from the reference accounting nor
    added to this ID's closure gate. Audit current source rather than treating the
    migration review's historical capability assessment as current test evidence.

  - [ ] **05.2 — Food and production bootstrap (G01.07b, shared storage in c).**
    First connect original-supply Allow and saved work assignments; then safe
    harvest/wood acquisition and bounded hunting; then fields/crop choice and
    cooking, butchering and preservation bills. Deliver each executable method
    before expanding to its next alternative. Bring forward only the storage/haul
    subset of c needed to observe protected food output, and count it once there.
    Consume crop forecasts while keeping future yield, animal demand, reserved
    stock and current edible stock distinct. Reuse accepted campfire and field
    geometry. Gate on ordinary acquired, harvested, cooked and stored output plus
    a renewed deficit, emergency interruption and preserved player assignments.
    Production bills remain unfinished on `codex/g01-05-bootstrap`: the typed
    bill path, schema 36, planners and native output tracking need regression
    coverage, player-edit invalidation and final native/protocol validation.
    Compile-only Go checks are not completion evidence. Reserved-stock accounting
    and protected storage/haul remain unimplemented; gameplay acceptance remains
    tracked under G01.12.

  - [ ] **05.3 — Defense and urgent care (G01.07a).**
    Compose squad selection with existing draft/melee and exact owned cleanup;
    add movement, accessible equip and supported ranged actions as needed. Follow
    with native-approved doctor/patient selection, tend, rescue/rest and monitoring.
    Consume patient forecasts without treating predicted recovery as completion.
    Gate on actual defense and patient outcomes, repeated/interrupted treatment,
    player takeover and restart/stand-down cleanup. Preserve the current explosive
    exclusion and fail closed on unsupported or unavailable native capabilities.

  - [ ] **05.4 — Storage, shelter and direct upkeep (G01.07c).**
    Finish room adoption and storage ownership after the food prerequisite subset;
    then bed assignment/upgrade/use, Home coverage, repair/clean/fire selection and
    staged stone replacement. Close the partial comfort lifecycle using existing
    construction/use evidence where applicable and verify disabled restart.
    Cover `SecureSupplies`, `MaintainSleeping`, `MaintainHomeCoverage`,
    `MaintainEssentialRepairs`, `MaintainCleanFacilities`, `MaintainFireSafety`
    and `MaintainStoneShell`. Preserve exact ownership, exclusions and temporary
    structural support; wait for ordinary labor where Python does. Gate on storage,
    bed use and upkeep outcomes, layout changes and interruption, not issued jobs.

  - [ ] **05.5 — Equipment, research and replenishment (G01.07d).**
    Connect gear replacement/equip/wear to workshop recipes, bills and output;
    then research prerequisites, facility/refrigeration development, extraction
    and maintained resource targets. Use this same production path for
    `MaintainMedicalReserves`. Add missing non-building labor/ingredient forecasts
    only at their consuming method. Preserve player bills, material preferences,
    whole-project reservations and competition with food/medical work. Gate on
    finished/equipped gear, research progress through completion, extracted stock
    and replenishment after a renewed target. Reuse accepted power/temperature paths.

  - [ ] **05.6 — Management and service recovery (G01.07e).**
    Compose dynamic mood, ongoing care/surgery, population, herd, waste and trade
    needs with their executable methods. Reuse b/d for `MaintainAnimalFeed` and
    medical supplies; complete `MaintainAnimalContainment` with containment policy.
    Connect disaster history and bounded candidates to service jobs, non-widening
    existing-area admission and owned restriction cleanup. Implement each management
    family's need/health/stock/custody/service postcondition and fixture coverage
    before starting the next. Deferred native gates cover renewal, interruption and cleanup while preserving
    emergency priority, care commitments and player policy.

  - [ ] **05.7 — Close the routine integration coverage.**
    Reconcile the 05.1 reference rows against composed a–e paths and their evidence.
    No row may end at a need, proposal, unregistered action or unwired compiler.
    Verify priority/age/hysteresis, competing projects, prerequisite failure,
    retry-signature changes, no-progress holds and observed recovery through fast
    composed tests. Include unknown observations, uncertain replies, renewed
    deficits, cancellation, Manual, world replacement and disabled same-database
    restart. Assert zero model calls for routine reviews/events. Reuse family
    native evidence; add a combined scenario only for an uncovered interaction.
    Close G01.05 only when all its routine a–e rows pass; this does not close the
    player, world, packaging or production-switch items.

  **Per-slice implementation and verification.** Policy owns deterministic needs
  and method choice; domain/store own typed plans, durable progress and atomic
  resource/spatial accounting; Hands owns dispatch and uncertain-write recovery;
  runtime owns authority, review/clock composition and cancellation. Extend the
  existing Go owners and service wiring rather than adding a second planner or
  executor. Reuse native operations; coordinate with N01 only for a demonstrated
  missing consumer contract. A prerequisite-only commit stays gated and does not
  complete the slice.

  Start each slice with its Python branch, owning contract, missing behavior and
  smallest tests. Use policy fixtures/reference replay, store reopen/fault tests,
  handler coverage and runtime composition tests during iteration. Follow
  [test selection](developers/testing/choose-tests.md) and
  [Go checks](../go/README.md) for the full affected automated suite once before
  handoff; shared wire changes also use [generation checks](../contracts/schema-generation.md)
  and applicable dashboard checks. Use `GOMAXPROCS=2` and `-p 1` for local Go;
  reserve race checks for the supported Linux worker. During implementation, record
  which pawn outcomes fixtures cannot prove and prepare the deferred cases. At
  G01.12, extend the owning scenario through
  `scripts/container_scenario.py`, with fresh artifacts and a task-specific image;
  observe actual postconditions and reuse unchanged lifecycle evidence. Record
  source/binary/input identity, commands, results and limitations in the commit
  and `.rimgovernor/` reports. Add a fast regression for any acceptance failure
  where feasible, then rerun only the affected scenario. Land each verified slice
  on safe main by fast-forward without pushing; keep remaining gaps in its
  existing workflow entry.

- [ ] **G01.07b — A colony can obtain food and sustain ordinary production.**
  Implement startup Allow for the retained original supplies, work-priority writes
  from existing assignments, safe wild harvest/tree acquisition and bounded hunting,
  growing-zone creation/expansion, crop selection, cooking/butchering/preservation
  bills and their prerequisites. Finish food stockpile creation/filtering and safe
  hauling with G01.07c. Account for reserved stock, outstanding designations,
  animal demand, spoilage and future harvest separately. Reuse forecasts, field
  geometry, campfire construction and saved work preferences. Route safety,
  emergency preemption and interrupted/renewed production remain required.
  **Exit evidence:** Go assigns workers, acquires food/wood, creates or maintains
  a field and meal bill, and observes harvested/cooked/stored output. Exercise a
  renewed deficit and interruption without duplicate orders or overriding player work.

- [ ] **G01.07a — A colony can defend itself and care for injured pawns.**
  Compose existing draft/melee execution into squad defense; add movement,
  accessible-weapon equip and supported direct-bullet ranged attacks. Keep
  explosives outside the supported ranged contract. Add native-approved doctor/
  patient selection, tending, rescue/rest settings and medical monitoring,
  including interrupted or repeated treatment. Connect existing emergency needs
  and cleanup; reuse owned-draft lifecycle rather than replacing it.
  **Exit evidence:** observed defense result, tended/resting patient, injury
  interruption, player override and restart/stand-down cleanup through Go.

- [ ] **G01.07c — A colony can store supplies and maintain its buildings.**
  Complete player-room adoption/handoffs, storage zones and ownership, safe hauling,
  bed assignment/upgrade and actual bed-use verification, Home coverage changes,
  ordinary repairs/cleaning, safe fire response and staged stone-wall replacement.
  Preserve structural supports, exclusions, quantity accounting, exact owned
  identities and recovery after layout changes. Python sometimes waits for ordinary
  cleaning/fire labor; do not invent forceable native jobs. Reuse shell/sleeping
  construction, direct-upkeep targets, Home/stone ownership and sleeping history.
  Finish the existing comfort lifecycle acceptance (construction/use evidence is
  partial; disabled restart is not closed). Complete only room-development behavior
  already implemented in Python.
  **Exit evidence:** protected supplies reach valid storage, real beds are assigned
  and used, Home exclusions survive, repairs/cleaning/fire outcomes are observed,
  and a supported wall replacement preserves shelter and survives interruption.

- [ ] **G01.07d — A colony can develop equipment and replenish resources.**
  Connect equipment replacement/equip/wear and production proposals to native
  workshop recipes, skills, bills and ordinary output. Port research selection and
  prerequisites, refrigeration/facility development, material extraction/mining and
  player resource-target production. Add medical-reserve replenishment through
  the same production path. Preserve existing bills, material preferences, scarce
  resources and competing projects. Reuse accepted temperature/power/expansion
  construction; these are not unimplemented families.
  **Exit evidence:** finished/equipped gear, completed research, extracted/produced
  stock and a renewed resource/reserve target through Go. Established power and
  temperature evidence is reused unless affected code changes invalidate it.

- [ ] **G01.07e — A colony can carry out existing management policies.**
  Connect mood relief, ongoing care and requested surgery, animal containment/feed,
  herd configuration/training/slaughter policy, population decisions/integration,
  waste containment and trade. Port their missing dynamic needs/policy state, not
  just actions. Finish disaster recovery: native service-job and non-widening
  existing-area admission, owned restriction cleanup, repair/refuel/breakdown work
  and actual restoration. Reuse retained mood/patient/animal/disaster histories and
  bounded recovery candidates. Preserve custody, player policy, care commitments
  and emergency priority. Reuse food, medical, production and facility methods.
  **Exit evidence:** actual need/health/containment/stock/custody/service outcomes
  for supported Python workflows, including interruption, renewed needs and cleanup;
  candidates or receipts alone do not close any of them.

### G01.05 reference coverage and remaining composition

This is the finite 05.1 source inventory. Every row remains open for composition
or deferred acceptance unless the existing Go path is explicitly identified.
Python paths are under `controller/rimgovernor/`; Go paths under `go/internal/`.
The shared executable Go variants are currently building, owned draft and melee
(`domain/plan.go`). A need, forecast or proposal does not establish execution.
Keep remaining work in these owning workflow rows; remove resolved gaps as slices land.

**b — food/bootstrap (05.2).** `colony_policy.priority_nodes` produces these goals;
`ColonySkills.compile` selects the listed alternatives.

| Reference goal / alternatives | Existing Go | Missing composition and observed postcondition |
| --- | --- | --- |
| `AllowStartingSupplies`: discovered Unforbid, eight original cells, `supply_batches.supply_rectangles`, then wait | `policy/starting_supplies.go`, retained cohort and durable item claims; typed Allow plan/journal/Hands, native CAS/readback and opt-in `--routine-supply-plans` service composition | Native gameplay acceptance remains deferred to G01.12: observe original cells allowed, restart/lost replies and preserved later player forbids. First startup consumer: b. |
| `EnsureWorkAssignments`: changed priorities/checkboxes, eight-pawn batches, saved overrides and handler skill | `policy/work_assignment.go`, project requirements and saved preferences; typed work action/journal/Hands, native work-only CAS/readback, external-edit revocation and `--routine-work-plans` | Add non-building work requirements at b/d/e consumers as their actions land. Gameplay acceptance of saved overrides, checkbox mode, interrupted updates and ordinary qualified work remains deferred to G01.12. First settings consumer: b. |
| `MaintainWood`: safe trees, subtract outstanding yield, bounded acquisition or wait | Bounded typed acquisition planner/action/Hands with native CAS, enabled-worker/roof guards, exact produced-stack evidence and `--routine-acquisition-plans`; pending yield remains separate | G01.12 gameplay acceptance: ordinary wood recovery, renewed deficit, interrupted/failed labor and restart. |
| `EnsureFoodSupply`: `food_forecast.acquisition_targets` safe harvest before alternatives | Shared bounded wild-plant acquisition, diet/rot/competing-consumer forecasts and protected uncertain sources; native harvest/placement output evidence | Reserved production stock accounting joins bills below. G01.12: observe accessible nutrition, interruption and renewed deficit; regrowth may renew only resolved work. |
| `EnsureFoodSupply`: `food_capacity.choose_crop`, starter fields, `capacity_growth.growth_fields`, no-crop fallback | Native season/soil crop selection, bounded connected zone actions, shared construction reservations, exact zone readback and bounded ordinary growth windows via `--routine-field-plans` | G01.12 gameplay acceptance: ordinary sowing/harvest, renewed field deficit and preserved player footprints/crop overrides. Future capacity remains separate from harvested stock. |
| `EnsureFoodSupply`: preservation bill; butcher spot/bill; `hunting.screen_prey`, at most two outstanding hunts | Building execution, food forecasts and bounded shared hunting acquisition with native route/weapon/butcher prerequisites and exact prey/corpse evidence | Create missing butcher/preservation bills below; observe butchered/preserved output and renewed need. Hunting gameplay acceptance remains in G01.12. |
| `EnsureCooking`: selected-room furnishing, campfire, bounded fallback, available recipe target-count bill | Shared routine cooking building planner | Room handoff and executable bill after usable bench, preserving existing bills. Observe cooked output. Protected storage is the c prerequisite below. |

**a — defense/urgent care (05.3).** All four goals are fixed priority producers
and `ColonySkills.compile` branches.

| Reference goal / alternatives | Existing Go | Missing composition and observed postcondition |
| --- | --- | --- |
| `RestoreWorkers`: exact owned stand-down; wait during active threat | Routine cleanup need, draft journal/executor/session cleanup | First cleanup consumer: a, shared with medical/service shutdown. Compose routine release; observe exact claim release or positive supersession through restart/player takeover. |
| `ActiveCombat`: `combat_method.squad_defense` | Emergency policy, draft/melee/ranged execution, and composed bounded squad defense for humanlike opponents (`policy.SelectSquadDefense`/`EvaluateRangedDefense`, `domain.RangedAttack`, `executor.runRangedAttack`, `buildingruntime.RangedAttackBoundary`, `RoutineDefensePlanner`; native `AttackMode_RANGED`/`JobDefOf.AttackStatic` already supported, confirmed against `NativeCombatOperations.cs`) — at least two defenders per opponent, up to four opponents/eight defenders, ranged-vs-ranged requirement | Animal/manhunter opponents (native pawn snapshot does not yet expose body size, so `SquadThreatFacts.BodySize`/`Manhunter` stay unavailable and those threats are not selected), the single-raider tribal 3-defender/85%-health sub-case, accessible-weapon equip/fetch for an unarmed defender, `single_raider_defense`'s auto-mode weapon selection. Observe threat outcome and owned cleanup; explosive exclusion is native's own ranged-attribution allowlist (`NativeRangedCausality.Supports`), not something Go enforces separately. |
| `CriticalMedical`: native-approved `medical_triage.treatment_pairs`, repeated tend, active-tend/rest wait, owned doctor release only without threats | Emergency/patient needs, `policy/medical_care.go`, composed doctor/patient tend and rescuer/patient rescue selection+dispatch (`policy.SelectTend`/`EvaluateTend`, `policy.SelectRescue`/`EvaluateRescue`, `domain.Tend`/`domain.Rescue`, `executor.runTend`/`runRescue`, `buildingruntime.TendBoundary`/`RescueBoundary`, `RoutineTendPlanner`/`RoutineRescuePlanner`), and bounded per-patient repeated/interrupted-attempt retry with doctor/rescuer replacement (`medicalAttemptCount`, method IDs keyed by patient+attempt not by doctor/rescuer, capped at `maxMedicalAttemptsPerPatient` per goal episode) | Refusal-signature threading into goal evidence (no generic per-goal evidence store exists yet for any goal; building one is a separate cross-cutting change), patient forecasts. Observe living patient tended/in bed separately from healing. Unknown threats cannot authorize release. |
| `EnsureBasicDefense`: eligible unarmed pawn/accessibly stored weapon pairs, up to two defenders | Routine defense need/equipment facts | Equip action/selection shared with d. Observe exact weapon on selected pawn. |

**c — storage/shelter/upkeep (05.2 storage prerequisite, then 05.4).** The first
five rows originate in `priority_nodes` (comfort/expansion via
`development.development_nodes`). The other seven are upkeep contracts.

| Reference goal / alternatives | Existing Go | Missing composition and observed postcondition |
| --- | --- | --- |
| `EnsureInitialShelter`: `shelter_handoff` adoption, shell, sleeping furniture, large-colony partial furnishing / `capacity_growth.grow_shelter` | Shared shelter/sleeping building planners | Player-room handoff and population housing target; reconcile layout renewal. Observe roofed usable capacity, not walls alone. |
| `EnsureFoodStorage`: selected-room or starter-room furnishing after sleeping capacity | Storage need, construction/ownership facts | First c consumer in 05.2: typed stockpile geometry/filter/ownership and safe haul. Observe protected food quantity delivered; share with `SecureSupplies`. |
| `EnsureTemperatureSafety`: selected-room temperature, campfire/passive cooler, usable heat reuse and shelter wait | `policy/temperature_method.go`, shared thermal building path | Reconcile player-room handoff and hysteresis with existing path/evidence. Observe actual safe room temperature. |
| `EnsureComfort`: `development_method` delegates `comfort_upkeep` | Comfort history, shared building/use clock path | Close functional use and disabled restart coverage; furniture alone is insufficient. |
| `EnsureExpansion`: one spare indoor place through `grow_shelter` | Shared expansion/shelter planner | Reconcile prerequisites/competition with composed path; observe usable spare capacity and population renewal. |
| `SecureSupplies`: safe haul, `upkeep_storage.covered_storage` then `supply_storeroom` | Direct vulnerable-stock targets | Storage/haul handler and selection, covered destination and exact quantity ledger. Missing/merged source is not delivery. |
| `MaintainSleeping`: available-bed assignment, build bed, ordinary-use wait | Sleeping need and durable exact ownership/use history | Bed assignment/upgrade actions with player ownership guards. Observe safe real-bed use, not assignment alone. |
| `MaintainHomeCoverage`: `home_coverage.method` | Facility upkeep and exact construction claims | Home action from owned structures preserving exclusions. Observe required coverage without widening excluded cells. |
| `MaintainEssentialRepairs`: enabled worker and native-approved repair | Direct targets/metrics | Typed repair admission/handler/reconciliation; observe exact structure HP restored. Disappearance is not repair. |
| `MaintainCleanFacilities`: clean job or ordinary-labor wait for non-orderable targets | Direct targets/metrics | Safe worker selection, supported action and explicit wait. Observe removed filth; preserve unknown census. |
| `MaintainFireSafety`: bounded safe fires, reachable enabled firefighters, ordinary-labor wait | Fire need/unsafe-fire policy | Compose wait/hold without inventing forceable jobs. Observe extinguished fires; large/unknown fires retain hold. |
| `MaintainStoneShell`: `wall_upgrade.method` support/build/deconstruct/reconcile | Stone targets/exact construction ownership | Staged replacement actions with temporary support. Observe stone replacement and retained shelter before support release. |

**d — equipment/research/replenishment (05.5).** Equipment/power are fixed
priority producers. `colony_controller.cycle` refreshes resource goals and delegates
research to `research.refresh`. Medical reserves are the eighth upkeep contract.

| Reference goal / alternatives | Existing Go | Missing composition and observed postcondition |
| --- | --- | --- |
| `MaintainEquipment`: `gear_upkeep.compile_method`, existing gear first, preserve active bill, bounded one-item recipe | `policy/gear.go` proposals | Equip/wear with a, workshop bills and ingredient/labor accounting. Observe exact equipped output; preserve material/forced-gear policy. |
| `EnsureResearch`: active-goal unavailable ThingDef/RecipeDef prerequisites, bench then project, native-research wait | Definition availability/building execution | Typed queue, prerequisite selection, research action and service wiring. Observe native progress/completion; cancelled/adviser goals cannot request work. Later development-tuple branch in `compile` is shadowed by earlier research delegation. |
| `EnsureBasicPower`: connect/generate, solar-flare wait | Power policy and shared power construction | Reconcile topology composition and simulation wait; observe powered service/capacity and reuse applicable evidence. |
| `MaintainResource-*`: progress/prerequisite refresh, sources before recipes, material storage, pending work/existing bill wait | Resource rules/forecasts/building accounting | Dynamic targets, acquisition/mining and bills with exact excavation progress. Port delegated extraction/facility/refrigeration prerequisites at their consuming method. Observe replenished stock, not pending yield. |
| `MaintainMedicalReserves`: `medical_reserves.reserve_method` delegates remaining herbal deficit | Medical reserve need/hysteresis | Same resource production path; better usable medicine reduces deficit. Observe usable reserve recovery without changing care policy. |

**e — management/recovery (05.6).** Dynamic producers extend the fixed priority
list. Containment/feed are the final two of the ten upkeep contracts.

| Reference goal / producer / alternatives | Existing Go | Missing composition and observed postcondition |
| --- | --- | --- |
| `EnsureMood-*`: `mood_control.assess/priority_nodes/method`, bounded relief or wait | Mood history/proposal selection | Dynamic goal and relief action; observe actual need recovery and interruption, not forecast mood. |
| `MaintainMedicalCare`: fixed priority producer, permitted Patient/PatientBedRest settings then monitor | Care history/need (`policy.ReviewMedicalCare`), generic Patient/PatientBedRest priorities already default-enabled by `policy.AssignWork`, and the goal is now marked `MethodUnavailable` (monitoring only, no dispatched action) | Reuse a treatment (composed) and b settings-write dispatch once that shared work-priority write action exists; preserve NoCare/player overrides. Observe rest/health without claiming chronic conditions cured. Requested surgery shares e care but is player-created; verify exact patient/body-part health change separately from bill disappearance. |
| `Population-*`: `population.refresh/compile_method`, rescue/capture/release/recruit then integration | No composed producer/method | Durable commitments/custody settings and capacity/native guards; reuse a/b/c/d prerequisites. Observe custody/recruitment and food/housing/work/equip integration. |
| `MaintainHerd-*`: `husbandry.refresh_husbandry/husbandry_method`, configuration/training/slaughter and `update_feed_goal` | Animal observations/upkeep policy | Dynamic herd policy/actions, handler skills through b, owned feed goals through d. Observe herd/training/stock and cancellation; retain release/slaughter exclusions. |
| `MaintainAnimalContainment`: suitable-pen handler wait, otherwise fence/gate then marker | Animal containment need | Bounded pen construction/handler prerequisite. Observe animals contained, not fence completion. |
| `MaintainAnimalFeed`: discovered diet-compatible feed then resource production or wait | Feed forecast/hysteresis | d/b target production/staging; observe reachable rot-aware runway after competing eaters. Inaccessible stock cannot satisfy it. |
| `MaintainWaste`: controller creates/refreshes from native waste; `waste_management.compile_method` | No composed method | Typed policy/need and exact containment action with storage/grave prerequisites. Observe later exact relocation/burial; absence/merging is not disposal. |
| `RecoverDisasterServices`: `disaster_recovery.reconcile/prioritize`, `service_recovery.compile_method` | Disaster history/bounded recovery candidates | Service jobs, non-widening existing-area admission and owned cleanup; refuel/repair/breakdown restoration and changed-prerequisite retries. Observe actual restored service. |
| Trade policy: explicit shared trade action, not a fixed priority/compile branch | No composed action | Guarded session/preview/accept and economic floors through Hands; observe exchange stock separately from delivery/storage. Do not invent a routine trade goal. |

**Shared 05.7 checks.** Reconcile `colony_controller.cycle` priority/age arbitration,
`spatial_program.stage_layout`, construction capability retries, medical refusal
inputs, upkeep retry signatures/windows, resource/service prerequisite refresh and
no-progress watchdog/recovery with Go review, goal/method store, shared reservations
and worker lifecycle. Cover every wait above, competing projects, unknown facts,
uncertain replies, renewed deficits, cancellation, Manual, world/direction changes
and disabled same-database restart. Assert zero routine model calls. Existing
building fixtures do not establish the other families or native pawn outcomes.

**Outside a–e closure.** `ConfirmColonyNames` is a fixed priority/compile branch:
confirm the exact observed window once, then wait; its action/readback is G01.08.
Caravan, quest and settlement progression/dynamic work belong to G01.07f. Player
surgery/trade are counted in e without pretending they are fixed routine producers.
The unsupported-goal branch must fail closed. Startup Allow/settings are owned by
b; exact worker cleanup by a, with reuse by their later consumers.

- [ ] **G01.07f — World progression works through Go.**
  Port caravan packing/departure/routing/arrival/return/storage, quests and rewards,
  settlement gifts, failure recovery and multiple active maps. Preserve supply and
  home-staffing checks, expedition policies and stale-map rejection. Reuse existing
  Python world-progression scenarios after dependent management paths work.
  **Exit evidence:** native departure, arrival, reward/return storage and failure
  recovery with Go owning the workflow and no wrong-map writes.

### Remaining player and production delivery

- [ ] **G01.08 — Existing player commands and local-model chat work.**
  Use `player_commands.py` as the command list: policies/goals/resources, research,
  population/surgery/herds, trade/world commands, build/adopt/relocate/cancel,
  zones, work priorities, bills/temperature and pawn draft/move/tend/rescue.
  Reuse the local transport and building interpreter; finish typed interpretation,
  consultation/scout/visual review, knowledge/memory/evidence retrieval, streaming,
  deduplication, cancellation and explicit unsupported-command errors. Implement
  naming confirmation and existing UI operations through the appropriate shared
  control boundary. Accepted commands use the same plans/Hands as routine work.
  **Exit evidence:** representative scripted invalid/cancelled replies and actual
  configured LM Studio requests execute supported commands; advisers cannot mutate
  the game and there is no paid-provider fallback.

- [ ] **G01.09 — Player controls, media and save/load replace Python services.**
  Finish controls for the ported families, action hold/observation-failure reasons,
  camera/input ownership, portraits, follow, video and recording/diagnostics.
  Preserve drafts/last-good data, reconnect and competing-viewer behavior. Finish
  trusted save/load against the attached game: drain writers/owned resources,
  verify pause and reject stale direction or foreign instances before native
  lifecycle operations. Reuse current read APIs and building/draft/clock controls.
  **Exit evidence:** rendered/input outcomes and same-session save/load/reconnect
  with no Python media/control service and no stale action reasons.

- [ ] **G01.10 — Integrate the complete Go controller.**
  Compose the above paths in one process with clock, recovery and diagnostics;
  reconcile responsibilities against current Python source and domain/interface/
  state inventories. Inventory labels alone are not evidence. Resolve the known
  `TestClockWorkerTransportBlockedWriteRetainsOwner` stop/deadline synchronization
  issue while preserving blocked-write ownership and joined shutdown assertions.
  **Exit evidence:** fresh startup, ordinary colony work, player interruption,
  world/load changes and restart with one Go writer and no per-operation Python
  fallback. Reuse applicable family evidence; this is not another full migration.

- [ ] **G01.11 — Produce runnable Windows and Docker Go packages.**
  Replace production build/install/launch/configuration paths and scenario adapters;
  include dashboard/assets/media dependencies, loopback access and private profiles.
  **Exit evidence:** runnable install/image without production Python; identify
  retained Python development/scenario tooling explicitly.

- [ ] **G01.12 — Accept and switch the production default.**
  Run applicable automated checks and targeted combined native/model acceptance
  after composition/packaging. Cover startup, ordinary pawn work, player/chat
  controls, interruption and same-session restart; reuse unchanged evidence.
  **Exit evidence:** the functional Go controller is the default in production
  launchers and documentation. Historical byte parity, performance campaigns and
  unfinished B-series features are not rewrite gates.

- [ ] **G01.13 — Remove the replaced Python production runtime.**
  Remove replaced `controller/rimgovernor` code, production entry points, duplicate
  runtime implementations and runtime-only dependencies. Relocate required assets
  first; retain notices, useful fixtures and explicitly named development tooling.
  Update setup, architecture, source map, troubleshooting and CI.
  **Exit evidence:** every supported production path starts and works without a
  Python interpreter; launch/API/chat/media paths cannot silently invoke Python.

### Verification discipline

Use fast reference/replay tests for policy branches and one complete targeted
native workflow when execution is connected. Verify native work, not receipts.
Manual, cancellation, unknown outcomes and restart follow relevant changed code;
reuse unchanged evidence instead of repeating a full campaign per planning record.
Keep failed runs, exact source/binary identity and scope in generated artifacts
and commits. Do not run local `go test -race`; ordinary Go uses `GOMAXPROCS=2`
and `-p 1`. Use one bounded Docker worker.

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
