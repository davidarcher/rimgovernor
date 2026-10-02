# Apparel policy operation

[Documentation](../../README.md) · [Equipment and apparel upkeep](equipment-upkeep.md) · [Weapon planner](weapon-planner.md)

`ApparelPolicyIntent` on Actions/Apply (`NativeApparelPolicyOperations`) is the intent through
which `MaintainEquipment` keeps one outfit per pawn, labelled with the pawn's
short name (#1302), before selecting individual wear or production work. It is
what lets vanilla's own apparel optimizer do the many-pawn dressing between
reviews, and it retires tainted raid drops without a wear order.

The outfit's filter is the pawn's [loadout role](equipment-upkeep.md#loadout-model)
base plus its hard requirements:

- Worker, hunter, indoor, slave and non-combatant outfits exclude combat armor;
  soldiers allow it; children use child-compatible definitions.
- Every definition the pawn's royal title, ideoligion role or ideoligion apparel
  precepts require is allowed, whatever the role says.
- A nude pawn (nudist trait, or a mandatory-nudity precept for its gender)
  gets nothing covering torso or legs unless required.
- Only definitions the pawn can wear (body parts, body type, genes, stage,
  gender) are listed, so no other is ever allowed.
- Tainted apparel is excluded; hit points 51-100% (swap before the tattered
  thought); the quality floor rises Awful -> Normal -> Good while every slot
  the pawn wears still has a garment at that quality worn or on hand.

Pawns still choose by temperature inside the filter. Autonomous control
replaces manual assignments and clears forced/locked apparel. It is an
intent-mode kind: native finds the pawn's outfit through its assignment (its
current outfit when no other pawn holds it, else an unheld outfit carrying its
name, else a new one), relabels it on a rename, applies a matching outfit as a
no-op and reads the filter back; applied is terminal, refused replans.

Once every pawn is on its own outfit, the gear planner prunes every other
outfit, vanilla and player-made included, with a
[`PolicyPruneIntent`](action-contracts.md) on the outfit database; the owed
prune keeps `MaintainEquipment` in deficit until it lands.

`GearSnapshot.apparel_policy` carries, per pawn, the outfit's token, load id,
label, allowed definitions, hit-point and quality ranges, whether it excludes
tainted apparel and whether forced/locked apparel overrides it; the pawn's
short name, required definitions and nudity; and the definitions it can wear
with their armor, child/adult and torso-or-legs flags.
`policy.DesiredApparelPolicy` compares the current outfit with the
specification and raises an `apparel_policy` action only on a difference.

## Acceptance

`production/apparel-policy` is a short native smoke case for create/update/assign,
reapply, manual-policy override and clearing forced/locked apparel;
`production/per-pawn-outfits` is the end-to-end signal that every lab colonist
ends on its own outfit and the rest are pruned. The filters are unit tests in
`go/internal/policy/apparel_policy_test.go`; the tainted-apparel decision
(#468) replays from colony snapshots in `go/internal/policy/gear_snapshot_test.go`.
See [equipment upkeep](equipment-upkeep.md#acceptance).
