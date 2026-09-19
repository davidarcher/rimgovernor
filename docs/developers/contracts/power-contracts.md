# Power contracts

`EnsureBasicPower` (`go/internal/policy/power_method.go`, `power_budget.go`;
issue [#413](https://github.com/davidarcher/rimgovernor/issues/413)) keeps
every enabled consumer powered by reading the native power census and sizing
each network against a day of energy, never by counting generators. Native
placement previews and shared admission still gate every build, and output
plus `PowerOn` on the consumer, not a receipt, establishes recovery.

## Deficits

A deficit is an enabled consumer (not forbidden, switched on) that is
disconnected or unpowered, or a powered network whose stored reserve covers
its current drain for under `ReserveMinDays` (one day). Forbidden or
switched-off consumers and producers are player intent
(`player_disabled_power`); an out-of-fuel or broken producer whose installed
capacity covers demand holds the goal (`waiting_for_refuel`,
`waiting_for_repair`), since refuelling and repair are ordinary pawn work. A
`SolarFlare` condition holds every build (`solar_flare`, see
[upkeep contracts](upkeep-contracts.md)).

## Energy budget

A draining network is sized by `ComputePowerBudget`, a 24 h balance built
from the same-network census rows: demand is the sum of enabled consumer
wattage; each producer's nominal wattage is spread over the day by its
definition's profile (constant for fuel-burning, geothermal and watermill
generators; a daylight curve for `SolarGenerator`, zero for a third of the
day and zero throughout an `Eclipse` condition; the ~1200 W of 2300 W
average for `WindTurbine`); installed storage is the summed battery
capacity. The wiki figures behind the profiles and the battery (600 Wd,
50 % charge efficiency, 5 Wd/day self-discharge) are constants in
`power_budget.go`.

The budget reports two independent shortfalls:

- **Generation (W)**: the constant wattage to add so that, after the day
  surplus refills a bank at charge efficiency, the night is covered. A
  daytime deficit is generation outright; a night deficit the surplus
  cannot refill at 50 % is generation too.
- **Storage (Wd)**: the night deficit the day surplus *can* refill, plus
  `StorageMargin` (25 %), beyond the installed capacity. A bank is capped at
  what the surplus fills, so unused capacity never self-discharges for
  nothing.

Selection order on a draining network: a generation shortfall adds one
generator (`generate`), a storage shortfall alone adds one `Battery`
(`store`), and a network the budget already covers waits for its bank to
charge (`waiting_for_charge`). A battery is never the answer to a generation
shortfall, and when `Battery` is not natively available the storage
shortfall falls back to a generator, which the observed reserve still
justifies. The observed reserve stays the check that the model's prediction
held: a consumer that actually loses power is a deficit whatever the budget
predicted.

## Methods

- `HiddenConduit` (`connect`): when the consumer's network has no producer but
  the colony has one, one bounded route (eight cells per method) from the
  consumer toward the nearest enabled producer, over cells the site census
  reports as buildable and outside protected geometry; successive methods
  extend the same route. Blocked routes report `no_observed_route`. Ordinary
  conduits are also replaced in bounded methods because roofs do not prevent
  their random short-circuit event.
- `generate`: one generator chosen by `RankGenerators` over
  `GeneratorDefinitions` (`SolarGenerator`, `WoodFiredGenerator`,
  `ChemfuelPoweredGenerator`), natively unavailable ones skipped, by cost
  per delivered day of energy: a fuel-free generator first once a `Battery`
  can bank its surplus, fuel-burning generators whose stock meets the floor
  (75 wood, 30 chemfuel) next in list order, fuel-short ones after, and a
  solar generator last when no battery can be built or the shortfall is
  night-only (the day already in surplus). No available generator reports
  `no_affordable_generator`, or the research gate that would make one
  available.
- `store`: one `Battery`, placed on a roofed cell within six cells of the
  draining consumer (a battery short-circuits unroofed in rain or snow).
- `shelter_power`: a bounded enclosure around exposed vulnerable equipment,
  with recovery requiring the observed roof. See
  [electrical safety](../architecture/facilities.md#electrical-safety).

Method identity is the target consumer plus the sorted producer ids (and,
for `store`, the installed capacity), so a repeated deficit replays the same
method while a new producer or bank makes a new one. Hands correlate
completed work against `PowerFamilyDefinitions()`.

## Acceptance

`power/fuel`, `power/reserve` and `power/battery`
(`go/internal/nativeaccept/cases/power`) cover the refuel hold, the
generation shortfall on a draining reserve, and generation plus connection
from an exhausted bank with no generator. `power/rain` proves enclosure and
conduit replacement followed by a full day of rain without short circuits,
fires or equipment damage.
