# Food storage and refrigeration

[Contracts](README.md) · [Storage architecture](../architecture/storage.md)

The native colony frame always includes human food supply, cooking benches and
butchering benches, including empty censuses. Their wire members are required;
missing sections fail decoding. The combined supply belongs to the required
forecast section. A bench's auto-refuel state stays NotRefuelable or
Refuelable(Allowed) through observation and heat-campfire selection.

Cooling-room and meal-closet demand are computed from the current layout and
room census. Before a layout exists there are no planned rooms to cool or shell.
Unavailable cooler research also leaves that planned cooling demand empty.
A warm room named by the refrigeration review must exist in the observation,
and every existing cooler's hot side must have a site-cell or room observation;
missing geometry raises RequiredFactException instead of selecting a method.

Reserve supply targets are NotRequested until the family needs to hold or
release stacks. The full family read then supplies Targets, including a
measured empty list. Pending access cannot propose a Method from an unrequested
read. Reserve states remain Disabled, NoConsumers or Reviewed.

Meal-tier input uses the food forecast's NoEaters or measured Days, a total
cook roster and observed expectations pressure. No eaters owe no meal tier.
Malformed raw-food forecasts fail at the observation boundary. Food storage
site reads must all succeed before capacity is compared; a failed read cannot
make an incomplete list appear to prove a need for more production.
