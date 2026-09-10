# Waste containment contracts

[Documentation](../README.md) · [Plans and Hands](../explanation/plans-and-hands.md)

`MaintainWaste` is a maintained priority-3 colony goal. Fresh native colony facts
activate it for exposed spoiled goods and rotten anonymous animal corpses. Emergency
goals preempt it. Once the observed deficit clears, a later deficit reopens the goal.
Cancelling the goal preserves issued game orders and suppresses further maintenance.

The player can use `CreateGoal` with `goal: MaintainWaste` and two optional lists:

| Field | Authority |
| --- | --- |
| `unwanted` | Exact observed item IDs explicitly unwanted by the player. Enables relocation; does not authorize destruction. |
| `bury` | Exact corpse IDs explicitly selected for burial. Named, human and colony corpses require this authority. Relocation cannot satisfy burial. |

Both lists are bounded to 64 identities. Admission reads native state and rejects
missing or protected targets. Replacing policy requires observing or cancelling
pending work. Explicitly renewing a blocked goal retains old receipts and starts a
new method generation; it never silently retries an uncertain write.

`home/waste_state` returns current-map item identities, native rot stage, protection
reason, position and containment. Grave occupants remain visible as protected
`buried` bodies. Held possessions and fogged targets are never hauling candidates.
Forbidden items, quest-tagged objects, packed buildings, and native dissolution,
gas-release or explosive hazards are protected. Hazardous waste requires specialized
containment; ordinary outdoor dumping cannot satisfy that requirement.

`home/manage_waste` previews or issues one native hauling WorkGiver job. It refuses
unavailable, drafted or player-directed pawns, disabled hauling, unsafe paths,
reservations and incompatible native filters. It preserves existing storage settings
and all zones. A relocation destination must be an accepting outdoor stockpile,
unroofed, outside the home area and at least 12 cells from colony buildings. Burial
uses a native-approved empty grave. Native WorkGivers retain pawn eligibility,
preferences, reservations and grave acceptance rules. Missing facilities, capacity
or eligible workers produce explicit blockers instead of changing player facilities.

Methods preview at most eight pawn/target combinations per review and commit one
haul through the shared plan and Hands. Whole-stack jobs that would merge into an
existing destination stack are refused because identity loss cannot prove delivery.
This method does not create storage, graves or bills and does not delete items.

`waste_contained` completion waits for a later native tick and the exact item's
observed containment. `relocated` is temporary storage; `buried` is completed burial.
An issued order, a carried body, an absent item, a merged stack or elapsed ticks are
not completion. Load and player-direction changes invalidate pending authority.
Interrupted or uncertain work retains its evidence for inspection before replacement.

See [native waste acceptance](../how-to/waste-management.md) for test scope and inputs.
