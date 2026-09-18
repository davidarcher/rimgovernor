# Animal husbandry contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`MaintainHerd` creates a persistent, player-owned `MaintainHerd-<race>` goal in
ColonyPlan. It accepts an observed native race, native training targets and the
operator's herd policy (`RoutinePolicy`, set by `rimgovernor serve` flags):

| Field | Flag | Effect |
| --- | --- | --- |
| `HerdPopulationMin` | `--routine-herd-population-min RACE:MIN` | Below the floor, designate the lowest-ID tameable wild animal of that race (`tame`) while the herd's feed forecast reports no shortfall. |
| `HerdPopulationMax` | `--routine-herd-population-max RACE:MAX` | Above the ceiling, remove the lowest-ID eligible surplus animal — only with one of the two opt-ins below. |
| `AllowRelease` | `--routine-allow-release` | Remove surplus by release-to-wild (`release`); preferred when both opt-ins are set. |
| `AllowSlaughter` | `--routine-allow-slaughter` | Remove surplus by slaughter (`slaughter`). |

All default off. A ceiling alone never removes an animal; a floor alone does
propose taming, gated on `MaintainAnimalFeed`'s own review: while any player
animal is below its feed threshold, or the feed forecast is unknown, no tame is
proposed and the shortfall is not counted as a herd deficit (the wild animal's
own appetite is not forecast; the gate only refuses to add a mouth to a herd
already short). A race in both maps must have minimum ≤ maximum. There is no
per-race protected-ID list, breeder-pair reserve or feed-reserve bookkeeping:
eligibility relies on native's own `SafeToSlaughter`, `SafeToRelease` and
`Tameable` facts.

## Methods

Every method is one direct settings write on one exact animal through the same
preview → CAS token → dispatch path, so admission is the effect; native handler
labor afterwards (taming, walking an animal off-map, training steps) is observed
through the animal's own state, never inferred from the receipt.

| Method | Target | Native write | Completed when |
| --- | --- | --- | --- |
| `train` | player animal | `SetWantedRecursive` | trainable still wanted (learned state tracked separately) |
| `slaughter` | player animal | `Slaughter` designation | designation present |
| `release` | player animal | `ReleaseAnimalToWild` designation | designation present |
| `tame` | wild animal | `Tame` designation | designation present, or the animal now reads as a player animal (the taming job consumed it) |
| `allowed_area` | player animal | `AreaRestrictionInPawnCurrentMap` (argument: area id, empty clears) | animal's allowed area reads back equal |
| `master` | player animal | `PlayerSettings.Master` (argument: colonist id, empty clears) | master reads back equal |
| `follow_drafted` | player animal | `followDrafted` (argument: `true`/`false`) | flag reads back equal |
| `follow_fieldwork` | player animal | `followFieldwork` (argument: `true`/`false`) | flag reads back equal |

Admission facts: `allowed_area` needs `SupportsAllowedAreas`; `master` and the
follow flags need `Obedient` (learned Obedience; native refuses otherwise). An
area or master id the map does not carry is refused as not found. Each follow
method writes only its own flag.

Selection order each cycle is train, then tame, then surplus removal; one write
per cycle. No routine planner yet produces the settings methods; they are
available to any planner through the same husbandry action. Pen containment is not a husbandry method: pens are built by
`MaintainAnimalContainment` and native handlers rope pen animals into any
suitable pen on their own.

## Ownership and population

The goal belongs to the colony and map where it was accepted. Each native request
also binds the load token, exact animal ID, animal settings token and herd census.
Hands previews and dispatches through the normal writer lock and direction guards.
The native callback requires a paused matching context and rejects changed settings
or population. Unconfirmed requests remain blocked for inspection, without retry.

Surplus and shortfall counts subtract animals already designated for
slaughter or release and add wild animals already designated for taming, so a
pending write is never duplicated. Native eligibility is checked again at
dispatch: bonded, mastered, pregnant, downed and already-designated animals are
excluded from removal by RimWorld's own designator rules plus the master/bond
exclusions `SafeToSlaughter`/`SafeToRelease` add; a tame candidate must pass
`TameUtility.CanTame` and carry no tame or hunt designation. Masters, allowed
areas, following, sterilization and breeding separation are ordinary game
settings: while native authority reads Auto the controller may change any of
them, including ones the player just set (see the working agreement). Masters,
areas and following are written through the methods above; sterilization is
not yet written.

Births are never counted as pending: a shortfall with no tameable wild animal
of the race on the map simply reports no candidate until one appears. Renewing
a target cancels its uncompleted controller steps; cancellation leaves
previously issued game orders in place, following the shared goal cancellation
contract.

## Training and products

Training requests use native `CanAssignToTrain` and `SetWantedRecursive`; native
`learned` and step counts track progress separately from settings receipts. The
per-animal settings token and herd census token are compare-and-swap guards
against stale in-flight snapshots: they are re-read immediately before preview
and dispatch, and a mismatch (player edit, birth, death) fails that attempt so
the next cycle plans from fresh state. They never block the controller from
changing an animal again. Removal-designated animals receive no training changes.

Handling joins shared deterministic work allocation with the observed native minimum
skill. Player work overrides remain authoritative. The herd observation retains safe
handler reachability, current priorities, jobs and targets, alongside training target
count and ready product count. Normal native handlers perform training, milking and
shearing. No instant training, forced product generation or alternative job executor
exists. A full udder or wool comp is pending work, not a collected product.

## Feed capacity and containment

Reserve days are a planning horizon supplied by the player, including seasonal needs.
The shared food forecast allocates current stored food among native eligible eaters
using diet, policy, safe reachability, held stock and rot deadlines. Missing reads,
grass, expected harvest and unborn animals cannot certify feed capacity. Animal pen
membership uses the native enclosed, suitable pen lookup; a marker by itself is not
containment. Area-managed animals use their current allowed-area membership.

An explicit feed resource, or observed feed used exclusively by animals, creates an
owned `MaintainResource` goal through the existing acquisition/production methods.
The stock target uses native per-item nutrition and demand from competing eligible
eaters. Missing suitable feed, production prerequisites, handlers, access or storage
remain visible blockers. Cancelling the herd stops its unissued feed work. Acquiring
stock does not establish that animals can reach or have consumed it.

These are current-condition projections. Future births, changing temperatures,
spoilage and native job selection require fresh review; a bounded acceptance run
does not establish indefinite herd sustainability.

See husbandry acceptance for the native fixture
and the distinction between setup, orders and pawn outcomes.
