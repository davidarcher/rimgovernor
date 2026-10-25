# Food storage and refrigeration

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
A bench's auto-refuel state stays NotRefuelable or
Refuelable(Allowed) through observation and heat-campfire selection.

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
