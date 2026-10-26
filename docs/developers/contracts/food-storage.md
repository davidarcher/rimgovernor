# Food storage and refrigeration

Storage nutrition rows and their aggregate must remain finite; an overflowing aggregate fails the review.

Food acquisition selects the shared food plan's open forage and lone-hunt channels.
Pending forage leaves hunt slots available; pending or held sources are never
admitted twice. Resource acquisition uses the shared resource supply portfolio
for each requested resource, without a separate wood-only selector. Required-fact
and invariant failures reach the review owner and stop the affected pass.

Meal shelves use the native `MealShelf` filter preset: every storable definition
whose ingestible food type includes Meal. They include installed content without
a vanilla definition allowlist. Freezer catch-all storage uses `ColdFood`: only
rot-capable definitions in Foods or CorpsesAnimal (including the nested insect
category), with rotten items disallowed. Non-rotting reserve food belongs in
general storage. The same presets pass through zone creation, shelf settings,
journal codecs and native filter matching; their observations remain native-owned.

The existing storage chooser reads at most sixteen distinct unstored food
definitions in ordinal order. Policy computes spare capacity as observed
capacity minus stored units, floored at zero, before choosing the existing
storage method. The broader chooser replacement remains tracked by
[#3239](https://github.com/davidarcher/rimgovernor/issues/3239) and
[#3734](https://github.com/davidarcher/rimgovernor/issues/3734).

`FoodSupplyFacts.Stocks` owns observed food quantity, rot, location, temperature,
human-meat classification and corpse yield. Native observation builds those rows
directly from the stock census. The general thing table carries identity,
forbidden state and corpse classification; it does not duplicate food evidence.

[Contracts](README.md) · [Storage architecture](../architecture/storage.md)

The native colony frame always includes human food supply, cooking benches and
butchering benches, including empty censuses. Their wire members are required;
missing sections fail the required contract. The combined supply belongs to the required
forecast section. Its consumer, stock and larder records are the same GameFacts records
Policy reads. Native records the physical stock facts once; no thing-table or
catalog join reconstructs them in the brain. The forecast view retains references
to those rows and classifies barred stocks and reserve IDs. Reserve selections
remain derived policy state under the existing food-reserve owner; protecting a
stock in a forecast does not change its observed forbidden flag.
The canonical building fuel service owns auto-refuel control: NoSwitch or
Switch(Allowed). Bill stacks do not duplicate this physical state. Temperature
control reads the building service, including heaters with no cooking recipes.

Cooling-room and meal-closet demand are computed from the current layout and
room census. Before a layout exists there are no planned rooms to cool or shell.
Unavailable cooler research also leaves that planned cooling demand empty.
A warm room named by the refrigeration review must exist in the observation,
and every existing cooler's hot side must have a site-cell or room observation;
missing geometry raises RequiredFactException instead of selecting a method.
Each cooler retains its actual typed building row. Geometry, temperature control
and power state are read from that row; missing temperature control refuses the
calculation. The power census selects the exact buildings requested, without
copying their physical state into a second observation record.

Reserve supply targets are NotRequested until the family needs to hold or
release stacks. The full family read then supplies Targets, including a
measured empty list. Pending access cannot propose a Method from an unrequested
read. Reserve states remain Disabled, NoConsumers or Reviewed.

Meal-tier input uses the food forecast's NoEaters or measured Days, a total
cook roster and observed expectations pressure. No eaters owe no meal tier.
Malformed raw-food forecasts fail at the observation boundary. Food storage
site reads must all succeed before capacity is compared; a failed read cannot
make an incomplete list appear to prove a need for more production.

## Freezer value

A served planned freezer earns the value of the rot it prevents, in the shared
Investment ledger from its service day. For a stock with remaining freshness D
days, ambient native rot rate r and predicted freezer rate f, the daily credit
is `count × unit value × (r - f) / (r × D)`. Stocks that keep, already have zero
rot rate, or gain no lower rate earn nothing. `FoodStock.RotRate` is the native
`GenTemperature.RotRateAtTemperature(AmbientTemperature)` value; the catalog
samples the same function at whole degrees from −273 through 1000°C. Predicted
rates interpolate adjacent native samples. Missing samples, thermal facts,
construction estimates or material values raise `RequiredFactException`.

The closed-room temperature is the shared shelter model's steady temperature.
Each cooler uses its def's signed heat output, its thermostat and the game's
cold/hot-side efficiency; heaters include their falloff above 20°C. A room with
no source settles toward outdoors. Vented coolers exhaust to the outdoor thermal
reservoir (the game's 1.25× rejected heat does not return to the closed interior).
Current weather and the hot-side reservoir are held constant.

Every planned freezer joins both the planned thermal read and shelter geometry,
outside the ordinary four-room cap. Native chooses a vented ring cell, or reuses
the standing cooler serving the interior, and reads the cooler's build estimate.
With unfinished cooler research or no vented wall, the room earns no cooling
credit. Missing or insufficient current power does not block an early freezer:
its cooler, unbuilt conduit-link cells and next researched planned generator
(turbine, then solar, then geothermal) are its own owed work and materials.
One such generator is assumed to cover one cooler. Disconnected freezers allocate these planned generators in room-id order; a generator cannot be assumed again by another freezer. Without a planned generator
there is no powered source and no cooling credit. These prerequisite sites use
the ordinary per-site construction estimator and add no schedulable roots;
refrigeration and power retain execution ownership. Shared sites cost work and
materials once. Placed prerequisites retain their remaining work and materials.

Among served freezers, lowest room id takes each stack first, highest daily
credit first, up to one stack per interior storage cell. Each physical stack
earns credit once. The credit retains the canonical food-stock rows, including
barred stock; it creates no second food observation.
