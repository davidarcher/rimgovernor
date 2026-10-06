# Weapon planner

[Documentation](../../README.md) · [Equipment and apparel upkeep](equipment-upkeep.md) · [Apparel policy operation](apparel-policy.md)

`policy.AssignEquip` (the routine equip family) scores every pawn/weapon pair, solves the colony
assignment once, and admits the whole wave as one plan. Inputs: the loadout model's primary slot and
its [roles](equipment-upkeep.md#loadout-model). Outputs: the `Equip` pawn-target order and the
`pawn_equipped` postcondition.

## Scoring

Skill, optional combat role, nominal weapon throughput/range and known raid armor shape the score.
Precision rifles favor accurate shooters; short burst weapons favor novices. Brawlers and
Shooting-disabled pawns receive melee; Violent-disabled pawns receive nothing. Area-fire weapons need an
explicit lone-fighter input. Unknown roles and armor are neutral. The Core definition table uses
planning estimates, with conservative class defaults for other defs.

## Assignment

- Pairs are assigned highest score first, then pawn identity, distance and weapon identity; each pawn
  and weapon appears once.
- The planner owns every weapon decision: a pawn's weapon swaps for any loose weapon that scores
  strictly higher. A known biocode restricts the weapon to its pawn; biocoded primaries stay pinned,
  even if automation equipped them, and coded weapons with a lost owner stay unavailable.
- A colonist held back from arms (`EquipCandidatePawn.NoArms`: a creepjoiner whose downside has not
  shown, see [population concerns](population-contracts.md#creepjoiners)) scores zero for every weapon, is
  not counted as an unarmed fighter and is never assigned, swapped or loaded out. When every colonist
  left is held back the equip step refuses with `no_worker` naming the colonist and
  `creepjoiner_downside_unrevealed`.
- The assignment is admitted as independent actions in one plan, keeping per-pawn retry limits and
  native postconditions. Native preview still decides current equip eligibility, including biocoding.

## Inputs

- `WeaponProductionDemand` supplies definition/count demand to the bill batch, net of assigned loose
  weapons and limited to discovered available recipes. Optional roles come from the loadout model.
- A colonist working Hunting is a hunter (`WeaponRoleHunter`): only a hunting weapon scores for them
  (`WeaponDef.Hunts`: ranged, a plain projectile, any range, the same rule as the hunt gate). A hunter
  without one (unarmed, melee, or a flame or explosive primary) is demand like an unarmed fighter,
  returned apart from the fighters' demand because food owns that craft: while `EnsureFoodSupply` is
  open and unmet the bill is that Standard's Method, otherwise it is `MaintainEquipment`'s. A hunter's
  upgrade of an adequate weapon stays an ordinary fighter's.
- Native gear items carry a biocoded flag and, when retained, their owner's pawn ID. Supply weapon
  details use exact item identities.
- Combat pawn reads carry the map's mean peak sharp armor among live, standing hostile pawns (natural
  armor or strongest worn layer). No hostiles leaves raid armor unknown; producers may omit these
  optional facts.

## Dispatch and completion

`pawn_equipped` weapon orders use the same passive completion recovery as other equip orders. The
pre-write record retains observation time, load and plan revision even when the native reply is lost.
A later exact weapon observation can complete a blocked order without resending it. Changed context,
cancelled work, unknown pawn health and a different equipped item cannot clear the hold.

## Acceptance

The weapon fit replays in `go/internal/policy/gear_snapshot_test.go` over a recorded `AssignEquip`
input: with a bolt-action rifle and a pump shotgun loose, the Shooting 12 soldier gets the rifle and
the Shooting 8 one the shotgun. The armor ladder half has no recording yet (the recorded census
carried no loadout model). See [equipment upkeep](equipment-upkeep.md#acceptance).
