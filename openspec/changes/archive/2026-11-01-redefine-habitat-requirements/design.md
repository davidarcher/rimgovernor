# Design

## Context

The maintainer approved the proposal and habitat draft and requested a complete
implementation plan on 2026-10-26. See [proposal](proposal.md) for motivation and
decision provenance. This design was inspected against `105100df9`; inspection is
not a gameplay pass or evidence that all 11 requirements are implemented.

There is already one colony census, a saved `LayoutPlan`, a coupled activity
graph/comparator, planned-site native probes, service-day room credits, a Method
journal and Hands. Normal room selection compares construction against other work.
`ColonyReview` also compares whole tiled layouts before adopting one. These are
the integration points, rather than a new habitat Family or a facility scheduler.

Older facility paths still select definitions and work before that comparison.
Keeping those selectors beneath a new comparison would preserve their omissions.

### Requirement-by-requirement implementation audit

Paths below are relative to `brain/src/RimGovernor.Brain.Policy` unless qualified.
All support statements describe inspected paths, not exhaustive campaign evidence.

| Requirement | Existing path and inspected support | Gap to implement and completion evidence |
| --- | --- | --- |
| 001 Accommodation | `SleepingUpkeep.cs` observes exact-bed use, access and temperature; `SleepingMethod.cs` assigns/builds; `LayoutBedMerge.cs` and room credits price barracks; runtime stand-ins supply first shelter. Prison counts and animal pens/wards enter station demand. | Guests with no vacant bed disappear from sleeping targets and are excluded from bed construction; `StationFacts` sizes ordinary sleepers from colonist count. Prison credits focus on recruitment, not every held prisoner's functional accommodation. Animal containment and a medical ward do not establish species-appropriate resting shelter. Integrate all occupants, legal bed use, temporary provision and shared capacity without counting them as workers. Test guest shortages, non-recruited prisoners, animal climate exposure and permanent barracks through normal selection/binding. |
| 002 Essential operation | `PowerMethod.cs` separates generation, storage, fuel, repair and blackout; shelter and upkeep read current service; native operations and postconditions exist. | `HomeUpkeepFamily.Propose` explicitly holds ordinary repairs and door-to-wall work. Refuel/repair holds do not by themselves select the required worker chain. Compare exact servicing and alternatives; prove observed output/condition closes the Need, not a receipt. |
| 003 Useful development | `PlannedRoomCredit.cs`, `PlannedSites.cs`, planned construction and room sharing already price materials, service days, functions and connectors through the shared portfolio. | Choice is limited by what station and facility selectors offered; a priced room is not evidence that every equipment, site and retention alternative competed. Extend candidate generation and retain existing room-credit/conservation tests; test omitted alternatives and complete-chain costs. |
| 004 Care locations | `Hospital.cs` can convert beds; native bed-use operations validate medical capability. `ForecastHospital.cs` models several room/bed effects; support work offers exact tend/surgery at current locations. | Hospital conversion requires surplus non-medical capacity, including a patient's own bed; beds must be hosted and use a fixed ladder. Support contract explicitly lacks a joint patient-bed retention obligation for a standing patient. Field placement, transport/retention, transfer timing and restoration of bed use are missing as compared chains. Hospital findings explicitly omit standing medical beds, linked facilities, some effects and non-colonist surgery. Implement required location effects and execution, not an assumed ideal hospital. |
| 005 Complete facilities/sites | `FacilityPlanningChoices.cs`, `Workshop.cs`, station advice and native placement supply usable starting points; party and ideology demands exist. | Butcher spot/table retirement and construction are preselected; workshop reuse/order and `BenchCap` can hide useful alternatives. Party/comfort prerequisites and activity-site selectors are not proof of whole alternatives. Generate reuse, spot, table and bridge chains with exact sites; include activity sites without imposing a workstation lifecycle. |
| 006 Agriculture/animals | `LayoutFieldPlanning.cs` checks observed soils, crop adjacency and connected edits; station/layout policies share land; herd facts include pen suitability, pasture and roping; `ForecastPens.cs` values avoided roaming losses. | Field placement takes the first preferred crop/patch rather than comparing complete land alternatives; avoiding roaming does not establish feeding access or thermal/rest suitability. Compare selected crop/herd alternatives with their usable sites and shared land, including supporting work, without implementing another crop/herd objective. |
| 007 Improvement/reserve | Existing room, wall, floor, clothing and station comparison can value improvements, including future events; replanning revisits demand. | Guest omission, workshop population caps and current-count medical sizing limit preparation. No inspected general path compares prospective facility capacity with temporary alternatives and shortage consequences. Feed known arrivals, event-driven medical demand and seasonal/production changes into the same candidate/effect model. Computational search bounds remain distinct from capacity quotas. |
| 008 Resilience | `PowerBudget.cs` separates daily generation from storage; `LayoutPowerDerive.cs` sites sources; thermal models already price shells/airlocks. | `DefaultPowerPlanning` sets one reserve day and 25% storage margin; `SelectPowerMethod` and generator tiers choose locally. Service failures are not compared by their individual downstream effects or shared failure exposure. Replace policy quotas/ranking with complete source/network/fuel/passive alternatives and causal service effects; retain physical energy equations. |
| 009 Defense | `DefenseTiers.cs`, `DefenseBudget.cs`, `LayoutDefense.cs` and runtime defense layout produce legal sites, gates, cover, firing lines and wiring. Combat and encounter prediction supply separate threat/effect machinery. | Geometry starts from a killbox and prescribed perimeter/tier structure; capacity-in-raid-points and local gates do not prove costed protection. `DefenseBlock.cs` explicitly omits relocation downtime. Generate whole threat-relevant retain/cover/retreat/wall/equipment alternatives, compare staffing, inputs, routes, civilian exposure and service interruption, and bind only the selection. |
| 010 Redevelopment | `LayoutReplanTiled.cs` retains held/in-use rooms and successors; `ClearanceGround.cs` phases retirement; native uninstall/install/deconstruct and roof-safety paths exist. | Held geometry alone does not price the transition; dropped rooms feed clearance, and spot retirement is automatic once another bench exists. Repairs are held. Offer retain/repair/repurpose/store/remove/move as applicable; select a transition with observed replacement service before dependent removal. Price lost defense and live power during movement too. |
| 011 Functional outcome | Sleeping-use history, total room census, native legality/access, owner predicates and journal receipts already separate several outcomes. `ServiceTarget` refers to existing Needs rather than another goal store. | Guest shortages can be reported as no demand; count-based hospital availability and nominal pen/site provision can conceal unusable capacity. Define owner-specific completion from current facts for each newly supported path; keep required unread facts and true empty observations distinct and test load/rewind and partial completion. |

### Existing regression and acceptance seams

Reuse `SleepingPlannerTests`, `SleepingStandInTests`, `RoomSharingCreditTests`,
`PlannedRoomCreditTests`, `PlannedRoomActivitiesTests`, `HospitalCreditTests`,
`PowerMethodTests`, `FacilityPlanningRulesTests`, `HomeUpkeepFamilyTests`,
`UpkeepChunkRetirementTests`, `DefenseTierNodesTests` and field/layout tests.
Some currently assert the behavior being replaced: notably the home-upkeep holds
and butcher-spot retirement precedence. Replace those assertions, not the tests'
underlying legality, conservation or ownership guarantees.

Existing native seams include `layout/first-bedroom-closes`, prepared cold and
thermal cases, `power/fuel`, `upkeep/home-coverage`, `recovery/service`, medical,
husbandry and custody cases. They prove only their assertions when executed; an
explicit native command or fixture-selected assignment is not autonomous choice.
New cases must exercise the normal Family/comparison/admission/Hands route for
the changed claim. No new native result was obtained for this design.

## Goals / Non-Goals

**Goals:** Remove premature facility winners, price complete feasible alternatives
in the existing shared comparison, and carry the selected obligations through
admission, execution, fresh readback and reconsideration. All 11 requirements and
their approved scope remain in the task set; partial milestones do not complete
the change.

**Non-Goals:** Add a habitat authority, planner negotiation protocol, second demand
ledger, colony save, remote model or replacement tactical engine. This change
does not choose new dietary, prisoner recruitment, herd-size or ritual-purpose
policy. It provides the spatial and service alternatives those domain decisions
need. Legal game operations and explicit control authority remain authoritative;
construction origin never grants an ownership exemption.

## Decisions

### 1. Extend candidate generation; do not wrap existing winner selectors

Family providers produce provisional alternatives for existing Needs, retaining
their beneficiaries and physical targets. Spatial policy expands them into
bounded feasible arrangements, using the existing station graph, tiler and site
probes. A complete alternative describes the usable function, prerequisites,
resource inputs, participant obligations, timing and projected changes to service.
Retaining or deferring existing provision is a real baseline alternative.

Reuse the existing forecast activity graph, planned sites, room functions and
resource accounting. Extend their typed variants where a new physical effect or
operation is required. Do not make every provider implement every alternative,
and do not create a generic opaque bag of arbitrary facility attributes. Native
legality and measured capability determine which candidates exist.

Reject the alternatives of each Family privately choosing location, or a central
habitat planner choosing every function: the first duplicates spatial decisions;
the second must recreate domain policy. A request for one preselected object is
also insufficient because it excludes the better spot/table/location combination.

### 2. One coherent frame, measured mechanics and explicit prerequisites

Extend `GameFacts` observed records and `GameOrders` probe requests only for facts
the alternatives actually consume. Native probes must read game-computed
placement, worker work/delivery rates, room and bed effects, routes, source
profiles, refuel/repair mechanics and applicable defense effects. Reuse canonical
pawn/building/room rows, joining references instead of copying native observations.
Use compiler-checked defs and typed members; confirm unfamiliar APIs against game
assemblies before the native build.

Prospective capacity uses already observed commitments, native conditional event
timing and seasonal/production demand. It does not invent an arrival rate or
arbitrary probability to award reserves value. A source with no supported delivery
model cannot receive guessed output. Required missing facts prevent assessment of
that option, with a named reason; known absence or zero benefit is a different
state. Unrelated fully assessed alternatives remain eligible.

Construction, fetching inputs, installing, connecting, switching/configuring,
patient movement, occupancy and service use are explicit dependencies. Furniture
in an unpowered unfinished room must not gain its fully functioning benefit.

### 3. Shared comparison owns both readiness and downstream benefit

Extend the existing coupled comparator and Investment term, not a new facility
score. Charge each material, worker interval and shared prerequisite once; add
effect only after that schedule's usable service time. Include the opportunity
cost of other occupants/functions displaced by the alternative. Existing
functional eligibility and safety floors remain; decoration cannot make an
inaccessible bed usable. Do not add universal comfort or reserve floors.

The existing bounded layout search is deterministic and uses no model-service
calls. Retain explicit computation bounds, candidate-qualified native probe IDs,
stable ordering and cache ownership within the current review. Generate distinct
physical alternatives before bounded search rather than evaluating the same
winner repeatedly. Report truncation and omitted required mechanics; never turn
a search bound into a colony-capacity quota or claim exhaustive optimality.

### 4. Occupancy is a resource, not a second population census

Derive accommodation demand from current people, custody, guests, children and
animal rows plus known commitments. Preserve their domain distinctions and legal
bed/containment rules without adding guests or prisoners to worker availability.
Capacity is compatible, reachable slots over time, not just a global bed count.
Beds shared by compatible sleepers and beds temporarily used for care cannot be
reserved twice. Medical conversion must account for the patient no longer
requiring a simultaneous independent regular slot, while protecting displaced
occupants. Reassess assignments after medical use rather than persisting another
bed-owner restoration store.

Generate bedside care, transport to existing care capacity, field stabilization
and later transfer as complete medical alternatives. Bind exact patient/bed/doctor
obligations through the existing journal. Extend native support operations for
movement/retention and immediate spot configuration if the current variants cannot
express the chain. Do not claim that merely queueing a treatment makes its patient
remain in the chosen bed. Surgery reads actual bed/room/facility factors, including
non-colonist cases where required; a room's Hospital label is not a prerequisite.

### 5. Utilities and defenses serve effects, not fixed designs

Keep energy-balance and native thermal mechanics. Generate network routing,
generation/storage, switching, fuel supply, repair and passive thermal alternatives
and propagate service availability into their consumers' effects. Replace reserve
duration/margin and generator preference tiers as choice policy; do not remove
physical constants or computational geometry bounds merely because they are fixed.
Existing forbidden/off switches are observed state to consider changing under
Auto, not evidence of player ownership. Manual/control authority still governs
whether any order may execute.

Protection supplies threat scenarios and expected tactical/civilian effects.
Shared spatial policy generates feasible defense geometry including retention,
temporary cover/retreat, barriers and equipment. Existing killbox/perimeter/tier
builders become candidate generators where useful; they cannot impose the only
admissible design or independently admit their winner. Price staffing, native
ammunition/fuel/reload needs, power and ordinary access along with protection.
Extend the existing prediction boundary as necessary for changed geometry; do not
invent raid-point-to-safety guarantees or reintroduce a combat order bypass.

### 6. Redevelopment is an executable transition

Extend planned-room reconciliation and clearance with selected alternatives for
repair, replacement, relocation, repurposing, storage, retention and removal.
Offered choices depend on actual native operations; non-uninstallable equipment
has no imaginary pack/install alternative. Continued service, lost service,
recovered material and changed walks belong to the same schedule.

The plan records selected spatial intent and any durable transition dependency
it needs; the Method journal records attempts/claims, the game owns physical
buildings and assignments. Replacement service must be observed usable before
removal that depends on it; forecast readiness is not enough. Existing in-use,
roof-safety and held-room guards protect interim execution while useful relocation
is priced, rather than permanently vetoing change. A lost replacement reopens the
dependency. Never automatically clear idle or dropped geometry just because a
new layout omitted its function.

No new store is proposed. Amend the existing `LayoutPlan` entry in the persistence
table before extending saved transition intent; derive all other trial state in
memory and use existing journal actions for reservations. Delete superseded
fields and codecs through every layer; no compatibility shim for old fixtures.

### 7. Outcome predicates remain with the original Needs

Binding resolves the selected alternatives into normal actions without selecting
again. Runtime obtains frames, batches probes, adopts guarded plans and submits
work; it contains no valuation math. Store/Safeguards arbitrate exact overlapping
claims and resources atomically; Hands dispatches and reconciles writes. Fresh
observations close accommodation, treatment, utility, containment and construction
predicates separately. Finishing a bed build can close a construction action while
the occupant's accommodation remains unmet.

Reconsider at safe observed boundaries and on material changes, preserving open
work and uncertain receipts. Load, rewind and authority changes invalidate old
execution evidence. Extend existing kinded selection/outcome telemetry and its
reader where new effects are material; register any genuinely new kind. No second
completion tracker or unkinded diagnostic stream is needed.

## Risks / Trade-offs

- **Large cross-cutting implementation:** removing several independent selectors
  exposes missing native effects and execution operations. Implement vertical
  milestones with tests and contracts, keeping all remaining tasks open; do not
  declare the change complete after the common abstraction or first consumer.
- **Search growth:** complete equipment/site/transition combinations can multiply.
  Reuse bounded shared search and batch exact native probes; preserve distinct
  bridge, retention and no-change choices and expose truncation.
- **Double counting:** an existing medical bed or shared generator can appear in
  several alternatives. Use canonical identities, slot/interval occupancy,
  dependencies and the shared resource ledger; test paired competing consumers.
- **Unqualified forecast benefit:** current hospital findings and defense downtime
  omissions can overvalue work. Add the required observed mechanics and tests
  before pricing those effects; a permanent unsupported hold is not completion.
- **Conflicts with older contracts:** fixed reserve policy, colonist-only sleep,
  surplus-only conversion and automatic retirement are documented current code,
  not exceptions to the approved spec. Update their owning contracts together
  with each implemented behavior and preserve historical evidence as historical.
- **Native evidence cost:** a new choice needs a narrow staged claim. Use existing
  cases when sufficient; keep new cases about a minute, stopping on the claim.
  No campaign, repeated full combat sweep or raised stall budget is required.

## Migration Plan

1. Land this complete audit/design/task milestone with strict OpenSpec validation,
   checked links and the repository gate. Approval of requirements is not execution
   evidence; task checkboxes remain open until their behavior and tests exist.
2. Implement and land the dependency-ordered vertical milestones in `tasks.md`.
   Each replaces its earlier selector when its normal end-to-end path works and
   deletes unused facts/helpers/tests in the same milestone. Run the required gate
   per finished milestone; do not repeat it solely because main moved.
3. Update spatial, population, medical/support, power, upkeep, husbandry, forecast,
   defense and persistence contracts with the implemented ownership/behavior. Keep
   current navigation in `docs/README.md`; keep intent separate from evidence.
4. After every tracked obligation is implemented and verified, synchronize the
   approved habitat capability, preserving RG-HABITAT-001–011, and make the old
   requirements page the evidence owner. Update the requirements/OpenSpec maps and
   context to identify the standing authority. Do not archive before completion.
5. Run the epic-end full gate and report actual policy/boundary evidence separately
   from registered or executed native cases. Roll back a faulty milestone through
   the landing lane while retaining the approved requirements and honest open tasks;
   do not restore removed save shims or introduce a parallel behavior fallback.
