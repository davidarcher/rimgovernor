# Waste containment contracts

[Documentation](../../README.md) · [Plans and Hands](../architecture/plans-and-hands.md)

`MaintainWaste` is a maintained priority-3 colony concern. Fresh native colony facts activate it for
exposed spoiled goods and rotten anonymous animal corpses. Emergency concerns preempt it; once the
observed deficit clears, a later deficit reopens it.

The concern accepts two optional lists, each bounded to 64 identities:

| Field | Authority |
| --- | --- |
| `unwanted` | Exact observed item IDs the player marked unwanted. Enables relocation; does not authorize destruction. |
| `bury` | Exact corpse IDs selected for burial. Named, human and colony corpses require this authority. Relocation cannot satisfy burial. |

Admission reads native state and rejects missing or protected targets. Replacing policy requires
observing or cancelling pending work. Explicitly renewing a blocked concern keeps old receipts and starts
a new method generation; it never silently retries an uncertain write.

## Read

`Observations.ReadWaste` returns current-map item identities, native rot stage, protection reason,
position and containment.

- Grave occupants stay visible as protected `buried` bodies.
- Held possessions and fogged targets are never hauling candidates.
- Protected: forbidden items, quest-tagged objects, packed buildings, and native dissolution,
  gas-release or explosive hazards. Hazardous waste needs specialized containment; ordinary outdoor
  dumping cannot satisfy it.

## Hauling

`GiveJobIntent` HaulWaste on Actions/Apply issues one native hauling WorkGiver job; applied means
ordered, and the next waste census reads where the item is.

- Refused: unavailable, drafted or player-directed pawns, disabled hauling, unsafe paths, reservations
  and incompatible native filters. Existing storage settings and all zones are preserved.
- Relocation destination: an accepting outdoor stockpile, unroofed, outside the home area and at least
  12 cells from colony buildings. Burial uses a native-approved empty grave.
- Native WorkGivers keep pawn eligibility, preferences, reservations and grave acceptance rules.
  Missing facilities, capacity or eligible workers produce explicit blockers instead of changing player
  facilities.
- Methods preview at most eight pawn/target combinations per review and commit one haul through the
  shared plan and Hands. Whole-stack jobs that would merge into an existing destination stack are
  refused because identity loss cannot prove delivery. The method creates no storage, graves or bills
  and deletes no items.

## Completion

`waste_contained` waits for a later native tick and the exact item's observed containment. `relocated`
is temporary storage; `buried` is completed burial. An issued order, a carried body, an absent item, a
merged stack or elapsed ticks are not completion. Load and player-direction changes invalidate pending
authority. Interrupted or uncertain work keeps its evidence for inspection before replacement.

See native waste acceptance for test scope and inputs.
