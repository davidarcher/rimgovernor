# Apparel policy operation

[Documentation](../../README.md) · [Equipment and apparel upkeep](equipment-upkeep.md) · [Weapon planner](weapon-planner.md)

`ApparelPolicyIntent` on Actions/Apply (`NativeApparelPolicyOperations`) is how
the `military/equipment` Need keeps one outfit per pawn, labelled with the pawn's short
name, before selecting individual wear or production work. It lets vanilla's
apparel optimizer do the many-pawn dressing between reviews and, where the outfit
disallows them, retires tainted raid drops without a wear order.

The outfit's filter is the pawn's [loadout role](equipment-upkeep.md#loadout-model)
base plus its hard requirements:

- Worker, hunter, indoor, slave and non-combatant outfits exclude combat armor;
  soldiers allow it; children use child-compatible definitions.
- Every definition the pawn's royal title, ideoligion role or ideoligion apparel
  precepts require is allowed, whatever the role says.
- A nude pawn (nudist trait, or a mandatory-nudity precept for its gender) gets
  nothing covering torso or legs unless required.
- Only definitions the pawn can wear (body parts, body type, genes, stage,
  gender) are listed.
- Tainted apparel is excluded unless `allowTainted` is set: the coupled
  comparator rejected disallowing it for this pawn
  ([tainted apparel](equipment-upkeep.md#tainted-apparel-and-warmth)); hit points 51-100%
  (swap before the tattered thought); the quality floor rises Awful -> Normal ->
  Good while every slot the pawn wears still has an allowed garment at that
  quality worn or on hand.

Pawns still choose by temperature inside the filter; the policy writes no
insulation rule. Warmth is the comparator's insulation floor on the gear planner
(#3329), which acts through equip candidates and bills, not through the filter. Autonomous control replaces
manual assignments and clears forced/locked apparel. It is an intent-mode kind:
native finds the pawn's outfit through its assignment (its current outfit when no
other pawn holds it, else an unheld outfit carrying its name, else a new one),
relabels it on a rename, applies a matching outfit as a no-op and reads the filter
back; applied is terminal, refused replans.

Once every pawn is on its own outfit, the gear planner prunes every other outfit
(vanilla and player-made) with a [`PolicyPruneIntent`](action-contracts.md) on the
outfit database; the prune is one Method claimed by `policy/Outfit`, proposed only while no pawn's
policy is, and lands under the equipment Need.

`GearSnapshot.apparel_policy` carries, per pawn: the outfit's token, load id,
label, allowed definitions, hit-point and quality ranges, whether it excludes
tainted apparel and whether forced/locked apparel overrides it; the pawn's short
name, required definitions and nudity; and the definitions it can wear with their
armor, child/adult and torso-or-legs flags. `policy.DesiredApparelPolicy` compares
the current outfit with the specification and raises an `apparel_policy` action
only on a difference.

The outfit filter also prices the wardrobe: the allowed definitions covering
the core groups (`RoleApparelDefinitions`) are the garments whose recipes set
the standing [clothing material demand](equipment-upkeep.md#clothing-material-demand).

## Acceptance

`production/apparel-policy` is a short native smoke case (create/update/assign,
reapply, manual-policy override, clearing forced/locked apparel), read back
through the colony-facts gear section. `apparel/tainted_filter` (#3285) is the
end-to-end signal for the tainted-apparel choice: a content colonist in a cold
snap whose only parka is tainted ends on its own outfit with tainted apparel
allowed. The choice itself is covered by domain parity tests
(`ForecastCoupledSelectionTests`, `EffectSourceCandidatesTests`).
