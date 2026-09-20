# Weapon planner

[Documentation](../../README.md) · [Equipment and apparel upkeep](equipment-upkeep.md) · [Apparel policy operation](apparel-policy.md)

The weapon planner (`policy.AssignEquip`, the routine equip family) replaces
nearest-weapon-first arming with a colony assignment: every pawn/weapon pair is
scored, the assignment is solved once, and the equip family admits the whole
wave as one plan. The loadout model's primary slot and its
[roles](equipment-upkeep.md#loadout-model) are its inputs; the `Equip`
pawn-target order and the `pawn_equipped` postcondition are its outputs.

Weapon planning scores the colony's pawn/weapon pairs before assigning any
weapon. Skill, optional combat role, nominal weapon throughput/range and known
raid armor shape the score. Precision rifles favor accurate shooters; short
burst weapons favor novices. Brawlers and Shooting-disabled pawns receive melee;
Violent-disabled pawns receive nothing. Area-fire weapons require an explicit
lone-fighter input. Unknown roles and armor are neutral. The Core definition
table uses planning estimates, with conservative class defaults for other defs.

Pairs are assigned highest score first, then pawn identity, distance and weapon
identity; each pawn and weapon appears once. A known biocode restricts the weapon
to its pawn. An existing weapon is preserved unless automation owns its exact
identity, and a swap requires over 20% score improvement. Biocoded primaries stay
pinned. The routine equip planner admits the entire assignment as independent
actions in one plan, retaining per-pawn retry limits and native postconditions.
Native preview still decides current equip eligibility, including biocoding.

`WeaponProductionDemand` supplies definition/count demand to the bill batch
(#469), net of assigned loose weapons and limited to discovered available
recipes. Optional roles are supplied by the loadout model (#466). Native gear
items carry a biocoded flag and, when retained, their owner's pawn ID. Supply
weapon details use exact item identities. Coded weapons with a lost owner
remain unavailable, and coded primaries stay pinned even if automation equipped
them. Combat pawn reads carry the map's mean peak sharp armor among live,
standing hostile pawns (natural armor or strongest worn layer). No hostiles
leaves raid armor unknown; older producers may omit these optional facts.

## Dispatch and completion

Ordinary `pawn_equipped` weapon orders use the same passive completion recovery.
The pre-write record retains observation time, load and plan revision even
when the native reply is lost. A later exact weapon observation can complete a
blocked order without sending it again. Changed context, cancelled work, unknown
pawn health and a different equipped item cannot clear the hold.

## Acceptance

`gear/soldier` (#470, #471) is the area case: two marksmen at Shooting 12 and 8
with a bolt-action rifle and a pump shotgun loose end holding the rifle and the
shotgun respectively, in flak vests and helmets. See
[equipment upkeep](equipment-upkeep.md#acceptance).
