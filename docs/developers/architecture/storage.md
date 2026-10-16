# Storage

[Documentation](../../README.md) · [Architecture](overview.md) · Reference

Each department owns its stockpiles. A Department that holds stock is a
`policy.StoreOwner` (registered in `storeOwners`): it declares its `Stores` (a
`policy.Store`: role, planned room or rectangle, filter, priority) and its
`RoomDemand` (the yard's further room is asked from runway; see Headroom). `MaintainStockpiles` is
the one applier, through the ordinary zone create, cell-edit, patch and delete actions; the
standing zones are the only record (see
[persistence contracts](../contracts/persistence-contracts.md)). Rooms stay
layout's job. The stores are declared per department: Shelter (the starter
shelter's 3x3 starter-storage patch, one Low priority general zone until a
storage room or freezer stands, then deleted so its contents haul to the better
stores; it is only the patch because the placement search treats zoned cells as
unplaceable, so a whole-interior zone starved the campfire slot, tracked by the
layout epic), Storage (warehouse, yard), Food, Medical, Industry (bench ingredients), Military (the weapons and apparel zones),
People (tomb, morgue); the
animal feed store belongs to Husbandry.

A store is a rectangle placement over its planned room, created once the room's
interior is open ground (at plan time on open ground; when the last cell clears
for a dug or partly rocky room, which layout excavates first). Whole-footprint
stores expand over reachable cleared cells within their declared site. Compatible
owned fragments consolidate into the first native zone by ID once they connect:
growth settles before adjacent fragments are deleted, then their freed cells are
added on the next review. Blockers, unknown cells, policy exclusions and unrelated
zones stay untouched. Fixed-size stores retain their requested size. A store is
never given a stand-in; a move creates its replacement before deleting the old
zone. Headroom beyond the declared footprint needs another room: see below.

Native filters each rectangle with vanilla stockpile zoneability and passable
terrain, then creates a zone for every cardinally connected component. Items
already on the ground do not prevent placement. Existing zones are skipped;
creating a store never adopts or changes player zones. Each created component
gets the requested filter and priority, and its receipt carries its native ID
and actual cells into the Concern's ownership claims. Growth keeps the survivor's
native ID, filter, priority and ownership. Planned exclusions are covered by
separate rectangle drags and excluded from growth.

## The stores


| Store | Role | Where | Priority | Holds |
| --- | --- | --- | --- | --- |
| Warehouse | `general` | One zone over the whole interior of each planned storage room (15x7): from plan time on open ground, once the interior is open for a dug or partly rocky room | Low | The `indoor_only` preset plus Buildings, less the burnable special |
| Materials yard | `yard` | One zone over the whole interior of each planned yard, an Outdoor room (13x9 interior, unfenced: no ring, gate or floor) beside the core inside its ring (`ReserveYard`, planned from the start) | Low | The `outdoor_safe_waste` preset, the one outdoor store: items that neither spoil nor deteriorate outdoors, plus what is already rotten (rot-capable defs with allow-fresh off: rotting corpses) and waste (dissolving defs: the toxic wastepack), left there to wear away. Defined natively from the def database, never a list in the brain |
| Workstation stockpiles | `ingredients:*` | A free roofed 2x2 patch in the bench's room nearest the bench, one per bench with an active bill (the kitchen and butcher excepted) | Important | That bench's recipe ingredients (`policy.DeriveBenchInputs`); a stonecutter's is its stone chunks |
| Meal store | `meals:*` | The planned meal closet whole, a 2x2 in the freezer at its dining door, or one cell beside the dining table off the chairs (the one store sited from a built fact, the table); each from plan time | Critical | Prepared meals |
| Freezer shelves | `rawmeat:*`, `rawveg:*`, `corpses:*`, `perishables:*` | Three 2x2 shelves in the planned freezer (raw meat and raw vegetables nearest the kitchen door, carcasses by the butchery door) and the perishables cover over the rest, all declared by the Food department from plan time on open ground (a rock or unseen interior defers the create; no room census) | Critical, perishables Preferred | Raw meat, raw vegetables, the corpse larder, the perishables catch-all |
| Medicine store | `medicine:*` | A 2x2 in the planned hospital, nearest the template's bed slots by walking distance (nearest the door without slots), from plan time | Important | Medicine |
| Tomb | `tomb:*` | The tomb room | Critical | Tomb corpses |
| Gear | `apparel`, `weapons` | A 3x3 patch each inside the first planned warehouse, which leaves the cells out (weapons: clear of every prison) | Preferred | Clothing, and weapons with armor |

Low priority on the warehouse and yard is deliberate: the higher-priority
workstation and medicine stockpiles draw their items first, and
hauling (which RimWorld owns) moves the rest to the warehouse or yard.

Rotten things and waste have no store of their own (#3232): they haul to the
materials yard. Fresh animal corpses use the freezer shelf and the butchery. Zones of the retired `dump:*` roles
in older saves are deleted.

## Gear zones

Gear has no rooms of its own: the Military department declares an `apparel` and a `weapons` store as fixed
patches of the warehouse (`policy.GearPatchesOf`) and the warehouse zone leaves their cells out. A prison
within reach of weapons is a hazard, so the weapons patch takes no cell within the prison weapon clearance
(six cells of any prison cell, walls included); a warehouse with no such square hosts no weapons zone and the
next warehouse is tried. A full gear zone grows by the storage capacity options (#3227), not by a gear
trigger. A catalog naming no armor def fails the stockpile review with `NoArmorDefsException`. Saves
holding `armory:` or `wardrobe:` zones or rooms migrate by the replan: no store claims them, and their
stock drains by vanilla hauling.

## Headroom

Storage rooms are not asked for by a fill reading: the layout graph sizes the
storage and cold-store nodes from held stock (stacks over the station's slots),
and an outgrown room gets a successor ([facilities](facilities.md#station-plan-and-successor-rooms)).
A store's capacity and use are counted in stacks, from the native per-cell
`storage_stacks` and `storage_capacity` grid columns: one stack per zone cell,
plus `maxItemsInCell` (3) per cell of each built shelf serving the zone (a
shelf's cells leave the zone natively, so they are added back). A cell whose
count is unknown leaves the store's count unknown, never 0.

`policy.StoreCapacity` reports a declared store's facts: capacity and used
stacks over the zones serving it, net inflow (below), stranded stacks (below)
and its runway, free stacks over net inflow in days (known infinite when inflow
is not positive, unknown while inflow or a count is). An option's lead time
(`StorageLeadDays`) is its build work at base construction speed (one work tick
per game tick) plus `MaintenanceHorizonDays` (5 days); unknown work leaves the
option unvalued. A further yard builds nothing, so its lead time is the horizon.

The yard asks layout for a further room (`policy.YardDemand`, `RoomDemand.Yard`:
one more than the declared yards) once every yard's zone stands and either a
yard has stranded stacks (the "now" signal) or the yards' runway (their free
stacks summed over their inflow summed) is shorter than that lead time. A
small colony filling its yard in a day asks long before a large one filling it
in a month at the same fill. Unknown stays unknown: an unread inflow with no
stranded stock asks nothing, and a draining yard never asks. The ask needs the
loot census and the occupancy ring, so it is decided over the stockpile
request rather than the department's `StoreView`. The new room's zone is
created once it is planned and open.

### Stranded stock

A stack is stranded when it can be hauled nowhere now: the loot census reads it
unforbidden and safe with zero `StorageHeadroom`. For each such row (and each
zero-headroom row of unknown safety) native names the player's storage groups,
stockpile zones and storage buildings by load id, whose settings accept the item,
ignoring space (`LootItem.AcceptedBy`). `policy.StrandedStacks` counts a
store's stranded stacks over the zones serving it and the shelves in them. It
is unknown, never zero, while the census is unread, while any unforbidden row's
headroom is unknown, or when a zero-headroom row of unknown safety names the
store. The yard's further-room ask reads it.

### Store inflow

Native keeps a saved occupancy ring (`StorageOccupancyState`, 15 days of 2500-tick
hours): at each hour's first tick it records the item stacks every player storage
group on a home map holds, zero included. A group created mid-window starts its own
samples; a deleted one stops and ages out. The stockpile request reads the samples
of its zones and built shelves over the inflow window
(`observations_read_storage_occupancy`, `StockpileRequest.Occupancy`).
`policy.StoreInflow` is a store's net inflow in stacks per day: the sum over its
zones and their shelves of each group's least-squares slope of held stacks over the
last `StoreInflowWindowDays` (3). A negative inflow is a known reading (the store
drains). It is unknown, never zero, while the ring is unread, when the store has no
group, or while any of its groups has under a day of samples. The yard runway
reads it.

### Shelves

Every declared, non-retired store whose filter a shelf can hold is a shelf
candidate (`policy.ShelfCandidates`, on the review as `ShelfCandidates`): the
yard (unroofed shelves are allowed), bench ingredient stores, meal and medicine
stores, and the warehouse zones, gear included. A shelf's fixed settings exclude
chunks, plants and buildings, so a filter allowing only those gets none
(`policy.ShelfHolds`; any preset base, a thing def or another category holds).
A candidate is a 2x1 site inside the store's zone (`policy.ShelfSites`): free
walkable floor, off the layout plan's walkways (`policy.PlannedWalkways`: every
cell a planned route crosses, since a shelf costs 50 to path over and is
pass-through only) and not beside a doorway, leaving the zone contiguous. Each
reports its effect in stacks: the catalog Shelf's item slots less the floor
stacks its cells give up (6 - 2 = +4); unknown when either count is. While the
walkways are unknown (plan unread, or its routes fail) there is no candidate.
Candidates are generation only. The one-at-a-time warehouse shelf
(`NextWarehouseShelf`, one open shelf, at most a third of the footprint) still
chooses what is built until the storage options portfolio (#3239) replaces it.

### Storage options

`policy.StorageOptions` (on the review as `Options`) reports, for every
standing, non-retired store, its capacity facts (`StoreCapacity`) and its
costed options. Cost and effect are reported apart, with no conversion into
one value, and the report ranks nothing:

| Option | Cost | Effect |
|---|---|---|
| Zone growth | no material, 0 work | the open footprint cells the zone can still reach (`StoreGrowth`, as the review's grow edit reads them); one stack per cell |
| Shelf | the catalog Shelf's cost list from its build stuff (`BuildStuff`) and that stuff's `WorkToBuild` | one per shelf candidate: its 2 floor cells lost, its stacks |
| Successor room (warehouse) | the plan's owed material for a planned storage room no warehouse zone stands in (`PlannedOwedCost`); work is its planned sites' catalog `WorkToBuild` times pieces over the first stuff's full alternative (`PlannedRoomWork`), the roof left out and named (`room_roof_work_unassessed`) | its interior cells and stacks |
| Further room (yard) | a planned yard no yard zone stands in: no material, 0 work | its interior cells and stacks |

Each option's lead time is `StorageLeadDays` over its work. With no planned
room waiting, the room option is unsized: every value unknown, finding
`room_unsized`. Deterioration avoided is HP per day over the store's stranded
stacks only: the worst rows first (the def's `DeteriorationRate`, from
`ItemFacts`, times the exposure of the cell it lies on: 0.5 unroofed plus 0.5
not indoors), up to the stacks gained, each less the exposure where the option
puts it (0 on a shelf, the added cells' for zone growth, none in a roofed room
and open air in a yard). A shelf skips Chunks, Plants and Buildings rows. The
yard preset admits no deteriorating item a shelf holds, so a yard shelf reads a
known 0. A stuffed item def has a rate only when every stuff gives it the same
one (a loot row names no stuff). Any unknown value stays unknown, never zero,
and names a finding (`work_unknown`, `room_work_unknown`, `room_roof_work_unassessed`,
`deterioration_unknown`, ...): such an option is not ranked on a guess. The
placed options (zone growth, shelf) feed the storage portfolio (#3239); the
room options reach layout through the yard's runway and stranded signal and the
station plan's successor rooms.

## Planned rooms

Layout plans the storage rooms;
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

## Demand-driven delivery

The [support portfolio](../contracts/support-work.md#delivery-and-chunks) proposes
delivery quantities from outstanding native bill/material demand and compares
participant travel, occupancy and displaced work. A small refill is not withheld
until an arbitrary full stack fits. Native storage filters, reservations and
destination legality still constrain exact delivery. Changing the demand releases
obsolete obligations through the existing Method lifecycle.

Creating a store does not prove a supply need or completed hauling. Chunks enter
the supported acquisition path only for an actual material requirement; no global
chunk definition mutation makes unrelated chunks automatically haulable. The
`storage/refill-hysteresis` case name remains, but its assertions now cover
demand-bounded delivery; `storage/changed-demand` covers withdrawal.
