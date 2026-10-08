# Storage

[Documentation](../../README.md) · [Architecture](overview.md) · Reference

Each department owns its stockpiles. A Department that holds stock is a
`policy.StoreOwner` (registered in `storeOwners`): it declares its `Stores` (a
`policy.Store`: role, planned room or rectangle, filter, priority, the room it
asks for when full) and its `RoomDemand` from capacity. `MaintainStockpiles` is
the one applier, through the ordinary zone create, patch and delete actions; the
standing zones are the only record (see
[persistence contracts](../contracts/persistence-contracts.md)). Rooms stay
layout's job. The stores are declared per department: Storage (warehouse, yard),
Food, Medical, Industry (bench ingredients), Military (armory, wardrobe),
People (tomb, morgue, graveyard) and Sanitation (waste dump, incinerator); the
animal feed store belongs to Husbandry.

A store is one clean zone over its whole planned room, created once the room's
interior is open ground (at plan time on open ground; when the last cell clears
for a dug or partly rocky room, which layout excavates first) and sized once.
It is never grown, shrunk or merged, never given a stand-in, and deleted only
when its purpose is gone (a bench demolished, a room retired), create before
delete on a move. Headroom is another room: see below.

If obstacles or exclusions split a store's usable ground, its zone takes the
largest connected patch, for both rectangular and explicit footprints. Equal
patches choose the lowest cell. The standing zone keeps that size.

## The stores


| Store | Role | Where | Priority | Holds |
| --- | --- | --- | --- | --- |
| Warehouse | `general` | One zone over the whole interior of each planned storage room (15x7): from plan time on open ground, once the interior is open for a dug or partly rocky room; sized once | Low | The `indoor_only` preset plus Buildings, less the burnable special |
| Materials yard | `yard` | One zone over the whole interior of each planned yard, an Outdoor room (13x9 interior, fence and gate) beside the core inside its ring (`ReserveYard`, planned from the start; MaintainStockpiles raises the fence ring and gate as a `shell` edit, no floor); sized once | Low | The `outdoor_safe` preset: items that neither spoil nor deteriorate outdoors, derived from the native item catalog, never a list in Go |
| Workstation stockpiles | `ingredients:*` | A free roofed 2x2 patch in the bench's room nearest the bench, one per bench with an active bill (the kitchen and butcher excepted) | Important | That bench's recipe ingredients (`policy.DeriveBenchInputs`); a stonecutter's is its stone chunks |
| Meal store | `meals:*` | The planned meal closet whole, a 2x2 in the freezer at its dining door, or one cell beside the dining table off the chairs (the one store sited from a built fact, the table); each from plan time | Critical | Prepared meals |
| Freezer shelves | `rawmeat:*`, `rawveg:*`, `corpses:*`, `perishables:*` | Three 2x2 shelves in the planned freezer (raw meat and raw vegetables nearest the kitchen door, carcasses by the butchery door) and the perishables cover over the rest, all declared by the Food department from plan time on open ground (a rock or unseen interior defers the create; no room census) | Critical, perishables Preferred | Raw meat, raw vegetables, the corpse larder, the perishables catch-all |
| Medicine store | `medicine:*` | A 2x2 in the planned hospital, nearest the template's bed slots by walking distance (nearest the door without slots), from plan time | Important | Medicine |
| Tomb | `tomb:*` | The tomb room | Critical | Tomb corpses |
| Gear | `apparel`, `weapons` | The storage room (apparel) and armory (weapons) | Preferred | Clothing and armor, weapons |
| Waste dump | `wastedump` | The waste yard interior outside the incinerator outline (the Sanitation department's declared store, planned room cover from plan time) | Low | Everything storable except the native not-burnable special |

Low priority on the warehouse and yard is deliberate: the higher-priority
workstation and medicine stockpiles draw their items first, and
hauling (which RimWorld owns) moves the rest to the warehouse or yard.

The one waste dump is a declared store sized once over the yard, so waste
hauls to it from anywhere and the incinerator zone (Preferred) draws the
burnable part off it. Fresh animal corpses use the freezer shelf and the
butchery; there is no dump role for them. Zones of the retired `dump:*` roles
in older saves are deleted.

## Gear rooms

When every warehouse zone is at 85% used (the state that
also asks for a further storage room), the Military department raises `RoomDemand` for each
kind of serviceable gear the colony holds (armory for weapons and armor,
wardrobe for clothing) and layout adds the room: the armory beside the
storage room, the wardrobe beside the workshop with the tailor bench, with no
wealth gate and no item-count threshold. A planned gear room not yet standing
holds back the further storage room. Layout keeps the armory and every prison
out of each other's weapon clearance; a standing armory with no free cell clear
of a prison is `ErrArmoryNearPrison` in `StoreDeclaration.Err`, logged by the
reviewer. A catalog naming no armor def fails the stockpile review with
`ErrNoArmorDefs`.

## Headroom

When every zone of a store is at 85% used (`StockpileFurtherRoomFill`), the
department's `RoomDemand` asks layout for a further room (`Storage`, `Yard`,
armory, wardrobe or graveyard, one more than the plan holds) and that room's
zone is created once it is planned and open.

## Planned rooms

Layout plans the storage, armory and wardrobe rooms and the yard's fence ring;
the shared room reconciliation builds them ([facilities](facilities.md)). A
store's zone does not wait for the shell: zoning needs no builder.

## Review check

The [colony review](../testing/colony-review.md) reports the storage state
from the colony census (`/api/player/colony`: owned zones per role kind and
whether the loot census still holds a safe stack forbidden). It lists the final zone count
per role and flags starting supplies still forbidden after a day. A report, not
a gate.

An admitted zone create or delete is a `layout_edit` row (family `stockpile`, attrs `kind`,
`role`, and `cells` on a create), stamped with its tick. The sustained colony acceptance's
`AuditStockpiles` reports the standing zones per store kind, the admitted creates in order
(role, tick, cells: the first create is the earliest a store can open) and the deletes by
role, and fails a role created again after a delete (churn). Early-room spoilage is read
against the create ticks; no check gates it.
