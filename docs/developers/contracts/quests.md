# Quests

[Subsystem contracts](README.md) · [Action completion](action-contracts.md)

The world-progression census carries typed objectives, reward choices, eligible
accepters and offer expiry. Missing objective values remain unknown. Royal favor
outside a choice part applies independently of the chosen reward index.

World sites expose typed lifecycle state, planet layer/tile, associated visible
quest IDs and threat presence. Hidden site parts leave threat unknown. Loaded
maps use the native active-threat predicate. Distance is geographic distance,
never travel duration. Surface route estimates use the native path and arrival
estimator at full carrying mass and the slowest singleton of the observed eligible
crew; only subsets of those crew identities may use that bound. Layered routes
remain unknown. Formation crew and caravan destination are derived live reads.

MechanoidSignal inspection names its exact quest look-target grav engine, its
spawned state, eligible and already-inspecting colonists, and the native global
gravEngineInspected flag. Missing targets hold the driver until the chunks land.
The existing GiveJob intent runs InspectGravEngine on that engine; it leaves
forbidden gravlite panels unchanged. An order receipt never proves inspection.

The definition catalog classifies explicit Core and DLC roots into quest families.
Unknown roots remain unknown; automatic and incident-driven roots are observed
without an acceptance decision. Endgame offers and deferred Anomaly progression
are refused. SurveySite reserves food for its full native hold and return, prices
the forced raid, and retains a separately affordable healthy relief crew. Only
native scanner completion ends the hold; scanner destruction ends it as failure.

MaintainPopulation selects joiners first, then allowed bestowing claims, then an
affordable family offer. Feasibility uses the existing work roster and emergency
decision, protects sole doctors, cooks and builders, and reserves spare capacity
against open non-automatic quests. Departures retain at least three home colonists.
Unknown demands and unfinished objective drivers prevent acceptance. Native
CanAcceptQuest remains the final eligibility check at dispatch.

Timed work uses native workloads and observed worker rates, with shared budgets
for open quests. The forecast counts complete days, at most eight scheduled
Work/Anything hours per day, and half that time for useful work. Unknown work
or rates hold admission. Monument offers also require a legal clear footprint,
reachable materials after existing commitments, and enough defense for their
protection threats.

ThreatReward joiners require a calm colony and observed defense capacity covering
the generated quest's incident budget. PawnLend and BanditCamp departures preserve
every work type's last primary owner, healthy adults, home population and home
defense. Pending, loaded and lent pawn identities prevent overlapping squads.

Deadline admission uses native effective pawn work rates and canonical recipe or
sketch work, not skill estimates. It budgets complete days before the deadline,
at most eight scheduled Work/Anything hours per day, and half that time for useful
work after walking, hauling and upkeep. Open nonautomatic work shares each pawn's
budget. Missing rates, schedules or workloads hold admission; offer expiry is
separate from the duration available after acceptance.

Monument offers expose their generated drop-pod sketch before acceptance. Admission
requires native legal footprint placements, reachable material stock plus supplied
pod material after existing construction and quest reservations, and enough build
time. TimeProtect also needs observed defense capacity covering its native threat.
The execution path rechecks placement rules before ordering construction.

Required accepters come from the native eligibility list. Lower aggregate skill
value is preferred; royal rewards prefer an eligible existing title holder.
Reward selection prioritizes favor needed for pending titles, observed material
shortages, then useful benefits. Universal rewards apply to every choice; multiple
choice parts are refused because the native acceptance intent selects one index.
Each refusal is a stable reason in a deduplicated quest decision under
`routine_skip`, with quest and script identities in attributes.

Quest shuttles use one intent: toggle vanilla autoload where offered, order an
explicit pawn list into the transporter, or request launch once requirements are
loaded. Native validates quest ownership, map, eligibility and departure
conditions. Loading and launch-request receipts prove instructions applied;
native boarding, departure and quest success establish lifecycle completion.

Active decrees use native crafted, harvested and killed counts. Production bills
retain exact product and stuff requirements; stored products do not count as
crafting. Harvest demand counts plants, and hunt orders target the required wild
species only when violence is permitted. Existing queued work keeps the objective
active until native progress changes.

Monument objectives expose the live marker, sketch footprints, native placement
permissions and eligible loose-resource haulers. The existing packed installation
intent places the marker; shared building admission handles its sketch. Queued
blueprints remain work in progress, and the intact native sketch determines
completion and maintenance during the keep period.

Pod-refugee objectives name the native crashed pawns. Existing custody rescues
neutral refugees and secures hostile ones; housed patients use the ordinary tend
job, and held prospects use the existing recruitment policy. Ghoul pods retain
their threat treatment. A native PlayerTended event completes the charity quest.

All quest facts are derived observations. No quest-specific store is added.

World sites retain exact quest associations, lifecycle state, layer and loaded map.
Unknown or hidden threats remain unknown. Surface expeditions use native route
estimates bounded by the slowest eligible pawn at full carrying mass; the chosen
crew must belong to that estimate. Shared departure staffing preserves home work
owners and defense. Food packing covers the round trip and leaves the diet-aware
home reserve intact; vanilla formation checks remain authoritative. Existing
formation, travel and loaded-site facts prevent sending replacement crews.

Site admission requires a complete native security bound, including dormant
defenders, delayed attacks and complex triggers. Unsupported generated threats
and underground entrances retain unknown risk. Peace talks send the best eligible
social negotiator only when native goodwill downside is affordable. Survey crews
pack for the scanner's full native duration and preserve an independently affordable
relief crew; elapsed time alone does not finish the hold.

Core rescue sites use exact native pawn identities and vanilla OfferHelp for
willing joiners. Mineral sites designate observed mineables and wait for their
disappearance. Cleared sites reform through vanilla with explicitly retained
inventory, return food and mass-bounded loot. Failed site fights walk surviving
pawns to native exit cells. A stopped caravan returns only when its crew matches
a journaled expedition; moving caravans retain their route. Existing journals
and derived site facts own this lifecycle.

MechanoidSignal uses the exact quest grav engine and vanilla InspectGravEngine job.
The planner waits for spawning and running inspection, then observes the native
inspection flag. Gravlite panels retain vanilla forbiddance. Shuttle-crash rescue
admits the observed hostile arrivals against defense capacity and boards named
survivors through the shared pickup lifecycle.
