# Power contracts

Pending power demand accepts nonnegative finite wattage without an arbitrary upper ceiling. Nonfinite and negative values fail planning validation.

The power family's `power/basic` Need (`PowerFamily.cs`, `PowerMethod.cs`, `PowerBudget.cs`, #3257) keeps every enabled
consumer powered by reading the native power census and sizing each network against a day of energy
and comparing the answers to a shortfall by the interruption they cost,
never by counting generators. Native placement previews and shared admission gate every build; output
plus `PowerOn` on the consumer, not a receipt, establishes recovery.

Building previews retain the complete native placement evaluation and selected
orientation. Blockers, watch access, stuff capability and wind obstruction come
from those same rows; admission safety and the combined occupied/interaction
footprint remain derived. A planned zone has an explicit intent preview. Refusal
reporting may filter an already-present wanted blueprint locally, without
rewriting the native blocker evidence.

The canonical `BuildingState` owns every building's identity, occupied rectangle, service state and settings.
Its distinct power facts include rated watts (the native `PowerConsumption` property applies finished research
upgrades), battery capacity, roof coverage, rain sensitivity, accepted fuels and optional turret gun.
Policy reads these same rows directly; its topology is a reference view with native network and geyser records.
The refuelable service state also owns its current difficulty-adjusted burn parameters; fuel runway reads
that same building row. There is no second development-power or fuel-consumer row, or copied planner-side service state. Network reserve runway,
coverage and energy budgets are policy calculations over those observations.

## Deficits

A network is *short* when its 24 h energy balance leaves energy unserved (`UtilityChoiceRules.UnservedWD`: the day's
draw its live producers do not cover, and the night's draw beyond night generation that a bank filled from the day's
surplus at the charge efficiency does not cover; an eclipse zeroes solar producers) or when one of its consumers is
switched off or unpowered. A network the balance covers needs nothing; an unpowered one whose bank or native output is
still to come waits (`waiting_for_charge`, `waiting_for_native_power`). There is no reserve-days floor and no storage
margin: the balance alone says how much energy goes unserved, and the comparison below prices it.

- The colony orders switches under Auto, whoever set them ([configuration](#configuration)). A forbidden or
  switched-off consumer or producer is not player intent to preserve; the only veto is the player's preferences
  (`Preferences.ProtectedThings`, `PowerPlanning.Protected`), whose switches are never ordered and whose switched-off
  members draw nothing the network must serve. `PowerMethod.WaitPlayer` and the `player_disabled_power` wait are deleted.
- An out-of-fuel or broken producer on a short network is a lost producer. It counts toward the
  network's capacity only while the [restoration comparison](#restoration) restores it; a network
  its restorable producers cover reports `restoring_producers` (`PowerMethod.Restore`) and adds
  nothing, and one they cannot restore is short and is answered by the [comparison](#comparison) as if the
  producer were absent.
- A `SolarFlare` condition holds every build (`solar_flare`, see
  [upkeep contracts](upkeep-contracts.md)).

## Restoration

A *lost producer* is a connected, switched-on producer that is out of fuel or broken down on a network where a
consumer is unpowered or the output is negative (`PowerRestorationRules.LostProducers`, most rated watts first). A
forbidden producer is lost like any other: who forbade it is no exemption, and a switched-off one is the
[configuration](#configuration)'s switch order first. The `power/basic` Need
compares each one every review through the shared maintenance choice (`ServicingChoiceRules`,
[forecast contracts](forecast-contracts.md#servicing-activity-read)):

- **Refuel**: carry stocked fuel to the empty tank (`RefuelWork`, priced from the pawn's carry
  capacity, the legs to the nearest stack and the 240-tick wait per load).
- **Fix**: the breakdown fix (`FixBreakdownWork`, one component, divided by the pawn's
  success chance). The fix is the `home-upkeep/repairs` Need's Method (one owner); power only
  counts it toward the restorable capacity.
- **Acquire, then refuel**: when the colony holds none of the fuel, the chain whose first step
  designates the harvest sources `AcquisitionRules.SelectResourceAcquisition` picks over the
  observed non-hunt sources and the yield already designated. Only the harvest path is
  modelled: with no observed source the state is the named `fuel acquisition:<def>`
  (chemfuel production and trade are not modelled), and a fix with no component in stock is
  the named `component acquisition:<def>` (no component path exists).

A condition-only loss (damaged but running) is not a power deficit: a damaged producer still
produces, so its repair stays `home-upkeep/repairs`'.

Methods: `allow-<producer>` (a `BuildingAllow` order for a forbidden lost producer the choice restores, ahead of
the refuel or fix, because the native refuel and repair givers never service a forbidden building; it claims
`forbid/<producer>`), `refuel-<producer>-<pawn>` (an exact `RecoveryService` refuel claiming its producer
and pawn; an issued one continues its open Method and a running native refuel is observed,
not reordered) and `fuel-<def>-<sha8>` (the acquisition, its digest over the sources it
designates, so the same selection continues). The fuel items a chosen or issued refuel
carries are held by the choice's ledger (census stock less issued and chosen refuels), so two
producers never count one stock twice; a second producer with only a partial stock left is
excluded `fuel_taken` (the stock is spent) and defers; an acquire-then-refuel chain is excluded `fuel_stocked` while any of its fuels is in stock. The ledger is policy state re-derived each review, not a
`MethodClaims` claim. Underway and refused orders come from the journal and the pawns'
running jobs (no store); a refused order spends its refusal budget and the next review
reselects.

The native auto-refuel threshold is read, not assumed: `BuildingFuel.Refuelable.AutoRefuelPercent`
(`Props.autoRefuelPercent`, 0.3 by default) gates whether native auto-refuel starts the work
now (`NativeStarts`) and `RecoveryPending`'s refuel entry. The brain still issues the refuel
order when native auto-refuel would also start it. The Need closes only on fresh facts
(output meets demand): an accepted refuel restores nothing by itself.

## Comparison

A short network's answers are compared by one function, `UtilityChoiceRules.Choose` (`PowerUtility.cs`), against
deferring (RG-HABITAT-008). The alternatives are `Configure` (switch low-value consumers off), `Route` (a conduit to
a network with surplus), `Generate` (each catalog generator, the steam plant on the nearest free geyser a measured
conduit path reaches), `Store` (one `Battery`), `Shelter` (roof the network's rain-exposed equipment) and `Passive`
(an unpowered thermal provision in place of a powered temperature control the thermal equation shows meets its
band). Each is `Assessed`, `Excluded` by a named constraint or `Unassessed` naming an unread fact.

Each is priced in capability-days over the horizon (`ColonyModelPolicy.DefaultHorizonDays`) by the interruption the
consumers suffer: its cost, then its lead time charged as interruption while it is built, then the share of demand
it still leaves unserved (the consumers it switches off lose their whole day) while its producers run, and the base
share after their runway. A finite fuel's runway (`GeneratorRow.FuelItemsPerDay`, the stock over the items burnt a
day) ends the producers' share. The share unserved is the net's own shedding: `PowerNet.PowerNetTick` sheds and
restores a random consumer at a time while the net is short, so each is off that share of the day in expectation.
A network's common-cause outage ([rain short circuit](#rain-short-circuit)) charges every alternative that does
not clear it, deferring included. Deferring is the baseline and wins ties; an unpriced deferral is chosen,
naming the fact, because nothing can be compared against it (`UtilityPrice.Unpriced`, wire
`interruption_deferred`, with each unassessed consumer's `service:fact`). A consumer whose loss is unassessed is
left out of every price, so the priced loss is a lower bound.

The consumer loss is `PowerValuationRules.Valuation` (`PowerValuation.cs`), each by the valuation its own
milestone owns:

| Consumer | Interruption | Source |
| --- | --- | --- |
| Cooler holding perishable stock | the spoilage its cooling prevents | `ForecastFreezer.SpoilageAvoided` |
| Workshop bench | the bill throughput lost unpowered, by the recipe-flow run value less labor | `WorkshopLoss`, `ForecastWorkshop.RunValueOf` |
| Medical facility | each queued surgery's loss at the bed's factor without the facility, charged once per pending surgery (`InterruptionLoss.PerDay.OnceDays`, at the share unserved when it is done) and nothing when none is queued | `CareLoss`, `CareCapacityRules.SurgeryLossOf` |
| Bedroom heater or cooler | the sleepers' mood leaving the comfortable band at the room's steady temperature without it | `SleepLoss`, `BedroomGainFacts.MoodGain`, `ForecastHousing.FlatMoodGain`, `ForecastShelterPolicy.SteadyTemperature` |
| Dated chain draw | the host's later step run at the bridge's base speed instead | `ProspectiveCapacityRules.ChainStepLoss` |
| Passive provision | its piece price plus its fuel over the horizon (`FuelItemsPerDay`), the room's steady temperature read from the thermal model | `PassiveProvision` |

Every other consumer is unassessed, naming the service: lamps and other consumers (`consumer service:`), turrets
(milestone 7), the immunity gain a patient loses and tend quality. A network of such consumers defers, naming the
fact. A piece (generator, battery, conduit, a shelter's wall, door and roof) is its work and cost list in its
cheapest common stuff (`LayoutClusterRules.PiecePrice`), ready after its work over the colony's builders
(`ProspectiveCapacityRules.PieceOption`); material delivery time is not read. Dated chain draws and pending
consumers join the demand only once `ProspectiveCapacityRules.Choose` prepares them
([forecast contracts](forecast-contracts.md#prospective-capacity)); a pending consumer's service is unassessed.

### Rain short circuit

`RainShortCircuitRules.Exposure` prices the network's expected rain short circuits from the native roll
([mirrored literals](kept-constants.md#mirrored-game-literals-rain-short-circuit-habitat-64)): every 97 ticks a 2 %
roll, then a pick among the colony's rain-vulnerable buildings with a `0.2 * N * RainRate` chance; an unroofed pick
short-circuits; a battery holding over 500 Wd then loses 400 Wd with 5 % chance. The event's interruption is the
stored energy it burns, as a share of a day's demand. `Exposure` is `None` without an exposed member and unassessed
while the rain rate is unread. Sheltering is one alternative in `Choose`, priced against the exposure it clears.
Known limits: the wind turbine's clear path is checked only at placement (`wind_blocked_cells` on the preview), there
is no breakdown rate, a network's dependence on a single producer is not priced, and material delivery time is not
read.

## Configuration

Switches are the colony's to order under Auto (task 6.5). A flick switch is observed as the building's typed
`switch` (`fixed`, or `flick{on, wanted}`; [wire observations](../../../contracts/wire/observations.md)) and written by
`PowerSwitch(thing, on)`, which sets the `CompFlickable` wanted state as the building's own power gizmo does, so a
pawn flicks it natively; a building without the comp is refused "Building is not flickable.".
`BuildingAllow(thing)` is the forbid gizmo's unforbid. Both are claimed per building (`switch/<thing>`,
`forbid/<thing>`), so neither contends with a refuel or repair of the same building
([action contracts](action-contracts.md)).

The planner (`SelectPowerMethod`, `BuildingPlanner.Power.cs`) derives per network each review:

- a switch that wants a state it does not hold waits for the flick (`waiting_for_flick`, `PowerMethod.WaitSwitch`);
  nothing is re-ordered;
- a network the balance serves with every consumer on switches its idle consumers back on, most valued per watt
  first, while the 24 h balance leaves nothing unserved;
- a short network's idle producers are switched on first (it costs nothing);
- a chosen `Configure` commits one `PowerSwitch` per building as one owner Method `configure-<key>` (the switches off
  first, then on, each in id order; wire `configure_consumers`); once the switches hold, the network reports
  `configuration_holds` (`PowerMethod.Configured`). The Method key is the network and the orders, so the same orders
  continue their Method.

An accepted switch whose building still reads unchanged is Unconfirmed (`power_switch_unconfirmed`): the same orders
are not re-issued blindly, the facts are re-derived, and the hold lasts until the order set changes; an open attempt
continues (`power_switch_method`); a refused one spends the refusal budget. A chosen `Passive` builds its unpowered
burner beside the control (wire `passive_thermal`); it does not switch the control it replaces off. Recovery stays
the observed `PowerOn` of the consumers left on: an accepted order establishes nothing. The player-origin exemptions
are gone from coverage, restoration and routing; the remaining player-origin skips are outside this scope
(forbidden refuel targets in disaster planning and a switched-off lamp in lighting).

## Energy balance

`PowerRules.GenerationShortfallW` (`PowerBudget.cs`) sizes the generation a layout plan owes a first plan's demand;
the runtime's balance (`UnservedWD`) is the same day shape over the same-network census rows.

- **Demand:** the wattage of every consumer the colony may switch (on or off) plus the declared draw
  (`observation.PlanningDefinition.PowerW`, the def's power comp) of every building action still open
  in other concerns' held reservations, so a workbench about to be built is priced before it turns on. A consumer
  the player's preferences protect counts only while it is on.
- **Supply:** each producer's nominal wattage spread over the day by its profile: constant for
  native `CompPowerPlant` and steam generators; a daylight curve for `CompPowerPlantSolar` (zero for a
  third of the day and throughout an `Eclipse`); for `WindTurbine`, `policy.WindProfile` over the
  catalog's `WindManager.WindSpeedRange` (0.04-2) and the `Clear` weather's `windSpeedFactor` (1):
  output is nominal times wind speed capped at 1.5 (`CompPowerPlantWind`, a game constant the catalog
  lacks), the speed taken as uniform over the range. That is 0.956 of 2300 W, 2199 W, matching the
  wiki's measured clear-weather average (snow 2622 W and blizzard 3409 W are higher, so clear is the
  conservative figure). The earlier 1200 W figure was about half the catalog's.
  Class matching is exact: an unfamiliar subclass cannot inherit a continuous-output assumption.
  Water flow, gravcore substructure, hacked reactors, electroharvester occupants and automatic
  power/pollution conditions have no delivery model here. Their catalog profile names the unsupported
  mechanics; comparison and energy budgeting refuse it instead of inventing continuous supply.
- **Storage:** the summed battery capacity. The wiki figures behind the profiles and the battery
  (600 Wd, 50 % charge efficiency, 5 Wd/day self-discharge) are physical constants in `PowerBudget.cs`; they stay
  because they are the game's equations, not a policy knob.

The generation shortfall is the constant wattage to add so that, after the day surplus refills a bank at charge
efficiency, the night is covered. A daytime deficit is generation outright; so is a night deficit the surplus
cannot refill at 50 %. The observed reserve remains the check on the model: a consumer that actually loses power
is a deficit whatever the balance predicted.

## Methods

- **`connect` (`HiddenConduit`):** the `Route` alternative. When the consumer's network has no producer but the colony
  has one, or a network with surplus can serve it, the whole route from the consumer toward the nearest producer that
  already outputs power (a producer switched off is its network's switch order first, never a conduit's end) as one
  proposal, over cells the site census reports buildable and outside protected geometry. There is no cell cap: each
  cell is its own native preview call, so no wire frame or shm slot bounds the route. Blocked routes
  report `no_observed_route`. Ordinary conduits are also replaced in one method because roofs do not
  prevent their random short-circuit event.
- **`generate`:** the `Generate` alternatives priced together; the cheapest wins and the priced runners-up after it
  travel as `Alternatives`, so a definition no site accepts yields to the next under the same method key.
  - A supported, natively available steam plant is an alternative whenever the development
    census lists a free steam geyser (`DevelopmentFacts.geysers`: `Building_SteamGeyser` cells,
    occupied when a harvester, building, blueprint or frame stands on it); its price includes the measured conduit
    path from the consumer to the geyser's footprint over buildable cells, in cost and lead, and no reach constant
    bounds it. The proposal is fixed-site (`FixedSite()`), previewed at the geyser's anchor without the site census
    (which reports the geyser cell occupied) and admitted on the native preview's legality and safety alone.
    Steam plants never enter free-placement pricing without a free geyser. Observed mechanics, rather than a
    geothermal definition name, identify fixed placement.
  - Every installed player-buildable ordinary generator whose native mechanics have a delivery model, natively
    unavailable ones skipped, is priced by its build cost and lead, the fuel runway of its stocked tank
    (`GeneratorRow.FuelItemsPerDay`) and the unserved share it leaves; no tier, watts-per-market-value ranking or
    grown-fuel preference orders them. A multi-fuel plant burns a stocked full-tank fuel first, then a
    growable one (`policy.ResourceGrowable`: a crop that harvests the fuel is sowable now, so a tree plantation
    counts and wild trees or chemfuel do not), then the greater stock, ordinal identity breaking ties. A
    stuff-made generator uses its cheapest allowed material, carrying that material through ordinary and geyser
    previews, alternative candidates and construction intent. Candidates require an available capable builder; cost,
    nominal watts and accepted fuels come from the catalog (`DefinitionCatalog.GeneratorRow`). All candidates join
    the ordinary planning census, research gate, pending-work correlation and room reconciliation.
  - A `WindTurbine` site is searched within twelve cells of the consumer and accepted only when the
    placement preview's `wind_blocked_cells` (native `WindTurbineUtility.CalculateWindCells`
    catch-zone cells that are roofed, off-map or hold a wind-blocking thing) is known and zero.
  - No available generator reports `no_affordable_generator` (every alternative to deferring is excluded), or the
    research gate that would make one available (the research ladder places `GeothermalPower` after `Batteries`).
- **`store`:** one `Battery` on a roofed cell within six cells of the draining consumer (an unroofed
  battery short-circuits in rain or snow): the placement search keeps only roofed cells. The layout plans
  no battery room and no battery site (#3814): an enclosing room changes no rain exposure, and the fire it
  would contain is fire harm, not modelled yet. A battery wins only where the balance prices it below the
  generators.
- **`shelter_power`:** a bounded enclosure around exposed vulnerable equipment, an alternative in `Choose` priced
  against the rain short circuit it clears; recovery requires the observed roof. See
  [electrical safety](../architecture/facilities.md#electrical-safety).
- **`configure_consumers`, `passive_thermal`:** see [configuration](#configuration).

Method identity is the target consumer plus the sorted producer ids (and, for `store`, the installed
capacity), so a repeated deficit replays the same method while a new producer or bank makes a new one.
Hands correlate completed work against `PowerFamilyDefinitions()`.

## Planned conduit net

A tiled layout plan (`LayoutTile.cs`) owns its conduit paths: `LayoutPlan.Conduits` is a list of
`ConduitLink{From, To, Cells}` derived with the plan by `RouteConduits` (`LayoutConduit.cs`). It is the
net the planner would lay; the `power/basic` Need still decides which sites connect, by which method and at
what budget, and the `connect` contract above is unchanged. No action kind carries it.

- **Refs.** `From`/`To` are tagged: `room:<id>` for a planned room, `site:<node id>` for a site: a
  turbine or solar plot site node (`NodeTurbine`, `NodeSolar`) by its node id, or a
  `geothermal` reservation, named `<kind>-<index in Reservations>`
  (`ReservationSiteID`).
- **Consumers.** Planned rooms holding a station whose def draws power (`Station.BaseW < 0`, read from the
  power comp into the station catalog); the cells are those stations' footprints, else the whole room.
  There is no role table.
- **Topology.** One net grows. The first power site is the root; each later site, then each consumer, routes
  from its own cells to the nearest cell of the net so far, cells shared. No star forms on any node. A node that cannot reach the net gets a `conduit_no_route` decision and no link;
  with no power site every consumer gets `conduit_no_source`; a routed link is `conduit_routed`
  (`TileResult.ConduitDecisions`, journalled as `layout_conduit` on a derive and on a replan).
- **Replan (#2966).** A replan routes against the links the colony holds (`TileInput.Conduits`): a node
  whose held link still starts on it, crosses only `ConduitCells` ground and ends on the net so far keeps it
  (`conduit_kept`); only the nodes a change broke route anew (`conduit_routed`). A room that replaces another
  (`Replaces`) or a site that replaces another (`ReplacesSites`) inherits its predecessor's link. A retired
  site or room has no node, so its path is not laid again. The rooms `keepHeld` puts back, which the tiler
  did not plan, join the net through `JoinConduits` by the link they held (`rejoinHeld`). Re-routing never
  cuts live power: nothing removes a built conduit (planned-ground clearance takes down walls, doors and
  furniture, not the `Conduits` layer), so an old path stays until the new one is built and `connect`
  builds only the cells of the planned path that are missing.
- **Router.** `routeCells` (`ConduitRouter.cs`) is the one deterministic 4-neighbour breadth-first router,
  shared with `connect` and the turret wiring (`powerRoute`), bounded by `powerRouteBound`. Cells come from
  `ConduitCells`: conduits ignore buildings, walls and doors (the `Conduits` layer; only another power
  transmitter conflicts), but need terrain with light footing, so deep water, other terrain without
  footing, hazards and natural rock (outside a dug room's walls) block, and cells the survey lacks are
  unknown and never routed.
- **Valid.** `LayoutPlan.Valid` rejects a link whose end names no room or power site of the plan, equal
  ends, or cells that are empty, repeat or are not 4-connected.

## Acceptance

`power/fuel` (`brain/src/RimGovernor.Brain.Acceptance.Cases/Power`) stages an empty wood-fired generator and shows
ordinary hauling refuel it until its stove is powered; the restoration comparison is `PowerRestorationTests` and
`PowerMethodTests`, the interruption-priced comparison `PowerUtilityTests` and the switch and allow binding
`PowerSwitchTests`. The OnDemand native case `power/configure-and-route`
(`Power/PowerConfigureAndRoute.cs`) stages a fuelled wood-fired generator wired to a cooler and three idle electric
stoves, and a lamp out of the conduit's reach: it asserts that a `configure-` Method switches an idle stove off and a
colonist flicks it, that the cooler stays powered and that a conduit routes to the lamp. It is registered and compiles
but has not been executed, so the native gizmo toggle, the pawn's flick and the `switch` readback are unverified in game. The OnDemand native case `upkeep/restore-service` stages the same empty generator with the
colonist's work priorities at zero, so only the brain's forced refuel (and a wall's forced repair) can restore it;
it is registered and compiles but has not been executed.
`power/wind-path` reads the service row's `wind` state (`wind_turbine` with its `blocked_cells`) for a clear and a roofed turbine.
The generation shortfall on a draining reserve, storage for a solar-only network (one `Battery` sited
indoors), a wind turbine on a clear catch zone and a geothermal generator on a free geyser, each priced against
deferring, are decided by the power planner over recorded reviews
(`brain/src/RimGovernor.Brain.Runtime`).
