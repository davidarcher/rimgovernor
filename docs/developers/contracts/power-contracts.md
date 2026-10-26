# Power contracts

Pending power demand accepts nonnegative finite wattage without an arbitrary upper ceiling. Nonfinite and negative values fail planning validation.

The power family's `power/basic` Need (`PowerFamily.cs`, `PowerMethod.cs`, `PowerBudget.cs`, #3257) keeps every enabled
consumer powered by reading the native power census and sizing each network against a day of energy,
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

A deficit is an enabled consumer (not forbidden, switched on) that is disconnected or unpowered, or a
powered network whose stored reserve covers its current drain for under `ReserveMinDays` (one day), or
whose known stored reserve is under the coming night's deficit (a solar-only network by day).

- Forbidden or switched-off consumers and producers are player intent (`player_disabled_power`).
- An out-of-fuel or broken producer whose installed capacity covers demand holds the concern
  (`waiting_for_refuel`, `waiting_for_repair`): refuelling and repair are ordinary pawn work. The
  refuel hold lends the clock the same bounded window a stock refusal does (`stockWaitTicks`), since
  only ticks land the haul.
- A `SolarFlare` condition holds every build (`solar_flare`, see
  [upkeep contracts](upkeep-contracts.md)).

## Energy budget

`ComputePowerBudget` sizes a draining network as a 24 h balance over the same-network census rows.

- **Demand:** enabled consumer wattage plus the declared draw
  (`observation.PlanningDefinition.PowerW`, the def's power comp) of every building action still open
  in other concerns' held reservations, so a workbench about to be built is priced before it turns on.
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
  (600 Wd, 50 % charge efficiency, 5 Wd/day self-discharge) are constants in `PowerBudget.cs`.

Two independent shortfalls:

- **Generation (W):** the constant wattage to add so that, after the day surplus refills a bank at
  charge efficiency, the night is covered. A daytime deficit is generation outright; so is a night
  deficit the surplus cannot refill at 50 %.
- **Storage (Wd):** the night deficit the day surplus *can* refill, plus `StorageMargin` (25 %), beyond
  installed capacity. A bank is capped at what the surplus fills, so unused capacity never
  self-discharges for nothing.

Selection on a draining network: a generation shortfall adds one generator (`generate`); a storage
shortfall alone adds one `Battery` (`store`); a network the budget already covers waits for its bank
(`waiting_for_charge`). A battery is never the answer to a generation shortfall; when `Battery` is not
natively available the storage shortfall falls back to a generator. The observed reserve remains the
check on the model: a consumer that actually loses power is a deficit whatever the budget predicted.

## Methods

- **`connect` (`HiddenConduit`):** when the consumer's network has no producer but the colony has one,
  the whole route from the consumer toward the nearest enabled producer as one proposal, over cells
  the site census reports buildable and outside protected geometry. There is no cell cap: each cell
  is its own native preview call, so no wire frame or shm slot bounds the route. Blocked routes
  report `no_observed_route`. Ordinary conduits are also replaced in one method because roofs do not
  prevent their random short-circuit event.
- **`generate`:**
  - A supported, natively available steam plant first whenever the development
    census lists a free steam geyser (`DevelopmentFacts.geysers`: `Building_SteamGeyser` cells,
    occupied when a harvester, building, blueprint or frame stands on it) whose footprint the consumer
    can reach within `GeothermalReachCells` (48) over buildable cells. The proposal is fixed-site
    (`FixedSite()`), previewed at the geyser's anchor without the site census (which reports the geyser
    cell occupied) and admitted on the native preview's legality and safety alone.
    Every exact native steam-plant definition joins the shared generator catalog;
    ranked steam alternatives retain the same geyser anchor through legal preview.
    Steam plants never enter ordinary free-placement ranking without a free,
    reachable geyser. Observed mechanics, rather than a geothermal definition name,
    identify fixed placement.
  - Otherwise one generator chosen by `RankGenerators` over every installed player-buildable ordinary
    generator whose native mechanics have a delivery model, natively unavailable ones
    skipped. Tiers: a fuel-free generator first once a `Battery` can bank its surplus; then a
    generator burning a fuel the colony can grow, stocked to the fuel tank (`fuelCapacity` of its
    refuelable comp, divided by its item-to-fuel multiplier with the census's measured difficulty
    maintenance factor, rounded up to whole items; a crop that harvests the fuel is sowable now, `policy.ResourceGrowable`, so a
    tree plantation counts and wild trees or chemfuel do not); then one burning a finite fuel,
    stocked; fuel-short ones; a renewable last when no battery can be built or (solar) the
    shortfall is night-only or the current twelfth has no daylight at the map's latitude (polar
    night, read from the required gear climate's `DaylightHours`). Within a tier the higher average watts per steel of build cost goes
    first, components priced at the game recipe's steel (`Make_ComponentIndustrial`: 12 steel each,
    `DefinitionCatalog.ProductInputCost`); a component with no recipe (`InputCost.NoRecipe`) prices nothing and list order breaks
    ties in ordinal definition order. Cost, nominal watts and accepted fuels come from the catalog
    (`DefinitionCatalog.GeneratorRow`). A multi-fuel plant chooses a stocked full-tank fuel first,
    then renewable fuel, then greater stock, with ordinal identity breaking ties. All candidates join
    the ordinary planning census, research gate, pending-work correlation and room reconciliation.
    Renewability is read from the colony's sowable crops rather than the Round's supply plan: that
    plan holds rows only for unmet `economy/resource` floors, so a stocked fuel has none. Ranked
    runners-up travel as `Alternatives`: a definition no site accepts yields to the next under the
    same method key.
  - A `WindTurbine` site is searched within twelve cells of the consumer and accepted only when the
    placement preview's `wind_blocked_cells` (native `WindTurbineUtility.CalculateWindCells`
    catch-zone cells that are roofed, off-map or hold a wind-blocking thing) is known and zero.
  - No available generator reports `no_affordable_generator`, or the research gate that would make one
    available (the research ladder places `GeothermalPower` after `Batteries`).
- **`store`:** one `Battery` on a roofed cell within six cells of the draining consumer (an unroofed
  battery short-circuits in rain or snow): the placement search keeps only roofed cells. The layout plans
  no battery room and no battery site (#3814): an enclosing room changes no rain exposure, and the fire it
  would contain is fire harm, not modelled yet.
- **`shelter_power`:** a bounded enclosure around exposed vulnerable equipment; recovery requires the
  observed roof. See [electrical safety](../architecture/facilities.md#electrical-safety).

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
ordinary hauling refuel it until its stove is powered; the planner's refuel hold is `PowerMethodTests`.
`power/wind-path` reads the service row's `wind` state (`wind_turbine` with its `blocked_cells`) for a clear and a roofed turbine.
The generation shortfall on a draining reserve, storage for a solar-only network (one `Battery` sited
indoors), a wind turbine on a clear catch zone and a geothermal generator on a free geyser ahead of
every other generator are decided by the power planner over recorded reviews
(`brain/src/RimGovernor.Brain.Runtime`).
