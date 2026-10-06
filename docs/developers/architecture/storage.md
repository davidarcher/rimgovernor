# Storage

[Documentation](../../README.md) · [Architecture](overview.md) · Reference

One deterministic function, `policy.PlanStorage`, decides every room-bound
stockpile the colony has. It reads the layout plan, room census, bed census,
planning cells and the standing zones, and returns the desired
`StockpileSite`s and the gear-room demand. The plan is derived each pass in
Go memory and stored nowhere (see
[persistence contracts](../contracts/persistence-contracts.md)); the standing
zones are the only record. `MaintainStockpiles` applies the diff through the
ordinary zone create, cell edit, patch and delete actions, and the role
registry (`domain` stockpile roles) supplies each role's filter and priority.
Rooms stay layout's job: the planner fills them with zones and signals layout
when it needs another room.

## The stores

| Store | Role | Where | Priority | Holds |
| --- | --- | --- | --- | --- |
| Warehouse | `general` | One zone over the whole interior of each planned storage room (15x7): from plan time on open ground, once the interior is open for a dug or partly rocky room; sized once | Low | The `indoor_only` preset plus Buildings, less the burnable special |
| Materials yard | `yard` | One zone over the whole interior of each planned yard, an Outdoor room (13x9 interior, fence and gate) beside the core inside its ring (`ReserveYard`, planned from the start; MaintainStockpiles raises the fence ring and gate as a `shell` edit, no floor); sized once | Low | The `outdoor_safe` preset: items that neither spoil nor deteriorate outdoors, derived from the native item catalog, never a list in Go |
| Workstation stockpiles | `ingredients:*` | A free roofed 2x2 patch in the bench's room nearest the bench, one per bench with an active bill (the kitchen and butcher excepted) | Important | That bench's recipe ingredients (`policy.DeriveBenchInputs`); a stonecutter's is its stone chunks |
| Meal store | `meals:*` | The planned meal closet whole, a 2x2 in the freezer at its dining door, or one cell beside the dining table off the chairs (the one store sited from a built fact, the table); each from plan time | Critical | Prepared meals |
| Freezer shelves | `rawmeat:*`, `rawveg:*`, `corpses:*`, `perishables:*` | The freezer | Critical, perishables Preferred | Raw meat, raw vegetables, the corpse larder, the perishables catch-all |
| Medicine store | `medicine:*` | A 2x2 in the planned hospital, nearest the template's bed slots by walking distance (nearest the door without slots), from plan time | Important | Medicine |
| Food store | `food` | A 3x3 in the planned kitchen at its door, from plan time | Preferred | Food, until the colony's food storage is met |
| Tomb | `tomb:*` | The tomb room | Critical | Tomb corpses |
| Gear | `apparel`, `weapons` | The storage room (apparel) and armory (weapons) | Preferred | Clothing and armor, weapons |
| Waste dump | `wastedump` | The waste yard interior outside the incinerator outline (the Sanitation department's declared store, planned room cover from plan time) | Low | Everything storable except the native not-burnable special |

Low priority on the warehouse and yard is deliberate: the higher-priority
workstation, medicine and food stockpiles draw their items first, and
hauling (which RimWorld owns) moves the rest to the warehouse or yard.

The one waste dump is a declared store sized once over the yard, so waste
hauls to it from anywhere and the incinerator zone (Preferred) draws the
burnable part off it. Fresh animal corpses use the freezer shelf and the
butchery; there is no dump role for them. Zones of the retired `dump:*` roles
in older saves are deleted.

## Gear rooms

When every warehouse zone is at 85% used (the state that
also asks for a further storage room), the planner raises `RoomDemand` for each
kind of serviceable gear the colony holds (armory for weapons and armor,
wardrobe for clothing) and layout adds the room: the armory beside the
storage room, the wardrobe beside the workshop with the tailor bench, with no
wealth gate and no item-count threshold. A planned gear room not yet standing
holds back the further storage room. Layout keeps the armory and every prison
out of each other's weapon clearance; a standing armory with no free cell clear
of a prison is `ErrArmoryNearPrison` on `StoragePlan.Err`, logged by the
reviewer. A catalog naming no armor def fails the stockpile review with
`ErrNoArmorDefs`.

## Warehouse and yard stores

The Storage department declares both stores (`store_storage.go`) and they are no longer planner sites. A store is never
grown, shrunk or merged. When every zone of a store is 85% used, the department's
`RoomDemand` asks layout for a further room (`Storage` or `Yard`, one more than
the plan holds) and the room's zone is created once it is planned and open. The
warehouse supersedes the opening general store.

## Growth, shrink and churn (planner sites)

A zone grows onto adjacent open cells once 85% of its cells hold things and
sheds empty edge cells after sitting at or under 25% for a day (never below
four cells). Same-filter
fragments merge. Edits are admitted within a per-cycle haul budget so a zone
move never floods the colonists with hauling. A role whose site moved gets its
new zone first; the old zone is deleted in the same review only once that create
is admitted, and its items rehome. Held building reservations and the unroofed
floor of a planned shell are protected ground no site takes.

## Review check

The [colony review](../testing/colony-review.md) reports the storage state
from the colony census (`/api/player/colony`: owned zones per role kind and
whether the loot census still holds a safe stack forbidden). It lists the final zone count
per role, flags any hour a role's zone count falls (a zone deleted or merged:
churn) and flags starting supplies still forbidden after a day. A report, not
a gate.
