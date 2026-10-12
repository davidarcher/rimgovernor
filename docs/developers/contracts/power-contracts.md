# Power contracts

`EnsureBasicPower` (`go/internal/policy/power_method.go`, `power_budget.go`) keeps every enabled
consumer powered by reading the native power census and sizing each network against a day of energy,
never by counting generators. Native placement previews and shared admission gate every build; output
plus `PowerOn` on the consumer, not a receipt, establishes recovery.

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
  fuel-burning, geothermal and watermill generators; a daylight curve for `SolarGenerator` (zero for a
  third of the day and throughout an `Eclipse`); for `WindTurbine`, `policy.WindProfile` over the
  catalog's `WindManager.WindSpeedRange` (0.04-2) and the `Clear` weather's `windSpeedFactor` (1):
  output is nominal times wind speed capped at 1.5 (`CompPowerPlantWind`, a game constant the catalog
  lacks), the speed taken as uniform over the range. That is 0.956 of 2300 W, 2199 W, matching the
  wiki's measured clear-weather average (snow 2622 W and blizzard 3409 W are higher, so clear is the
  conservative figure). The earlier 1200 W figure was about half the catalog's.
- **Storage:** the summed battery capacity. The wiki figures behind the profiles and the battery
  (600 Wd, 50 % charge efficiency, 5 Wd/day self-discharge) are constants in `power_budget.go`.

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
  - `GeothermalGenerator` first whenever the definition is natively available and the development
    census lists a free steam geyser (`DevelopmentFacts.geysers`: `Building_SteamGeyser` cells,
    occupied when a harvester, building, blueprint or frame stands on it) whose footprint the consumer
    can reach within `GeothermalReachCells` (48) over buildable cells. The proposal is fixed-site
    (`FixedSite()`), previewed at the geyser's anchor without the site census (which reports the geyser
    cell occupied) and admitted on the native preview's legality and safety alone.
  - Otherwise one generator chosen by `RankGenerators` over `GeneratorDefinitions` (`SolarGenerator`,
    `WindTurbine`, `WoodFiredGenerator`, `ChemfuelPoweredGenerator`), natively unavailable ones
    skipped. Tiers: a fuel-free generator first once a `Battery` can bank its surplus; then a
    generator burning a fuel the colony can grow, stocked to the fuel tank (`fuelCapacity` of its
    refuelable comp; a crop that harvests the fuel is sowable now, `policy.ResourceGrowable`, so a
    tree plantation counts and wild trees or chemfuel do not); then one burning a finite fuel,
    stocked; fuel-short ones; a renewable last when no battery can be built or (solar) the
    shortfall is night-only. Within a tier the higher average watts per steel of build cost goes
    first, components priced at the game recipe's steel (`Make_ComponentIndustrial`: 12 steel each,
    `DefinitionCatalog.ProductInputCost`); an unknown recipe prices nothing and list order breaks
    ties. Cost, nominal watts and fuel come from the catalog (`DefinitionCatalog.GeneratorRow`).
    Renewability is read from the colony's sowable crops rather than the Round's supply plan: that
    plan holds rows only for unmet `MaintainResource` floors, so a stocked fuel has none. Ranked
    runners-up travel as `Alternatives`: a definition no site accepts yields to the next under the
    same method key.
  - A `WindTurbine` site is searched within twelve cells of the consumer and accepted only when the
    placement preview's `wind_blocked_cells` (native `WindTurbineUtility.CalculateWindCells`
    catch-zone cells that are roofed, off-map or hold a wind-blocking thing) is known and zero.
  - No available generator reports `no_affordable_generator`, or the research gate that would make one
    available (the research ladder places `GeothermalPower` after `Batteries`).
- **`store`:** one `Battery` on a roofed cell within six cells of the draining consumer (an unroofed
  battery short-circuits in rain or snow). On a cold map, while the plan has no battery room,
  `PlannedPowerSites(Battery)` lists the shelter porch's slots first: an open lean-to strip on the
  exterior face of the wall opposite the shelter door (a `PlannedPorch` child of the shelter, `layout_porch.go`; two 1x2
  slots, no walls). The porch is not enclosed, so the plan that places a battery on it also carries a
  `build_roof` action over the whole strip (vanilla's Build Roof area, see
  [action contracts](action-contracts.md)), and `previewPlannedPower`'s roofed-cell gate accepts porch
  cells on that basis. Once a battery room is planned the porch slots are no longer listed; batteries
  already on the porch stay until the shelter retires, when the porch ground joins `RetiredGroundOf`
  and clearance takes them down. Temperate maps get no porch.
- **`shelter_power`:** a bounded enclosure around exposed vulnerable equipment; recovery requires the
  observed roof. See [electrical safety](../architecture/facilities.md#electrical-safety).

Method identity is the target consumer plus the sorted producer ids (and, for `store`, the installed
capacity), so a repeated deficit replays the same method while a new producer or bank makes a new one.
Hands correlate completed work against `PowerFamilyDefinitions()`.

## Planned conduit net

A tiled layout plan (`layout_tile.go`) owns its conduit paths: `LayoutPlan.Conduits` is a list of
`ConduitLink{From, To, Cells}` derived with the plan by `RouteConduits` (`layout_conduit.go`). It is the
net the planner would lay; `EnsureBasicPower` still decides which sites connect, by which method and at
what budget, and the `connect` contract above is unchanged. No action kind carries it.

- **Refs.** `From`/`To` are tagged: `room:<id>` for a planned room, `site:<node id>` for a site: a
  turbine or solar plot site node (`NodeTurbine`, `NodeSolar`) by its node id, or a
  `battery_room` or `geothermal` reservation, named `<kind>-<index in Reservations>`
  (`ReservationSiteID`).
- **Consumers.** Planned rooms holding a station whose def draws power (`Station.BaseW < 0`, read from the
  power comp into the station catalog); the cells are those stations' footprints, else the whole room.
  There is no role table.
- **Topology.** One net grows. The first power site is the root; each later site, then each consumer, routes
  from its own cells to the nearest cell of the net so far, cells shared. No star forms on any node, the
  battery room included. A node that cannot reach the net gets a `conduit_no_route` decision and no link;
  with no power site every consumer gets `conduit_no_source`; a routed link is `conduit_routed`
  (`TileResult.ConduitDecisions`, for the caller to journal).
- **Replan (#2966).** A replan routes against the links the colony holds (`TileInput.Conduits`): a node
  whose held link still starts on it, crosses only `ConduitCells` ground and ends on the net so far keeps it
  (`conduit_kept`); only the nodes a change broke route anew (`conduit_routed`). A room that replaces another
  (`Replaces`) or a site that replaces another (`ReplacesSites`) inherits its predecessor's link. A retired
  site or room has no node, so its path is not laid again. The rooms `keepHeld` puts back, which the tiler
  did not plan, join the net through `JoinConduits` by the link they held (`rejoinHeld`). Re-routing never
  cuts live power: nothing removes a built conduit (planned-ground clearance takes down walls, doors and
  furniture, not the `Conduits` layer), so an old path stays until the new one is built and `connect`
  builds only the cells of the planned path that are missing.
- **Router.** `routeCells` (`conduit_router.go`) is the one deterministic 4-neighbour breadth-first router,
  shared with `connect` and the turret wiring (`powerRoute`), bounded by `powerRouteBound`. Cells come from
  `ConduitCells`: conduits ignore buildings, walls and doors (the `Conduits` layer; only another power
  transmitter conflicts), but need terrain with light footing, so deep water, other terrain without
  footing, hazards and natural rock (outside a dug room's walls) block, and cells the survey lacks are
  unknown and never routed.
- **Valid.** `LayoutPlan.Valid` rejects a link whose end names no room or power site of the plan, equal
  ends, or cells that are empty, repeat or are not 4-connected.

## Acceptance

`power/fuel` (`go/internal/nativeaccept/cases/power`) covers the refuel hold natively on the blank lab.
The generation shortfall on a draining reserve, storage for a solar-only network (one `Battery` sited
indoors), a wind turbine on a clear catch zone and a geothermal generator on a free geyser ahead of
every other generator are snapshot tests of the power planner over recorded reviews
(`go/internal/buildingruntime`).
