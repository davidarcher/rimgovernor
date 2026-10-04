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
| Warehouse | `general` | Inside the standing storage room; starts as a free square of the opening store's size, else a 2x2 patch, and grows by the fill rule onto roofed cells | Low | Everything the `indoor_only` preset allows: storable items minus those the game data marks safe outside |
| Materials yard | `yard` | Unroofed ground inside the inner (core ring) enclosure, nearest the standing workshop by walking distance | Low | The `outdoor_safe` preset: items that neither spoil nor deteriorate outdoors, derived from the native item catalog, never a list in Go |
| Workstation stockpiles | `ingredients:*` | A free roofed 2x2 patch in the bench's room nearest the bench, one per bench with an active bill (the kitchen and butcher excepted) | Important | That bench's recipe ingredients (`policy.DeriveBenchInputs`); a stonecutter's is its stone chunks |
| Meal store | `meals:*` | The dining room's meal closet, the freezer's door, or beside the dining table | Critical | Prepared meals |
| Freezer shelves | `rawmeat:*`, `rawveg:*`, `corpses:*`, `perishables:*` | The freezer | Critical, perishables Preferred | Raw meat, raw vegetables, the corpse larder, the perishables catch-all |
| Medicine store | `medicine:*` | The hospital room with the most medical beds, nearest those beds | Critical | Medicine |
| Food store | `food` | Beside the kitchen | Preferred | Food, until a roofed food store stands |
| Tomb | `tomb:*` | The tomb room | Critical | Tomb corpses |
| Gear | `apparel`, `weapons` | The storage room (apparel) and barracks (weapons) | Preferred | Clothing and armor, weapons |
| Dumps | `dump:worn`, `dump:rotten`, `dump:corpses`, `dump:fresh` | A free outdoor 2x2 patch clear of living rooms, nearest the warehouse; sited only while something waits for it (worn-out apparel, spoiled items and rotting animal carcasses, human corpses, fresh animal carcasses while no freezer shelf stands) | Low | Worn gear, rotten items, corpses, fresh animal corpses |

Low priority on the warehouse and yard is deliberate: the higher-priority
workstation, medicine and food stockpiles draw their items first, and
hauling (which RimWorld owns) moves the rest to the warehouse or yard.

A dump's site room is the outdoor ground at least six cells from every
living room, so a dump a new bedroom or kitchen crowds is deleted and sited
again. The opening corpse dump (no planner yet) stands from the first pass.

## Gear rooms

When every warehouse zone is full and cannot grow in its room (the state that
also asks for a further storage room), the planner raises `RoomDemand` for each
kind of serviceable gear the colony holds (armory for weapons and armor,
wardrobe for clothing) and layout adds the room: the armory beside the
barracks, the wardrobe beside the workshop with the tailor bench, with no
wealth gate and no item-count threshold. A planned gear room not yet standing
holds back the further storage room. Layout keeps the armory and every prison
out of each other's weapon clearance; a standing armory with no free cell clear
of a prison is `ErrArmoryNearPrison` on `StoragePlan.Err`, logged by the
reviewer. A catalog naming no armor def fails the stockpile review with
`ErrNoArmorDefs`.

## Growth, shrink and churn

A zone grows onto adjacent open cells once 85% of its cells hold things and
sheds empty edge cells after sitting at or under 25% for a day (never below
four cells); the warehouse grows onto roofed cells only. Same-filter
fragments merge. Edits are admitted within a per-cycle haul budget so a zone
move never floods the colonists with hauling. A role whose site moved gets its
new zone first; the old zone is deleted in the same review only once that create
is admitted, and its items rehome. Held building reservations and the unroofed
floor of a planned shell are protected ground no site takes.

## Review check

The [colony review](../testing/colony-review.md) reports the storage state
from the colony census (`/api/player/colony`: owned zones per role kind and
whether starting supplies are still forbidden). It lists the final zone count
per role, flags any hour a role's zone count falls (a zone deleted or merged:
churn) and flags starting supplies still forbidden after a day. A report, not
a gate.
