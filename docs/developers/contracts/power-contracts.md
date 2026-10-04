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
  third of the day and throughout an `Eclipse`); ~1200 W of 2300 W average for `WindTurbine`.
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
  one bounded route (eight cells per method) from the consumer toward the nearest enabled producer,
  over cells the site census reports buildable and outside protected geometry; successive methods
  extend the route. Blocked routes report `no_observed_route`. Ordinary conduits are also replaced in
  bounded methods because roofs do not prevent their random short-circuit event.
- **`generate`:**
  - `GeothermalGenerator` first whenever the definition is natively available and the development
    census lists a free steam geyser (`DevelopmentFacts.geysers`: `Building_SteamGeyser` cells,
    occupied when a harvester, building, blueprint or frame stands on it) whose footprint the consumer
    can reach within `GeothermalReachCells` (48) over buildable cells. The proposal is fixed-site
    (`FixedSite()`), previewed at the geyser's anchor without the site census (which reports the geyser
    cell occupied) and admitted on the native preview's legality and safety alone.
  - Otherwise one generator chosen by `RankGenerators` over `GeneratorDefinitions` (`SolarGenerator`,
    `WindTurbine`, `WoodFiredGenerator`, `ChemfuelPoweredGenerator`), natively unavailable ones
    skipped, by cost per delivered day of energy: a fuel-free generator first once a `Battery` can bank
    its surplus; then fuel-burning generators whose stock meets the floor (75 wood, 30 chemfuel) in
    list order; fuel-short ones after; a renewable last when no battery can be built or (solar) the
    shortfall is night-only. Ranked runners-up travel as `Alternatives`: a definition no site accepts
    yields to the next under the same method key.
  - A `WindTurbine` site is searched within twelve cells of the consumer and accepted only when the
    placement preview's `wind_blocked_cells` (native `WindTurbineUtility.CalculateWindCells`
    catch-zone cells that are roofed, off-map or hold a wind-blocking thing) is known and zero.
  - No available generator reports `no_affordable_generator`, or the research gate that would make one
    available (the research ladder places `GeothermalPower` after `Batteries`).
- **`store`:** one `Battery` on a roofed cell within six cells of the draining consumer (an unroofed
  battery short-circuits in rain or snow).
- **`shelter_power`:** a bounded enclosure around exposed vulnerable equipment; recovery requires the
  observed roof. See [electrical safety](../architecture/facilities.md#electrical-safety).

Method identity is the target consumer plus the sorted producer ids (and, for `store`, the installed
capacity), so a repeated deficit replays the same method while a new producer or bank makes a new one.
Hands correlate completed work against `PowerFamilyDefinitions()`.

## Acceptance

`power/fuel` (`go/internal/nativeaccept/cases/power`) covers the refuel hold natively on the blank lab.
The generation shortfall on a draining reserve, storage for a solar-only network (one `Battery` sited
indoors), a wind turbine on a clear catch zone and a geothermal generator on a free geyser ahead of
every other generator are snapshot tests of the power planner over recorded reviews
(`go/internal/buildingruntime`).
