# Apparel policy operation

[Documentation](../../README.md) · [Equipment and apparel upkeep](equipment-upkeep.md) · [Weapon planner](weapon-planner.md)

`SetApparelPolicy` (`NativeApparelPolicyOperations`) is the operation through
which `MaintainEquipment` keeps one named `RimGovernor <role>` apparel policy per
[loadout role](equipment-upkeep.md#loadout-model) and assigns it to each pawn
before selecting individual wear or production work. It is what lets vanilla's
own apparel optimizer do the many-pawn dressing between reviews, and it retires
tainted raid drops without a wear order.

The gear planner configures a named `RimGovernor <role>` apparel policy through
`SetApparelPolicy` before selecting individual wear or production work. Autonomous
control replaces manual policy assignments and clears forced/locked apparel;
vanilla optimizes apparel between reviews. Worker, hunter, indoor, slave and
non-combatant policies exclude armor; soldiers allow it; children use native
child-compatible definitions. Every role excludes tainted apparel, admits
51-100% hit points and Awful-Legendary quality. Policy filters update in place
and assignments use CAS preview/admission with native postcondition readback.

`GearSnapshot.apparel_policy` carries, per pawn, the policy's token (its CAS
precondition), name, allowed definitions, hit-point and quality ranges, whether
it excludes tainted apparel and whether a manual assignment or forced/locked
apparel overrides it, beside the discovered stage-compatible definitions with
their armor and child/adult flags. `policy.DesiredApparelPolicy` compares the
pawn's current policy with the role's specification and raises an
`apparel_policy` action only on a difference.

## Acceptance

`production/apparel-policy` is a short native smoke case for create/update/assign,
stale CAS refusal, manual-policy override and clearing forced/locked apparel.
`gear/tainted` (#468) is the area case: a tainted parka beside a clean one,
nobody wears it and every colonist ends on a role policy, the worker policy
among them. See [equipment upkeep](equipment-upkeep.md#acceptance).
